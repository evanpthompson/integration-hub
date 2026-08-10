"""Pure worker logic: fetch, classify, transform.

Deliberately free of gRPC so every branch here is testable without a server.
server.py is the only module that knows protobuf exists.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Any

import httpx
import jmespath


class TransformError(Exception):
    """The upstream responded fine, but its shape did not survive the transform."""


@dataclass
class FetchOutcome:
    ok: bool
    status: int = 0
    body: Any = None
    error_code: str = ""
    error_message: str = ""
    retryable: bool = False
    duration_ms: int = 0
    headers: dict[str, str] = field(default_factory=dict)


# 408 Request Timeout and 425 Too Early are the two 4xx codes worth retrying —
# everything else in that range means "you asked wrong", and retrying just burns
# rate limit. 429 is called out separately so the orchestrator can back off on it
# specifically rather than treating it as a generic server fault.
_RETRYABLE_4XX = frozenset({408, 425})


def classify_status(status: int) -> tuple[str, bool]:
    """Map an HTTP status to (error_code, retryable). Only called for non-2xx."""
    if status == 429:
        return "RATE_LIMITED", True
    if status >= 500:
        return "UPSTREAM_5XX", True
    if status in _RETRYABLE_4XX:
        return "UPSTREAM_4XX", True
    return "UPSTREAM_4XX", False


def classify_exception(exc: Exception) -> tuple[str, bool]:
    """Map a transport-level failure to (error_code, retryable)."""
    # TimeoutException is a subclass of TransportError, so it must be checked first.
    if isinstance(exc, httpx.TimeoutException):
        return "TIMEOUT", True
    if isinstance(exc, httpx.TransportError):
        return "CONNECT", True
    return "UNKNOWN", False


def apply_transform(expression: str, body: Any, emit: str, resource: str = "?") -> list[dict]:
    """Run the manifest's JMESPath expression and validate the canonical shape.

    This is the anti-corruption layer: whatever the upstream returned, what comes
    out of here is a list of records that each carry an `id`.
    """
    try:
        result = jmespath.search(expression, body)
    except Exception as exc:  # jmespath raises several unrelated types
        raise TransformError(f"resource '{resource}': JMESPath failed: {exc}") from exc

    if emit == "list":
        if result is None:
            records = []
        elif isinstance(result, list):
            records = result
        else:
            raise TransformError(
                f"resource '{resource}': emit=list but the transform produced "
                f"{type(result).__name__}, not a list"
            )
    else:
        if result is None:
            raise TransformError(
                f"resource '{resource}': emit=single but the transform matched nothing"
            )
        if not isinstance(result, dict):
            raise TransformError(
                f"resource '{resource}': emit=single but the transform produced "
                f"{type(result).__name__}, not an object"
            )
        records = [result]

    for index, record in enumerate(records):
        if not isinstance(record, dict):
            raise TransformError(
                f"resource '{resource}': record {index} is {type(record).__name__}, not an object"
            )
        if record.get("id") in (None, ""):
            raise TransformError(
                f"resource '{resource}': record {index} is missing the required field 'id'"
            )

    return records


def build_url(base_url: str, path: str) -> str:
    return f"{base_url.rstrip('/')}/{path.lstrip('/')}" if path else base_url


async def fetch_rest(
    client: httpx.AsyncClient,
    *,
    base_url: str,
    method: str,
    path: str,
    query: dict[str, str],
    headers: dict[str, str],
    timeout_ms: int,
) -> FetchOutcome:
    """One upstream call. Never raises for an upstream failure — it reports one.

    ponytail: no retry loop here. The worker classifies, the orchestrator decides.
    Keeping both in one place would mean two retry policies to reason about.
    """
    url = build_url(base_url, path)
    started = time.perf_counter()

    try:
        response = await client.request(
            method or "GET",
            url,
            params=query or None,
            headers=headers or None,
            timeout=(timeout_ms / 1000) if timeout_ms else 30.0,
        )
    except Exception as exc:
        code, retryable = classify_exception(exc)
        return FetchOutcome(
            ok=False,
            error_code=code,
            # str(exc) on httpx errors carries the URL but never request headers,
            # so a credential cannot leak through this path. See SPEC §5.
            error_message=f"{type(exc).__name__}: {exc}",
            retryable=retryable,
            duration_ms=_elapsed_ms(started),
        )

    duration_ms = _elapsed_ms(started)

    if response.is_success:
        try:
            body = response.json()
        except ValueError as exc:
            return FetchOutcome(
                ok=False,
                status=response.status_code,
                error_code="TRANSFORM",
                error_message=f"upstream returned {response.status_code} but not JSON: {exc}",
                retryable=False,
                duration_ms=duration_ms,
            )
        return FetchOutcome(ok=True, status=response.status_code, body=body,
                            duration_ms=duration_ms)

    code, retryable = classify_status(response.status_code)
    return FetchOutcome(
        ok=False,
        status=response.status_code,
        error_code=code,
        error_message=f"upstream returned {response.status_code}",
        retryable=retryable,
        duration_ms=duration_ms,
    )


def _elapsed_ms(started: float) -> int:
    return int((time.perf_counter() - started) * 1000)

"""The smallest set of checks that fail if the worker's logic breaks.

Two things are load-bearing here and get the most attention: the transform
(it is the anti-corruption layer) and retryability (it drives the orchestrator's
retry loop, so a wrong answer here either wastes calls or drops recoverable ones).
"""

import httpx
import pytest
from core import (
    TransformError,
    apply_transform,
    build_url,
    classify_exception,
    classify_status,
    fetch_rest,
)

# A trimmed but structurally faithful Open-Meteo response.
OPEN_METEO = {
    "latitude": 38.875,
    "longitude": -94.8125,
    "current": {"time": "2026-08-09T22:00", "temperature_2m": 29.4, "wind_speed_10m": 11.2},
}

# The transform committed in integrations/open-meteo.yaml. If this test fails,
# the shipped manifest is broken.
OPEN_METEO_TRANSFORM = (
    "{ id: join(',', [to_string(latitude), to_string(longitude)]), "
    "tempC: current.temperature_2m, windKph: current.wind_speed_10m, "
    "observedAt: current.time }"
)

GITHUB_ISSUES = [
    {"id": 1001, "title": "first", "state": "open", "updated_at": "2026-08-01T00:00:00Z"},
    {"id": 1002, "title": "second", "state": "closed", "updated_at": "2026-08-02T00:00:00Z"},
]
GITHUB_ISSUES_TRANSFORM = (
    "[].{ id: to_string(id), title: title, state: state, updatedAt: updated_at }"
)


class TestTransform:
    def test_shipped_open_meteo_manifest_produces_a_canonical_record(self):
        records = apply_transform(OPEN_METEO_TRANSFORM, OPEN_METEO, "single", "currentWeather")
        assert records == [
            {
                "id": "38.875,-94.8125",
                "tempC": 29.4,
                "windKph": 11.2,
                "observedAt": "2026-08-09T22:00",
            }
        ]

    def test_list_emit_flattens_an_array_upstream(self):
        records = apply_transform(GITHUB_ISSUES_TRANSFORM, GITHUB_ISSUES, "list", "recentIssues")
        assert [r["id"] for r in records] == ["1001", "1002"]
        assert records[0]["state"] == "open"

    def test_list_emit_tolerates_an_empty_upstream(self):
        assert apply_transform("[].{id: to_string(id)}", [], "list", "r") == []

    def test_record_without_id_is_rejected_and_the_error_names_the_resource(self):
        with pytest.raises(TransformError) as err:
            apply_transform(
                "{tempC: current.temperature_2m}", OPEN_METEO, "single", "currentWeather"
            )
        assert "currentWeather" in str(err.value)
        assert "'id'" in str(err.value)

    def test_empty_string_id_is_rejected_too(self):
        with pytest.raises(TransformError, match="'id'"):
            apply_transform("{id: ''}", OPEN_METEO, "single", "r")

    def test_single_emit_that_matches_nothing_is_an_error_not_an_empty_record(self):
        with pytest.raises(TransformError, match="matched nothing"):
            apply_transform("nonexistent.path", OPEN_METEO, "single", "r")

    def test_emit_mismatch_is_caught(self):
        with pytest.raises(TransformError, match="not a list"):
            apply_transform(OPEN_METEO_TRANSFORM, OPEN_METEO, "list", "r")

    def test_invalid_jmespath_is_a_transform_error_not_a_crash(self):
        with pytest.raises(TransformError, match="JMESPath failed"):
            apply_transform("{{{ broken", OPEN_METEO, "single", "r")


class TestClassification:
    @pytest.mark.parametrize(
        ("status", "code", "retryable"),
        [
            (429, "RATE_LIMITED", True),
            (500, "UPSTREAM_5XX", True),
            (503, "UPSTREAM_5XX", True),
            (408, "UPSTREAM_4XX", True),   # Request Timeout — worth another go
            (425, "UPSTREAM_4XX", True),   # Too Early — worth another go
            (400, "UPSTREAM_4XX", False),  # our fault; retrying repeats the mistake
            (401, "UPSTREAM_4XX", False),
            (404, "UPSTREAM_4XX", False),
        ],
    )
    def test_status_codes(self, status, code, retryable):
        assert classify_status(status) == (code, retryable)

    @pytest.mark.parametrize(
        ("exc", "code", "retryable"),
        [
            (httpx.ConnectTimeout("slow"), "TIMEOUT", True),
            (httpx.ReadTimeout("slow"), "TIMEOUT", True),
            (httpx.ConnectError("refused"), "CONNECT", True),
            (ValueError("not an httpx error"), "UNKNOWN", False),
        ],
    )
    def test_exceptions(self, exc, code, retryable):
        assert classify_exception(exc) == (code, retryable)


class TestFetch:
    async def test_success_returns_parsed_body(self):
        transport = httpx.MockTransport(lambda _: httpx.Response(200, json=OPEN_METEO))
        async with httpx.AsyncClient(transport=transport) as client:
            outcome = await fetch_rest(
                client, base_url="https://api.open-meteo.com", method="GET",
                path="/v1/forecast", query={"latitude": "38.88"}, headers={}, timeout_ms=5000,
            )
        assert outcome.ok
        assert outcome.body["latitude"] == 38.875

    async def test_upstream_5xx_is_reported_as_retryable_not_raised(self):
        transport = httpx.MockTransport(lambda _: httpx.Response(503))
        async with httpx.AsyncClient(transport=transport) as client:
            outcome = await fetch_rest(
                client, base_url="https://x.test", method="GET", path="/", query={},
                headers={}, timeout_ms=5000,
            )
        assert (outcome.ok, outcome.error_code, outcome.retryable) == (False, "UPSTREAM_5XX", True)

    async def test_transport_failure_is_reported_as_retryable(self):
        def boom(_):
            raise httpx.ConnectError("connection refused")

        async with httpx.AsyncClient(transport=httpx.MockTransport(boom)) as client:
            outcome = await fetch_rest(
                client, base_url="https://x.test", method="GET", path="/", query={},
                headers={}, timeout_ms=5000,
            )
        assert (outcome.ok, outcome.error_code, outcome.retryable) == (False, "CONNECT", True)

    async def test_a_200_that_is_not_json_is_not_retryable(self):
        transport = httpx.MockTransport(lambda _: httpx.Response(200, text="<html>nope</html>"))
        async with httpx.AsyncClient(transport=transport) as client:
            outcome = await fetch_rest(
                client, base_url="https://x.test", method="GET", path="/", query={},
                headers={}, timeout_ms=5000,
            )
        assert (outcome.ok, outcome.error_code, outcome.retryable) == (False, "TRANSFORM", False)

    async def test_credentials_never_appear_in_an_error_message(self):
        """SPEC §5: a resolved credential must not leak through the error path."""
        secret = "ghp_thisisnotarealtokenjustatestvalue"

        def boom(_):
            raise httpx.ConnectError("connection refused")

        async with httpx.AsyncClient(transport=httpx.MockTransport(boom)) as client:
            outcome = await fetch_rest(
                client, base_url="https://x.test", method="GET", path="/", query={},
                headers={"Authorization": f"Bearer {secret}"}, timeout_ms=5000,
            )
        assert secret not in outcome.error_message


def test_build_url_does_not_double_or_drop_slashes():
    assert build_url("https://x.test/", "/v1/f") == "https://x.test/v1/f"
    assert build_url("https://x.test", "v1/f") == "https://x.test/v1/f"
    assert build_url("https://x.test/graphql", "") == "https://x.test/graphql"

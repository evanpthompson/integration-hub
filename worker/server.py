"""gRPC server. Protobuf in, protobuf out — all real logic lives in core.py."""

from __future__ import annotations

import asyncio
import json
import logging
import os
import signal

import grpc
import httpx
import worker_pb2
import worker_pb2_grpc
from core import TransformError, apply_transform, fetch_rest

log = logging.getLogger("worker")

_EMIT = {worker_pb2.LIST: "list", worker_pb2.SINGLE: "single"}


class WorkerService(worker_pb2_grpc.WorkerServicer):
    def __init__(self, client: httpx.AsyncClient) -> None:
        self._client = client

    async def Invoke(  # noqa: N802 — name is fixed by the proto service definition
        self,
        request: worker_pb2.InvokeRequest,
        context: grpc.aio.ServicerContext,
    ) -> worker_pb2.InvokeResponse:
        if request.protocol == worker_pb2.GRAPHQL:
            # Task 1.8. Rejected explicitly rather than silently treated as REST.
            return worker_pb2.InvokeResponse(
                ok=False,
                error_code="UNSUPPORTED",
                error_message="GraphQL upstreams are not implemented yet",
                retryable=False,
            )

        outcome = await fetch_rest(
            self._client,
            base_url=request.base_url,
            method=request.method,
            path=request.path,
            query=dict(request.query),
            headers=dict(request.headers),
            timeout_ms=request.timeout_ms,
        )

        # Never log request.headers — they carry resolved credentials. SPEC §5.
        log.info(
            "invoke run=%s integration=%s resource=%s attempt=%d status=%d ok=%s ms=%d",
            request.run_id, request.integration_id, request.resource,
            request.attempt, outcome.status, outcome.ok, outcome.duration_ms,
        )

        if not outcome.ok:
            return worker_pb2.InvokeResponse(
                ok=False,
                upstream_status=outcome.status,
                upstream_duration_ms=outcome.duration_ms,
                error_code=outcome.error_code,
                error_message=outcome.error_message,
                retryable=outcome.retryable,
            )

        try:
            records = apply_transform(
                request.transform,
                outcome.body,
                _EMIT.get(request.emit, "single"),
                request.resource,
            )
        except TransformError as exc:
            return worker_pb2.InvokeResponse(
                ok=False,
                upstream_status=outcome.status,
                upstream_duration_ms=outcome.duration_ms,
                error_code="TRANSFORM",
                error_message=str(exc),
                retryable=False,  # the same response will fail the same way
            )

        return worker_pb2.InvokeResponse(
            ok=True,
            upstream_status=outcome.status,
            upstream_duration_ms=outcome.duration_ms,
            records_json=json.dumps(records).encode(),
            count=len(records),
        )


async def serve() -> None:
    port = os.environ.get("WORKER_PORT", "50051")
    logging.basicConfig(
        level=logging.INFO,
        format='{"ts":"%(asctime)s","level":"%(levelname)s","msg":"%(message)s"}',
    )

    async with httpx.AsyncClient(follow_redirects=True) as client:
        server = grpc.aio.server()
        worker_pb2_grpc.add_WorkerServicer_to_server(WorkerService(client), server)
        server.add_insecure_port(f"[::]:{port}")
        await server.start()
        log.info("worker listening on :%s", port)

        stop = asyncio.Event()
        loop = asyncio.get_running_loop()
        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.add_signal_handler(sig, stop.set)
        await stop.wait()

        log.info("draining")
        await server.stop(grace=5)


if __name__ == "__main__":
    asyncio.run(serve())

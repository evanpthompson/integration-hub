# Integration Hub

**Adding an integration is configuration, not code** — which is what makes an agent able to do
it, and what makes "time to add an integration" a number worth measuring.

An integration platform where a declarative YAML manifest describes how to talk to an upstream
API, a C# orchestrator owns definitions, credentials and resiliency, a Python worker does the
fetch and transform over gRPC, and an MCP agent turns *"add an integration to the Hacker News
API"* into a working, invocable integration without anyone writing code.

> **Status: week 1 of 4 — MVP-0 works.** A manifest drives a real upstream call end to end:
> orchestrator → gRPC → worker → HTTP → JMESPath → canonical record. Adding a resource is a
> YAML edit with no code change and no rebuild, which was the whole point of MVP-0.
>
> **Not built yet:** persistence and run history, retries and circuit breaking, credentials
> (so no authenticated integrations), GraphQL, the MCP agent, and the cluster deployment.
> [`docs/PLAN.md`](docs/PLAN.md) tracks what lands when. Nothing here claims to work that doesn't.

---

## Architecture

```
      ┌──────────────────────────────────────────┐
      │  MCP Agent (Python, stdio)               │   runs locally in Claude Code
      │  probe → draft → validate → apply → test │   — a client, not a cluster workload
      └───────────────┬──────────────────────────┘
                      │ REST (writes) + GraphQL (reads, introspected)
      ┌───────────────▼──────────────────────────┐
      │  Orchestrator — ASP.NET Core 10 / C# 14  │
      │  · manifest registry (EF Core → Postgres)│
      │  · credential resolution                 │
      │  · resiliency: retry / breaker / timeout │
      │  · run history + Prometheus metrics      │
      │  · REST write API + GraphQL read API     │
      └───────────────┬──────────────────────────┘
                      │ gRPC (proto/worker.proto)
      ┌───────────────▼──────────────────────────┐
      │  Worker — Python 3.13 / grpc.aio         │
      │  · HTTP fetch (REST + GraphQL upstreams) │
      │  · JMESPath transform → canonical records│
      │  · classifies errors as retryable or not │
      │  · stateless, horizontally scalable      │
      └───────────────┬──────────────────────────┘
                      │ HTTPS
      ┌───────────────▼──────────────────────────┐
      │  Upstreams: GitHub REST, GitHub GraphQL, │
      │  Open-Meteo, + whatever the agent adds   │
      └──────────────────────────────────────────┘
```

The orchestrator holds credentials and never runs integration-specific logic. The worker runs
transforms and never sees the credential store — it receives resolved auth headers scoped to a
single invocation. That trust boundary is why the split exists;
[`docs/SPEC.md` §2.1](docs/SPEC.md) has the full argument, including the case for collapsing it.

## What an integration looks like

No code. This is the whole thing:

```yaml
apiVersion: integrationhub.dev/v1alpha1
kind: Integration
metadata:
  id: open-meteo
  displayName: Open-Meteo Forecast
spec:
  protocol: rest
  baseUrl: https://api.open-meteo.com
  auth: { type: none }
  resiliency:
    retry: { maxAttempts: 3, backoff: exponential, baseDelayMs: 200, jitter: true }
  resources:
    - name: currentWeather
      method: GET
      path: /v1/forecast
      params:
        - { name: latitude,  in: query, required: true }
        - { name: longitude, in: query, required: true }
      emit: single
      transform: |
        { id: join(',', [to_string(latitude), to_string(longitude)]),
          tempC: current.temperature_2m,
          observedAt: current.time }
```

`transform` is a JMESPath expression. Every upstream shape — REST or GraphQL — collapses into
the same canonical record envelope. That is the anti-corruption layer, and it is declarative
so a human can review it in a diff and an agent can write it.

Credentials appear only as `credentialRef` names, never values. See
[`docs/SPEC.md` §5](docs/SPEC.md).

## Quickstart

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [uv](https://docs.astral.sh/uv/).

```bash
# one-time: Python env + generate gRPC stubs from proto/worker.proto
uv sync
uv run python -m grpc_tools.protoc --proto_path=proto \
  --python_out=worker --grpc_python_out=worker --pyi_out=worker proto/worker.proto

uv run python worker/server.py &          # worker  :50051
dotnet run --project src/Orchestrator &   # orchestrator :5066
```

Then call an upstream that nobody wrote code for:

```bash
curl -X POST localhost:5066/integrations/open-meteo/resources/currentWeather/invoke \
  -H 'content-type: application/json' \
  -d '{"latitude":"38.88","longitude":"-94.82"}'
```

```json
{
  "runId": "019fea06-b1b2-76d9-b4c8-eb542183508b",
  "integrationId": "open-meteo", "resource": "currentWeather",
  "count": 1, "durationMs": 581, "attempts": 1,
  "records": [{ "id": "38.87,-94.80", "tempC": 28.8, "windKph": 21.3,
                "observedAt": "2026-08-10T04:45" }]
}
```

`localhost:5066/scalar/v1` is the API reference. `GET /integrations` lists what's loaded.

## Tests

```bash
./scripts/e2e.sh                    # both services, real upstream, 9 assertions
uv run pytest                       # worker logic
dotnet test src/Orchestrator.Tests  # manifest validation, credential safety, param binding
```

## Docs

| Doc | What's in it |
|---|---|
| [`docs/SPEC.md`](docs/SPEC.md) | **The technical spec** — architecture, manifest schema, gRPC contract, credentials, GraphQL, data model, observability, risks |
| [`docs/PLAN.md`](docs/PLAN.md) | Phases, exit criteria, the pre-decided cut list, the 2-minute demo script |
| [`docs/adr/`](docs/adr/) | One page per real decision, with the alternative that was rejected |

## Why not Apache Camel, or Mulesoft?

**If you have fifty integrations and a team, you should use them.** Connector
libraries, mature transformation tooling, EIP patterns, monitoring, support contracts,
and a hiring pool that already knows them. Building this instead would be a bad
engineering decision and I would say so in the room.

This exists to test one hypothesis, and the hypothesis is narrow enough to state:

> Constraining an integration to **pure configuration** — no generated code, no
> compiled route, no deploy — makes it authorable by a language model. Is that worth
> the expressiveness you give up?

Camel and Mulesoft are code, or code-shaped. A Camel route is a Java DSL with
processors: compiled, deployed, reviewed. A Mulesoft flow is assembled visually but is
still a deployable artifact. Neither is "add a config file and it is live."

That distinction had no particular value until recently. It has one now: you cannot
reliably ask a model to author a production Camel route. You can absolutely ask it to
author twenty lines of YAML, check them against a published schema, dry-run them
against the live API, and confirm real records come back — in one conversation, with
no deploy. Mulesoft is adding AI assistants that help a human build a flow; this is a
different interaction shape.

**And the trade is real.** Camel does content-based routing, aggregation, splitting,
sagas, transactional outbox, JMS, Kafka, FTP, SOAP, and three hundred connectors. This
does an HTTP call and a JMESPath expression. The declarative-only constraint that makes
agent authoring possible is exactly what makes it less capable. That is the bet: pure
configuration is enough for the common case, and the escape hatch
([ADR 0001](docs/adr/0001-declarative-manifests.md)) exists for when it isn't.

The lineage is borrowed on purpose rather than reinvented — the canonical envelope is a
canonical data model, the transform is an anti-corruption layer, the
classify-then-decide retry split is standard EIP, and planned payload capture is a
wiretap.

## Why this exists

It is a scaled-down rebuild of an integration platform I built at RX Savings Solutions — which
onboarded partner API integrations through a step-by-step intake tool that emitted a YAML
manifest, and handled credentials, retries, and resiliency centrally. That system has a
problem: no number was ever attached to it. This one is built to produce the number.

The stack is deliberate too — [`docs/SPEC.md` §2.2](docs/SPEC.md) says why, including the
places where it is over-built on purpose and where the cheaper version would do.

## License

MIT

# Integration Hub

**Adding an integration is configuration, not code** — which is what makes an agent able to do
it, and what makes "time to add an integration" a number worth measuring.

An integration platform where a declarative YAML manifest describes how to talk to an upstream
API, a C# orchestrator owns definitions, credentials and resiliency, a Python worker does the
fetch and transform over gRPC, and an MCP agent turns *"add an integration to the Hacker News
API"* into a working, invocable integration without anyone writing code.

> **Status: week 1 of 4.** The spec is complete and the orchestrator serves health and API
> docs. The registry, worker, agent layer, and cluster deployment are not built yet. This
> README describes what is being built; [`docs/PLAN.md`](docs/PLAN.md) says how far along it
> actually is. Nothing here claims to work that doesn't.

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

```bash
dotnet run --project src/Orchestrator
curl localhost:5066/healthz          # {"status":"ok"}
open  localhost:5066/scalar/v1       # API reference
```

Requires the .NET 10 SDK. The worker, registry, and agent land in week 1–3 —
see [`docs/PLAN.md`](docs/PLAN.md).

## Docs

| Doc | What's in it |
|---|---|
| [`docs/SPEC.md`](docs/SPEC.md) | **The technical spec** — architecture, manifest schema, gRPC contract, credentials, GraphQL, data model, observability, risks |
| [`docs/PLAN.md`](docs/PLAN.md) | Phases, exit criteria, the pre-decided cut list, the 2-minute demo script |
| [`docs/adr/`](docs/adr/) | One page per real decision, with the alternative that was rejected |

## Why this exists

It is a scaled-down rebuild of an integration platform I built at RX Savings Solutions — which
onboarded partner API integrations through a step-by-step intake tool that emitted a YAML
manifest, and handled credentials, retries, and resiliency centrally. That system has a
problem: no number was ever attached to it. This one is built to produce the number.

The stack is deliberate too — [`docs/SPEC.md` §2.2](docs/SPEC.md) says why, including the
places where it is over-built on purpose and where the cheaper version would do.

## License

MIT

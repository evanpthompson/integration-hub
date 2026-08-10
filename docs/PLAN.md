# Integration Hub — Build Plan

Companion to [`SPEC.md`](SPEC.md). The spec says what it is; this says what gets built, in what
order, and what gets thrown overboard when the schedule slips — decided now, while it's cheap.

Start: 2026-08-09. Target: complete by **2026-09-07** (4 weeks, part-time).

---

## Phase 0 — The repo exists (day 0)

**Goal:** a public URL that proves the thing exists and that the architecture is thought
through. Not a working product.

| # | Task | Notes |
|---|---|---|
| 0.1 | Install the .NET 10 SDK | The Homebrew cask needs sudo; `dot.net/v1/dotnet-install.sh --channel 10.0` installs to `~/.dotnet` without it. |
| 0.2 | `git init`, first commit of `docs/` | The spec is the most valuable thing in the repo on day 0. |
| 0.3 | Create the repo, **public**; add the GitHub push mirror | A public container registry also disposes of Risk #1. |
| 0.4 | `dotnet new webapi -o src/Orchestrator`, add `/healthz`, wire Scalar | Ten minutes. It must actually run. |
| 0.5 | Commit `proto/worker.proto` verbatim from SPEC §4.1 | Contract-first is a claim; the commit history proves it. |
| 0.6 | Commit `integrations/open-meteo.yaml` and `github.yaml` | The manifest format is the idea. Show it early. |
| 0.7 | Write `README.md`: architecture diagram, the one-sentence thesis, a "week 1 of 4" status line | The status line is what keeps it honest. |
| 0.8 | Write `docs/adr/0001-declarative-manifests.md` | Sets the pattern that ADRs get written as decisions happen, not reconstructed later. |

**Exit criteria:** the repo loads, shows the diagram and the manifest format, and
`dotnet run` serves `/healthz`.

**Time budget:** 4 hours. If it exceeds 6, drop 0.4 and ship docs only — the docs are doing
most of the work anyway.

**Status: complete.** Watch item from this phase: the `webapi` template resolves
`Microsoft.OpenApi` 2.0.0, which carries GHSA-v5pm-xwqc-g5wc (high). Pinned to 2.11.0.

---

## Phase 1 — Working core, locally (Aug 11 – 17)

**Goal:** invoke a real upstream end to end, from `curl` to GitHub and back, with retries.

| # | Task | Exit signal |
|---|---|---|
| 1.1 | Manifest parsing + validation (YAML → typed model, JSON Schema, credential-value rejection) | Tests from SPEC §13 pass. |
| 1.2 | EF Core model + initial migration (`integrations`, `runs`); Postgres in a local container | `dotnet ef database update` clean. |
| 1.3 | Startup reconciliation of `integrations/*.yaml` + `POST /integrations` hot-load | Both paths upsert; `source` recorded correctly. |
| 1.4 | Credential resolution chain + redaction enricher | Redaction test passes. |
| 1.5 | Worker: `grpc.aio` server, REST fetch via `httpx`, JMESPath transform, error classification | pytest transform + classification tests pass. |
| 1.6 | Orchestrator gRPC client + `Microsoft.Extensions.Http.Resilience` pipeline (retry, timeout, breaker) | A `retryable=true` fake produces `RETRIED_SUCCESS`. |
| 1.7 | `POST /invoke` writes a `runs` row and returns the canonical envelope | `curl` against `open-meteo` returns real weather. |
| 1.8 | GraphQL **upstream** support + `github-graphql.yaml` | `repoTopics` returns records; a `RATE_LIMITED` GraphQL error is classified retryable. |
| 1.9 | End-to-end script against a stub upstream | Runs green locally. |

**Exit criteria:** three integrations invocable from `curl`, run history in Postgres, retries
observable. Still localhost, still no agent.

**Order note:** do 1.8 immediately after 1.5 while the fetch path is fresh — it's a two-hour
change then and a half-day change later.

---

## Phase 2 — On the cluster (Aug 18 – 24)

**Goal:** it runs in the k3s dev cluster, GitOps-synced, with traces.

| # | Task | Notes |
|---|---|---|
| 2.1 | Get a working kubeconfig for the dev cluster | Do this first — it's the gate. |
| 2.2 | Dockerfiles for orchestrator and worker; multi-stage, non-root | `deploy/`. |
| 2.3 | `.gitlab-ci.yml`: lint → test → build → push to the **public** container registry | Sidesteps the missing-`imagePullSecret` gap entirely (Risk #1). |
| 2.4 | Postgres in-cluster: Deployment + PVC + Service, `postgres:17-alpine` | `ponytail:` single instance, no HA, no operator. Upgrade path is CNPG if this ever mattered, which it won't. |
| 2.5 | GitOps repo: `base/integration-hub/` (3 Deployments, 2 Services, 1 Ingress), `overlays/dev/…`, Argo `Application` | Mirror the existing sample-app pattern exactly. Kustomize, not Helm — follow what actually works in that repo today, not the aspirational chart flow. |
| 2.6 | Create the credentials secret by hand; document it in `RUNBOOK.md` | The cluster's SOPS/KSOPS decryption path is documented but not installed (Risk #2). Do not install it for one secret. |
| 2.7 | Kubernetes-native gRPC readiness probe on the worker; `/readyz` with DB ping on the orchestrator | No FastAPI, no `grpc_health_probe` binary. |
| 2.8 | OpenTelemetry on both services → OTLP → Tempo; `ServiceMonitor` for Prometheus | The cross-language trace is the deliverable here. |
| 2.9 | Ingress reachable on the LAN, serving `/scalar` | Add the `/etc/hosts` entry to the runbook. |

**Exit criteria:** Argo shows the app healthy and synced; a `curl` through the ingress returns
a real record; one Tempo trace spans C# → gRPC → Python → GitHub.

**Watch item:** 2.1 and 2.5 are where the unknown-unknowns live. If Phase 2 is going to
overrun, it will announce itself by Aug 20. If it does, take the Phase 2 cut and move on — the
agent layer is worth more than the cluster is.

---

## Phase 3 — The agent + GraphQL read API (Aug 25 – 31)

**Goal:** the thing that makes this not a CRUD app.

| # | Task | Notes |
|---|---|---|
| 3.1 | HotChocolate: `Query` with `integrations`, `integration`, `runs`, `stats` | SPEC §6. |
| 3.2 | `DataLoader` for `Integration.runs`; test asserting ≤ 2 SQL queries | The N+1 answer needs to be a file, not a claim. |
| 3.3 | `Stats` resolver in SQL, including `retrySuccessRate` | This backs every number in the write-up. Get it right. |
| 3.4 | MCP server skeleton, stdio, registered in Claude Code | `agent/`, uv workspace member. |
| 3.5 | `probe_api` — OpenAPI / GraphQL introspection / sample-response shape summary | Return a *summary*, not the payload; context budget matters. |
| 3.6 | `draft_integration` + `agent/prompts/` (system prompt, manifest JSON Schema, 2 few-shot examples) | The highest-iteration part of the project. Budget a full day. |
| 3.7 | `validate_integration`, `apply_integration`, `invoke` | Thin HTTP wrappers. |
| 3.8 | `graphql` passthrough tool | One tool replaces three read tools and improves as the schema grows. |
| 3.9 | Dry-run the full loop against Hacker News, then `git checkout` the result away | Rehearse the demo target without spending it. |

**Exit criteria:** "add an integration to the Hacker News API that returns the top 10 stories"
produces a valid manifest, applies it, invokes it, and returns real stories — in one
conversation, without hand-editing.

---

## Phase 4 — Measure, record, package (Sep 1 – 7)

**Goal:** the numbers, the demo, and the write-up.

| # | Task | Notes |
|---|---|---|
| 4.1 | Load script: concurrency sweep against a local stub, ≥500 runs | Never load-test GitHub. |
| 4.2 | Chaos: a 500-returning manifest, plus `kubectl scale worker --replicas=0` mid-run | Two distinct failure modes, reported separately. |
| 4.3 | Timed manual-vs-agent measurement, with the definition of done written down first | SPEC §9. Resist rounding in your own favour. |
| 4.4 | Grafana dashboard, 4 panels, committed as JSON | 25% of the demo's visual weight. |
| 4.5 | Record the 2-minute demo (script below) | Expect 5+ takes. |
| 4.6 | `SUMMARY.md`: product, architecture, metrics, evaluation plan — one page | |
| 4.7 | Remaining ADRs (0002–0006) | Write from the commit history while it's fresh. |
| 4.8 | Postmortem: what was cut, what surprised, what would change | This is what an experienced reader actually reads. |
| 4.9 | Polish `README.md`; add the demo video link at the top | Most readers give a README about 15 seconds. |

**Exit criteria:** a stranger can watch two minutes, read one page, and understand the system
and the numbers.

---

## The 2-minute demo

| Time | Shot |
|---|---|
| 0:00–0:15 | Grafana: three integrations live, run history ticking, p95 tile. Voiceover: the one-sentence thesis. |
| 0:15–0:45 | Claude Code: *"add an integration to the Hacker News API that returns the top 10 stories."* Agent probes, drafts the manifest, validates. Manifest YAML on screen. |
| 0:45–1:05 | Applied and invoked live. Real stories return. Stopwatch overlay against the hand-authored baseline. |
| 1:05–1:30 | Chaos: scale the worker to zero mid-run. Show the run table filling with retries, the breaker opening, then recovery. |
| 1:30–2:00 | Tempo: one trace, C# → gRPC → Python → upstream, retry attempts as sibling spans. End on the architecture diagram. |

Nothing in that two minutes is a slide. Every second is a running system.

---

## Cut list — decided now, in order

When the schedule slips, cut from the top. No re-litigating mid-sprint.

1. **Circuit breaker** → retry + timeout only. The breaker is 20% of the resiliency story and
   most of the tuning pain.
2. **Postgres** → SQLite on a PVC. EF Core provider swap; migrations still demonstrate the
   same skill.
3. **Cluster deployment (Phase 2)** → run the demo locally, keep the Dockerfiles and the
   GitOps manifests as unapplied, reviewable artifacts. Costs infra signal; costs the demo
   nothing. **The agent layer outranks the cluster — if only one survives, it's the agent.**
4. **Distributed tracing** → metrics and structured logs only. Painful, because the trace
   waterfall is the best screenshot in the project. Cut it before cutting the agent.
5. **`github-graphql` upstream** → REST only. Keeps the GraphQL *read API*, which is the
   stronger of the two.
6. **`Stats` p95 in SQL** → read it off the Prometheus histogram and say so.

Never cut: the manifest format, the agent loop, the honest metrics methodology, the ADRs.
Those are the project.

---

## Definition of done

- [ ] One-pager — product, architecture, metrics, evaluation plan
- [ ] 2-minute recorded demo (the dev cluster is LAN-only, so there is no public live URL)
- [ ] Postmortem
- [ ] README that lands in 15 seconds
- [ ] Six ADRs
- [ ] One measured number for "time to add an integration," manual vs. agent-scaffolded

---

## Later (not this project)

Parked deliberately. Adding any of these is how four weeks becomes twelve.

- GraphQL subscriptions for live run streaming in the demo
- The `handler:` plugin escape hatch and agent-scaffolded handler stubs
- OIDC on the orchestrator API
- Scheduled/polled sync and inbound webhooks
- GitOps-managed secrets (KSOPS or External Secrets)
- preprod/prod overlays, once a second cluster exists

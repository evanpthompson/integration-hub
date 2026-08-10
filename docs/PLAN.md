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
| 1.6 | Orchestrator gRPC client + Polly v8 pipeline (retry, breaker) — **done** | A `retryable=true` fake produces `RETRIED_SUCCESS`; confirmed end to end against `synthetic-flaky`. |
| 1.7 | `POST /invoke` writes a `runs` row and returns the canonical envelope | `curl` against `open-meteo` returns real weather. |
| 1.8 | GraphQL **upstream** support + `github-graphql.yaml` | `repoTopics` returns records; a `RATE_LIMITED` GraphQL error is classified retryable. |
| 1.9 | End-to-end script against a stub upstream | Runs green locally. |

**Checkpoint — MVP-0, the walking skeleton (first 2–3 days).** Before the rest of Phase 1,
build the thinnest slice that proves the one assumption everything else rests on: that a
manifest can drive a cross-language call and produce a correct canonical record with no
integration-specific code. That is **1.1 + the file half of 1.3 + 1.5 + 1.7 + 1.9**, against
`open-meteo` only — keyless, so no credential machinery is needed to prove the transform path.
Manifests stay in memory; no Postgres, no retries, no GitHub, no agent.

Done when this returns real weather:

```bash
curl -X POST localhost:5066/integrations/open-meteo/resources/currentWeather/invoke \
  -H 'content-type: application/json' -d '{"latitude":38.88,"longitude":-94.82}'
```

…and, the criterion that actually matters, **adding a second resource to that YAML requires
zero code changes.** If it doesn't, stop and fix the design before building anything on top of
it — the agent layer is worthless if integrations aren't really just configuration.

**Exit criteria (full phase):** three integrations invocable from `curl`, run history in
Postgres, retries observable. Still localhost, still no agent.

**Order note:** do 1.8 immediately after 1.5 while the fetch path is fresh — it's a two-hour
change then and a half-day change later.

---

## Phase 2 — The agent + GraphQL read API (Aug 18 – 24)

**Goal:** the thing that makes this not a CRUD app.

Ordered ahead of the cluster deliberately. The cut list below says the agent outranks the
cluster if only one survives; building them in the opposite order would have contradicted that
every day of the schedule. The agent talks to the orchestrator over HTTP, so localhost is a
perfectly good target — nothing here needs Kubernetes.

| # | Task | Notes |
|---|---|---|
| 2.1 | HotChocolate: `Query` with `integrations`, `integration`, `runs`, `stats` | SPEC §6. |
| 2.2 | `DataLoader` for `Integration.runs`; test asserting ≤ 2 SQL queries | The N+1 answer needs to be a file, not a claim. |
| 2.3 | `Stats` resolver in SQL, including `retrySuccessRate` | This backs every number in the write-up. Get it right. |
| 2.4 | MCP server skeleton, stdio, registered in Claude Code | `agent/`, uv workspace member. |
| 2.5 | `probe_api` — OpenAPI / GraphQL introspection / sample-response shape summary | Return a *summary*, not the payload; context budget matters. |
| 2.6 | `draft_integration` + `agent/prompts/` (system prompt, manifest JSON Schema, 2 few-shot examples) | The highest-iteration part of the project. Budget a full day. |
| 2.7 | `validate_integration`, `apply_integration`, `invoke` | Thin HTTP wrappers. |
| 2.8 | `graphql` passthrough tool | One tool replaces three read tools and improves as the schema grows. |
| 2.9 | Dry-run the full loop against Hacker News, then `git checkout` the result away | Rehearse the demo target without spending it. |

**Exit criteria:** "add an integration to the Hacker News API that returns the top 10 stories"
produces a valid manifest, applies it, invokes it, and returns real stories — in one
conversation, without hand-editing.

At this point the project is demoable end to end on a laptop. Everything after this is
production texture, not new capability.

---

## Phase 3 — On the cluster (Aug 25 – 31)

**Goal:** it runs in the k3s dev cluster, GitOps-synced, with traces.

Smaller than it looks. The runner (`tags: [homelab]`), the cluster, ingress, TLS, Argo CD, and
a default `local-path` StorageClass are all already running and verified — this phase adds an
app to a working pipeline rather than standing delivery up.

| # | Task | Notes |
|---|---|---|
| 3.1 | Point `KUBECONFIG` at the existing dev cluster credentials | Already provisioned and reachable. Not a gate. |
| 3.2 | Dockerfiles for orchestrator and worker; multi-stage, non-root | `deploy/`. |
| 3.3 | `.gitlab-ci.yml`: lint → test → build → push to the **public** container registry | Sidesteps the missing-`imagePullSecret` gap entirely (Risk #1). Dispatches to the existing self-hosted runner, so it burns no shared CI minutes. |
| 3.4 | Postgres in-cluster: Deployment + PVC + Service, `postgres:17-alpine` | `ponytail:` single instance, no HA, no operator. Upgrade path is CNPG if this ever mattered, which it won't. |
| 3.5 | GitOps repo: `base/integration-hub/` (3 Deployments, 2 Services, 1 Ingress), `overlays/dev/…`, Argo `Application` | Mirror the existing sample-app pattern exactly. Kustomize, not Helm — follow what actually works in that repo today, not the aspirational chart flow. |
| 3.6 | Create the credentials secret by hand; document it in `RUNBOOK.md` | KSOPS is documented but not installed (Risk #2). Do not install it for one secret. |
| 3.7 | Kubernetes-native gRPC readiness probe on the worker; `/readyz` with DB ping on the orchestrator | No FastAPI, no `grpc_health_probe` binary. |
| 3.8 | OpenTelemetry on both services → OTLP → Tempo; `ServiceMonitor` for Prometheus | The cross-language trace is the deliverable here. |
| 3.9 | Ingress reachable on the LAN, serving `/scalar` | Add the `/etc/hosts` entry to the runbook. |
| 3.10 | OIDC on the orchestrator API against the cluster's Authentik | ~half a day. The IdP is already running and its clients are Terraform-declarative, so most of this is wiring. **Not a copy of the Argo/Grafana pattern:** those are browser SSO via authorization-code, keyed off a redirect URI; the caller here is the MCP agent, so it wants **client credentials**, which has no meaningful redirect URI. Expect a small change to the shared `oauth2_redirect_uris` variable shape, not just a new map entry. |

**Exit criteria:** Argo shows the app healthy and synced; a `curl` through the ingress returns
a real record; one Tempo trace spans C# → gRPC → Python → GitHub.

**Watch item:** 3.4 and 3.5 are where the unknown-unknowns live — Postgres is the first
stateful workload in this cluster, and this is the first app added to the GitOps repo since the
sample. If the phase overruns, take the Phase 3 cut; by now the demo already works locally.

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
3. **Cluster deployment (Phase 3)** → run the demo locally, keep the Dockerfiles and the
   GitOps manifests as unapplied, reviewable artifacts. Costs infra signal; costs the demo
   nothing. **The agent layer outranks the cluster — if only one survives, it's the agent**,
   which is why the agent is built first and this cut is cheap to take.
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

## Direction changes not yet scheduled

Decisions taken after the plan was written. Recorded here rather than silently
rewritten into the phases, because each one contradicts something already committed.

### Multi-repo, managed by Helm

`SPEC.md` §12 says monorepo and task 3.5 says Kustomize. Both are superseded: supporting
services get their own repos, each shipping its own Helm chart, with a shared umbrella
chart eventually deploying the whole stack.

Helm is not a fight with the platform — it is where the platform was already going.
The delivery docs describe versioned charts pushed to an OCI registry as the target
state, with Kustomize as the interim. This accelerates that rather than diverging.

**The proto question is settled** — see [`adr/0007`](adr/0007-shared-proto-vendored-from-a-contracts-repo.md).
An `integration-hub-contracts` repo is the source of truth; each service vendors a
copy and a check fails on drift. Every repo keeps building offline with no registry
and no publish step.

Split order, smallest blast radius first:

| # | Step | Note |
|---|---|---|
| 1 | `integration-hub-contracts` — the proto, tagged | Nothing depends on it yet, so it cannot break anything. |
| 2 | Vendor the copy back into this repo + a drift check in `scripts/e2e.sh` | Proves the mechanism while everything is still one repo. |
| 3 | Split out `synthetic` with its chart | Already self-contained: own `go.mod`, own Dockerfile, own chart, no imports from this repo. |
| 4 | Charts for orchestrator and worker; retire the Kustomize plan in task 3.5 | |
| 5 | The umbrella chart | Whole stack — orchestrator, worker, Postgres, synthetic — in one `helm install` for e2e and demos. |

Step 5 is the one with real payoff: `helm install` bringing up the entire stack is a
much better demo opening than four terminals, and it makes the e2e suite runnable in
a cluster rather than only on a laptop.

### Inbound webhooks

`SPEC.md` §1.2 lists webhooks as a non-goal. That changes: they are wanted, just not
yet. Two halves, and they are independent:

- **Platform:** an inbound endpoint per integration, signature verification, replay
  protection, and delivery into the same canonical envelope the pull path produces.
  The manifest grows a `webhooks:` block. This is a genuine second ingress direction,
  not a variation on invoke — it inverts who initiates.
- **Synthetic service:** a webhook *emitter*, so the inbound path can be tested
  deterministically. Cheap once the platform side exists — a timer and an HTTP POST.

### Serverless emulation

**Do not build this.** LocalStack's Community edition is free, open source, and
already covers Lambda, S3, SQS, SNS, DynamoDB and API Gateway; it is a `docker run`.
Writing an emulator is a multi-year project that several teams already lost.

If the goal is specifically *functions on the existing k3s cluster* rather than AWS
compatibility, the answer is Knative or OpenFaaS, not an emulator. Worth deciding
which question is actually being asked before either lands on a schedule.

---

## Later (not this project)

Parked deliberately. Adding any of these is how four weeks becomes twelve.

- GraphQL subscriptions for live run streaming in the demo
- The `handler:` plugin escape hatch and agent-scaffolded handler stubs
- Scheduled/polled sync
- Per-environment `baseUrl` overrides for manifests — `integrations/synthetic.yaml`
  hardcodes `localhost:8080`, which will not survive the cluster
- Per-resource headers, so a fault (or an `Accept`) can be armed on one resource
  rather than a whole integration
- GitOps-managed secrets (KSOPS or External Secrets)
- preprod/prod overlays, once a second cluster exists

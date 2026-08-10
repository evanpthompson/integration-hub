# Integration Hub — Technical Specification

Status: **spec, v1**. Build sequencing lives in [`PLAN.md`](PLAN.md).

Last updated: 2026-08-09

---

## 1. What this is

A small, real integration platform: declarative manifests describe how to talk to an upstream
API, a C# orchestrator owns definitions/credentials/resiliency, a Python worker does the fetch
and transform over gRPC, and an MCP agent turns *"add an integration to the Hacker News API"*
into a working, invocable integration in under a minute.

It is a scaled-down rebuild of an integration technology platform I built at RX Savings
Solutions, with an AI layer that makes it novel rather than a CRUD demo.

### 1.1 The one-sentence thesis

**Adding an integration is configuration, not code** — which is what makes an agent able to do
it, and what makes "time to add an integration" a metric worth measuring.

Everything in this spec follows from that. Manifests are declarative. Transforms are
expressions, not functions. The worker is generic. The agent writes YAML, not Python.

### 1.2 Non-goals

Explicitly out of scope for v1, listed so they don't creep back in:

| Not building | Why |
|---|---|
| Multi-tenancy / orgs / RBAC | Single-user system — one identity, no roles. OIDC *authentication* on the API is in scope (PLAN Phase 3, task 3.10); per-user *authorization* is not. |
| Inbound webhooks | Not in v1 — but no longer a permanent non-goal. See `PLAN.md`, "Direction changes". |
| Scheduled polling / cron sync | On-demand invoke is enough to demo and measure. |
| A custom web UI | Scalar (API docs) + Grafana (metrics/traces) cover "visual" for free. |
| Streaming / large-result pagination beyond a page cap | Note the ceiling, don't build it. |
| preprod / prod overlays | Dev cluster only. |
| Secret rotation, audit log, approval workflow | Enterprise theater. Say it in the interview, don't build it. |

---

## 2. Architecture

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

### 2.1 Why the boundary is where it is

The orchestrator/worker split costs a network hop and a proto file. Three reasons it earns
that, in descending order of honesty:

1. **Trust boundary.** The orchestrator holds the credential store and never executes
   integration-specific logic. The worker executes transforms (and, at the escape hatch,
   user-supplied handler code) and never sees the credential store — it receives resolved auth
   headers scoped to one invocation. This is a real isolation argument, not a decoration.
2. **Independent scaling.** Fetch work is I/O-bound and bursty; registry/API work is not.
3. **Signal.** Cross-language distributed systems with contract-first protobuf and a
   propagated trace is the thing a Staff/Solutions-Architect interview actually wants to see.

Reason 3 is real but would not alone justify the split. If reason 1 ever stops being true —
if handler code migrates into the orchestrator — collapse this into one service.

> `ponytail:` a single ASP.NET Core service could do all of this. The split is deliberate,
> and reason 1 is the one to defend if challenged.

### 2.2 Technology decisions

| Layer | Choice | Note |
|---|---|---|
| Orchestrator | .NET 10 (LTS) / ASP.NET Core, C# 14 | .NET 8 goes out of support Nov 2026 — starting a new repo on it in Aug 2026 would be stale on arrival. |
| Persistence | Postgres 17, EF Core 10 + Npgsql, code-first migrations | Migrations are part of the C# signal. Single instance, no HA. |
| Resiliency | Polly v8 (`Polly.Core`) | `Microsoft.Extensions.Http.Resilience` wraps `HttpClient`, and the orchestrator’s outbound call is gRPC — so the pipeline is built directly. Retry is driven by the worker’s `retryable` flag, not by exceptions. |
| RPC | gRPC, `Grpc.AspNetCore` + `grpcio` | Protoc ships inside `Grpc.Tools` / `grpcio-tools`. No system protoc needed. |
| Read API | HotChocolate 15 (GraphQL) | See §6. |
| Worker | Python 3.13, `grpc.aio`, `httpx`, `jmespath` | **No FastAPI** — see below. |
| Python tooling | `uv` + `ruff`, single workspace for `worker/` and `agent/` | Per standing preference. No pip/requirements.txt. |
| Agent | Official `mcp` Python SDK, stdio transport | Runs in Claude Code, not in the cluster. |
| Observability | OpenTelemetry → OTLP → Tempo; Prometheus scrape; JSON logs → Loki | All three already run in the k3s dev cluster. |
| Tests | xUnit (orchestrator), pytest (worker/agent) | One meaningful check per non-trivial path, not a suite per function. |

> `ponytail:` the worker is gRPC-only, so **FastAPI is dropped** — it would exist solely to
> serve a health endpoint that `grpc_health_v1` already provides, and Kubernetes has native
> gRPC probes. Add FastAPI only if a debug HTTP surface turns out to be genuinely useful.
> Say so and it goes back in.

---

## 3. The manifest

The manifest is the source of truth and the direct descendant of the RX Savings
survey → YAML pipeline. It lives at `integrations/<id>.yaml` in the repo *and* in the
orchestrator's registry table.

### 3.1 Dual path: file and hot-load

Files are reviewable in a merge request. Hot-load makes the demo live. Both, layered:

- `integrations/*.yaml` in git is the declarative record. The orchestrator reconciles this
  directory on startup (upsert by `metadata.id`).
- `POST /integrations` accepts a manifest body and upserts it immediately, no restart.
- The agent does **both**: writes the file, then posts it. The demo is live; the repo stays
  legible; the commit shows what the agent produced.

Conflict rule: last write wins, and every manifest row records `source` (`file` | `api`) and
`updatedAt`. Startup reconciliation does not clobber an API-sourced manifest that is newer.

### 3.2 Schema

```yaml
apiVersion: integrationhub.dev/v1alpha1
kind: Integration
metadata:
  id: github                      # slug, [a-z0-9-]{1,40}, primary key
  displayName: GitHub REST API
spec:
  protocol: rest                  # rest | graphql
  baseUrl: https://api.github.com
  auth:
    type: bearer                  # none | bearer | headerKey | queryKey
    credentialRef: github-token   # a NAME, never a value — see §5
    headerName: Authorization     # headerKey/queryKey only
  defaults:
    headers:
      Accept: application/vnd.github+json
      User-Agent: integration-hub
    timeoutMs: 5000
  resiliency:
    retry:
      maxAttempts: 3
      backoff: exponential        # exponential | constant
      baseDelayMs: 200
      jitter: true
    circuitBreaker:
      failureRatio: 0.5
      samplingSeconds: 30
      breakSeconds: 15
      minThroughput: 8
  rateLimit:
    requestsPerMinute: 60
  resources:
    - name: repoSummary
      method: GET
      path: /repos/{owner}/{repo}
      params:
        - { name: owner, in: path,  required: true }
        - { name: repo,  in: path,  required: true }
      emit: single                # single | list
      transform: |
        { id: to_string(id), name: full_name, stars: stargazers_count,
          openIssues: open_issues_count, updatedAt: pushed_at }
    - name: recentIssues
      method: GET
      path: /repos/{owner}/{repo}/issues
      params:
        - { name: owner,    in: path,  required: true }
        - { name: repo,     in: path,  required: true }
        - { name: per_page, in: query, default: "10" }
      emit: list
      transform: |
        [].{ id: to_string(id), title: title, state: state, updatedAt: updated_at }
```

Field notes:

- **`transform` is a JMESPath expression**, evaluated by the worker against the decoded
  response body. This *is* the anti-corruption layer: every upstream shape collapses into the
  same canonical record shape. Declarative, so the agent can write it and a human can review
  it in a diff.
- **`emit`** tells the worker whether the expression yields one record or an array. Cheaper
  and clearer than sniffing the result type.
- **`params`** with `in: path` are substituted into `path`; `in: query` are appended.
  `required: true` with no supplied value is a 400 before any network call.
- **No response schema / JSON Schema validation in v1.** The transform is the contract.
  `ponytail:` add schema validation when a silent upstream shape change actually burns you.

### 3.3 GraphQL upstreams

A real integration platform meets upstreams where they are, and half of them are GraphQL now.
`protocol: graphql` changes only the request construction — the transform, resiliency,
credential, and canonical-envelope paths are shared:

```yaml
metadata:
  id: github-graphql
  displayName: GitHub GraphQL API
spec:
  protocol: graphql
  baseUrl: https://api.github.com/graphql
  auth: { type: bearer, credentialRef: github-token }
  resources:
    - name: repoTopics
      emit: list
      query: |
        query($owner:String!, $repo:String!) {
          repository(owner:$owner, name:$repo) {
            repositoryTopics(first:10) { nodes { topic { name } } }
          }
        }
      params:
        - { name: owner, in: variable, required: true }
        - { name: repo,  in: variable, required: true }
      transform: |
        data.repository.repositoryTopics.nodes[].{ id: topic.name, name: topic.name }
```

Semantics:

- Always `POST` with `{"query": ..., "variables": {...}}`. `method` and `path` are ignored.
- `in: variable` params map into `variables`.
- **GraphQL 200-with-`errors` is a failure.** A non-empty top-level `errors` array is treated
  as an error regardless of HTTP status; retryability is decided by the error's `type`
  (`RATE_LIMITED` → retryable, everything else → not). This is the detail that separates
  someone who has actually consumed GraphQL from someone who has read about it.
- Same-integration REST and GraphQL are separate manifests. Merging them into one manifest
  with mixed-protocol resources is possible and not worth it.

### 3.4 The escape hatch

Roughly 80% of integrations are expressible declaratively. For the rest, a resource may
declare `handler: mypkg.module:function` — a Python callable resolved by the worker, given the
raw response, returning canonical records. It runs in the worker, which is precisely why the
worker exists (§2.1).

Deliberately *not* built in v1; the field is reserved in the schema and rejected with a clear
"not implemented" error. The agent's ability to scaffold a handler stub is the natural Phase 5
story. `ponytail:` don't build the plugin loader until an integration actually needs it.

### 3.5 Canonical envelope

Every successful invocation, from every protocol, returns:

```json
{
  "runId": "01J...",
  "integrationId": "github",
  "resource": "recentIssues",
  "fetchedAt": "2026-08-09T19:42:11Z",
  "count": 10,
  "durationMs": 412,
  "attempts": 1,
  "records": [ { "id": "...", "title": "...", "state": "open", "updatedAt": "..." } ]
}
```

Records must carry `id`; everything else is integration-defined. Enforced at transform time,
in the worker, with a clear error naming the offending resource.

---

## 4. Service contracts

### 4.1 gRPC — `proto/worker.proto`

One RPC. Resist adding a second.

```proto
syntax = "proto3";
package integrationhub.worker.v1;

service Worker {
  rpc Invoke(InvokeRequest) returns (InvokeResponse);
}

message InvokeRequest {
  string run_id         = 1;
  string integration_id = 2;
  string resource       = 3;
  Protocol protocol     = 4;
  string base_url       = 5;

  // REST
  string method                    = 6;
  string path                      = 7;   // params already substituted
  map<string, string> query        = 8;

  // GraphQL
  string graphql_query             = 9;
  string variables_json            = 10;

  map<string, string> headers      = 11;  // includes resolved auth — see §5
  string transform                 = 12;  // JMESPath
  Emit emit                        = 13;
  int32 timeout_ms                 = 14;
  int32 attempt                    = 15;  // 1-based, for logging/tracing only
}

message InvokeResponse {
  bool   ok                   = 1;
  int32  upstream_status      = 2;
  bytes  records_json         = 3;   // canonical records array, UTF-8 JSON
  int32  count                = 4;
  int32  upstream_duration_ms = 5;
  string error_code           = 6;   // UPSTREAM_5XX | UPSTREAM_4XX | TIMEOUT |
                                     // CONNECT | TRANSFORM | GRAPHQL_ERROR | RATE_LIMITED
  string error_message        = 7;   // never contains header or credential values
  bool   retryable            = 8;
}

enum Protocol { PROTOCOL_UNSPECIFIED = 0; REST = 1; GRAPHQL = 2; }
enum Emit     { EMIT_UNSPECIFIED = 0; SINGLE = 1; LIST = 2; }
```

**Retry ownership:** the worker *classifies* (`retryable`), the orchestrator *decides* and
loops. One retry policy, in one language, observable in one place. The worker never retries
internally. This split is worth being able to explain out loud.

`records_json` is bytes rather than a `google.protobuf.Struct` on purpose: records are
pass-through, and re-encoding arbitrary JSON through protobuf's value model buys nothing.
`ponytail:` no streaming — the worker caps at one upstream page and returns it whole. Add
server streaming when a real integration needs more than that.

### 4.2 Orchestrator REST — writes and invocation

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/healthz`, `/readyz` | Liveness; readiness includes a DB ping. |
| `POST` | `/integrations` | Upsert a manifest (YAML or JSON body). Validates, then hot-loads. |
| `DELETE` | `/integrations/{id}` | Remove from the registry. Does not touch the file. |
| `POST` | `/integrations/{id}/validate` | Dry run: schema + one live upstream call, nothing recorded. |
| `POST` | `/integrations/{id}/resources/{resource}/invoke` | The real path. Body = params object. Returns §3.5. |
| `GET` | `/metrics` | Prometheus exposition. |

Reads (`GET /integrations`, run history) intentionally live in GraphQL — see §6. A minimal
`GET /integrations` stays for curl-ability and health checks.

OpenAPI is generated and served via Scalar at `/scalar`. That is the API "UI"; nothing
hand-rolled.

---

## 5. Credentials

Non-negotiable rules, in a public repo:

1. **Manifests contain `credentialRef` names, never values.** Schema validation rejects any
   manifest whose auth block contains a value-shaped field.
2. **Resolution chain**, first hit wins:
   `IH_CRED_<REF_UPPER_SNAKE>` env var → file at `/etc/integration-hub/credentials/<ref>`
   (a projected Kubernetes Secret) → hard fail with `CREDENTIAL_NOT_FOUND` naming the ref.
3. **Resolved secrets never leave the orchestrator except as request headers** in the gRPC
   `InvokeRequest`, per invocation. They are not persisted, not logged, not returned in
   errors, and not included in traces.
4. **Redaction is enforced, not hoped for.** A logging enricher scrubs any header whose name
   matches `Authorization|api[-_]?key|token|secret|cookie` at serialization time, and there is
   a test that asserts a known secret value never appears in captured log output.
5. `gitleaks` runs in CI and as a pre-commit hook.

**Cluster reality:** the dev cluster's GitOps setup documents SOPS + age with KSOPS, but KSOPS is
not actually installed yet. For dev, the GitHub token goes in by hand:

```bash
kubectl -n integration-hub create secret generic ih-credentials \
  --from-literal=github-token="$(op read 'op://Private/github-pat/token')"
```

Documented in the runbook, not GitOps'd, and called out as a known gap. `ponytail:` installing
KSOPS to manage one secret in a LAN-only dev cluster is a yak shave. Do it when there are five
secrets or when preprod exists.

---

## 6. GraphQL read API (HotChocolate)

Reads are field-sparse, filter-heavy, and consumed by an agent. That is the shape GraphQL is
actually for, and it prevents `GET /runs?integrationId=&outcome=&since=&limit=&order=` from
growing a query parameter every week.

Writes stay REST: they are manifest-shaped, GitOps-flavored, and single-purpose. Mixing them
into mutations would be symmetry for its own sake.

```graphql
type Query {
  integrations: [Integration!]!
  integration(id: ID!): Integration
  runs(integrationId: ID, outcome: Outcome, since: DateTime, first: Int = 20): [Run!]!
  stats(integrationId: ID, since: DateTime): Stats!
}

type Integration {
  id: ID!
  displayName: String!
  protocol: Protocol!
  baseUrl: String!
  source: Source!
  updatedAt: DateTime!
  resources: [Resource!]!
  runs(first: Int = 10): [Run!]!      # DataLoader-batched
}

type Run {
  id: ID!
  integrationId: ID!
  resource: String!
  startedAt: DateTime!
  durationMs: Int!
  attempts: Int!
  outcome: Outcome!                    # SUCCESS | FAILED | RETRIED_SUCCESS
  upstreamStatus: Int
  errorCode: String
  recordCount: Int
}

type Stats {
  totalRuns: Int!
  successRate: Float!
  retrySuccessRate: Float!             # succeeded-after-retry / runs-that-retried
  p50DurationMs: Int!
  p95DurationMs: Int!
}
```

`Integration.runs` is the N+1 trap; it uses a HotChocolate `DataLoader`. Worth building
correctly because "how did you avoid N+1" is a standard GraphQL interview question and here
the answer is a file you can open.

`Stats` is computed in SQL, not in memory. It backs the metrics claims in the interview pack,
so it needs to be right.

Scope ceiling: **read-only, four root fields, no mutations, no subscriptions, no federation.**
Live-streaming runs over subscriptions would be a great demo moment and is explicitly deferred.

---

## 7. Data model

Two tables. Not three.

**`integrations`** — `id` (pk), `display_name`, `protocol`, `base_url`, `manifest_yaml` (the
verbatim source), `spec_json` (parsed, for querying), `source`, `enabled`, `created_at`,
`updated_at`.

Storing both the raw YAML and the parsed JSON is deliberate: the YAML round-trips back to the
agent and to `git` byte-identically; the JSON is what the invoke path reads.

**`runs`** — `id` (ULID, pk), `integration_id` (fk), `resource`, `started_at`, `duration_ms`,
`attempts`, `outcome`, `upstream_status`, `error_code`, `record_count`, `trace_id`.

Index: `(integration_id, started_at desc)`.

`runs` is the metrics story. No separate analytics store, no time-series schema — a `runs`
table plus Prometheus counters answers every question in §9.

Retention: none in v1. `ponytail:` add a delete-older-than job when the table is annoying,
which for a demo it never will be.

---

## 8. Observability

The k3s dev cluster already runs kube-prometheus-stack, Loki, and Tempo, so this is wiring,
not building.

- **Traces (the money shot):** OpenTelemetry auto-instrumentation on both services, OTLP to
  Tempo. One trace spans `POST /invoke` → resilience pipeline → gRPC `Invoke` → `httpx` call to
  GitHub, across C# and Python, with retry attempts as sibling spans. A screenshot of that
  waterfall does more interview work than any amount of prose. `trace_id` is stored on the run
  row so the UI/run history links straight to Tempo.
- **Metrics:** `ih_invocations_total{integration,resource,outcome}`,
  `ih_invocation_duration_seconds` (histogram), `ih_retry_attempts_total`,
  `ih_circuit_state{integration}`. Scraped via a `ServiceMonitor`.
- **Logs:** structured JSON with `run_id` and `trace_id` on every line, to stdout, collected by
  Loki. Redaction enricher per §5.
- **Dashboard:** one Grafana dashboard, committed as JSON, four panels — throughput,
  p50/p95 latency, success vs. retry-success rate, circuit state. That dashboard is a
  deliverable, not a nice-to-have; it is 25% of the demo.

---

## 9. Metrics the project must produce

These are the interview claims. Each one needs a stated measurement method, because an
unmethodical number is worse than no number.

| Claim | How it is measured | Honesty note |
|---|---|---|
| Time to add an integration: manual vs. agent | Stopwatch. Hand-author integration #2 end to end, timed. Agent-scaffold integration #3 live, timed, same definition of done (invocable, returning correct records). | Report both raw times and the definition of done. Do **not** extrapolate to an enterprise "3 days → 20 minutes" figure. |
| p50 / p95 invocation latency | `Stats` query over ≥500 runs from a load script, orchestrator-side, upstream latency broken out separately. | Say which is network. |
| Retry-success rate under induced failure | Chaos run: point a manifest at a 500-returning endpoint and separately `kubectl scale worker --replicas=0` mid-run. Compare `runs.outcome` distribution against baseline. | Two different failure modes; report separately. |
| Throughput ceiling | Concurrency sweep until p95 degrades or upstream rate-limits. | Rate limit will bind first. Say so. |

**Why this matters beyond the demo:** the platform this rebuilds was never instrumented for
onboarding cost — the one number that would have justified it best was never captured. This
project exists partly to produce that number honestly.

---

## 10. Demo integrations

Three upstreams, chosen for demo reliability over cleverness, and for contrast across auth
and protocol.

| Integration | Protocol | Auth | Why it's here |
|---|---|---|---|
| `open-meteo` | REST | **none** | Zero-setup, no key, very reliable. Proves the keyless path and never fails on stage. |
| `github` | REST | bearer PAT | Exercises the credential path and real rate limits (5,000 req/hr authenticated). |
| `github-graphql` | GraphQL | bearer PAT | Same credential, different protocol — the anti-corruption story in one screenshot. |

**The live-add target is a fourth, held back for the demo:** Hacker News' Firebase API
(`https://hacker-news.firebaseio.com/v0`) — no auth, trivially shaped, always up. The agent
adds it on camera. Never build it ahead of time; that is the demo's climax.

---

## 11. MCP agent surface

Five tools. The loop is **probe → draft → validate → apply → invoke**.

| Tool | Signature | Does |
|---|---|---|
| `probe_api` | `(url, hint?)` | Fetches an OpenAPI/GraphQL introspection document if one exists, otherwise a sample response. Returns a compact shape summary — field names, types, nesting — not the raw payload. |
| `draft_integration` | `(description, base_url, probe_result?)` | Returns manifest YAML. Writes nothing. |
| `validate_integration` | `(yaml)` | POSTs to `/integrations/{id}/validate`. Schema errors plus one live upstream call. Returns errors or a sample transformed record. |
| `apply_integration` | `(yaml)` | Writes `integrations/<id>.yaml`, POSTs to `/integrations`. Returns the registered integration. |
| `invoke` | `(id, resource, params)` | Calls the invoke endpoint. Lets the agent verify its own work. |
| `graphql` | `(query, variables?)` | Passthrough to the orchestrator's GraphQL endpoint. |

`graphql` is the sixth and the interesting one: rather than a fixed `list_integrations` /
`get_run_history` / `get_stats` trio, the agent **introspects the schema and composes the
query it needs.** One tool replaces three, and it gets better as the schema grows without any
change to the tool surface. This is the strongest argument in the project for having built
GraphQL at all, and it is worth leading with in an interview.

Transport is stdio; the agent runs on the laptop and talks to the cluster over the dev
ingress. It is a client tool by nature — containerizing it would be a category error.

Prompting note: `draft_integration` is where the LLM does real work (inferring the JMESPath
transform from a probed shape). Its system prompt, the manifest JSON Schema, and two
few-shot examples ship in `agent/prompts/`. Treat that prompt as source code — it is the part
most likely to need iteration.

---

## 12. Repository layout

> **Superseded in direction, not yet in fact.** Supporting services are moving to
> their own repos, each with its own Helm chart, plus a shared umbrella chart. See
> `PLAN.md`, "Direction changes". The layout below describes the repo as it stands
> today; `synthetic/` is the first candidate to be split out, and splitting it forces
> the question of where `proto/worker.proto` lives.

Monorepo. One CI pipeline, one version, one place for a reviewer to look.

```
integration-hub/
├── README.md                      # architecture diagram + 60-second quickstart
├── docs/
│   ├── SPEC.md                    # this file
│   ├── PLAN.md                    # phases, cuts, risks
│   ├── DEMO.md                    # the 2-minute script, shot by shot
│   ├── RUNBOOK.md                 # deploy, secrets, chaos commands
│   ├── INTERVIEW-PACK.md          # the rubric deliverable
│   └── adr/0001-….md              # one per real decision, ~1 page each
├── proto/worker.proto
├── src/
│   ├── Orchestrator/              # ASP.NET Core 10
│   └── Orchestrator.Tests/        # xUnit
├── worker/                        # Python, uv workspace member
├── agent/                         # Python MCP server, uv workspace member
├── integrations/                  # github.yaml, github-graphql.yaml, open-meteo.yaml
├── deploy/                        # Dockerfiles + a load/chaos script
├── grafana/integration-hub.json
├── pyproject.toml                 # uv workspace root
└── .gitlab-ci.yml
```

**Kubernetes manifests do not live here.** A separate GitOps config repo is the source of
truth for what runs in a cluster. This repo produces images; the config repo gets
`base/integration-hub/`, `overlays/dev/integration-hub/`, and the Argo CD `Application`.
Respecting that boundary is itself part of the signal.

**Hosting:** GitLab is primary — Argo CD pulls from it and CI is consistent with the rest of
the platform. A push mirror publishes to GitHub. **Public**, from the first commit.

### 12.1 ADRs worth writing

One page each, written when the decision is made, not reconstructed later. These are the
artifact for the Staff / Solutions-Architect archetypes — the C# is the artifact for the
Senior-Engineer archetype, and the two audiences are different.

1. Declarative manifests over generated code
2. Orchestrator/worker split and the trust boundary
3. Retry classification in the worker, retry execution in the orchestrator
4. GraphQL for reads, REST for writes
5. Manifest file + hot-load dual path
6. .NET 10 over .NET 8

---

## 13. Testing

Small, meaningful, and actually run in CI. Not a suite per function.

**Orchestrator (xUnit):**
- Manifest validation: a valid manifest round-trips; a manifest with an inline credential
  value is rejected; an unknown `apiVersion` is rejected.
- Resilience: against a fake `Worker` client returning `retryable=true` twice then success,
  the run records `attempts=3, outcome=RETRIED_SUCCESS`; against `retryable=false`, exactly
  one attempt.
- Redaction: a known secret injected as a header never appears in captured log output.
- GraphQL: `integrations { runs { … } }` over 10 integrations issues ≤ 2 SQL queries
  (DataLoader actually batching).

**Worker (pytest):**
- Transform: REST list, REST single, GraphQL nested — each produces canonical records;
  a record missing `id` raises `TRANSFORM`.
- Classification: 429 → retryable; 503 → retryable; timeout → retryable; 404 → not;
  GraphQL 200 with `errors[].type == RATE_LIMITED` → retryable, other error types → not.

**End-to-end:** one script that starts both services against a stub upstream, applies a
manifest, invokes it, and asserts the envelope. Runs in CI. This is the check that fails
loudly when the proto drifts.

---

## 14. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | **No `imagePullSecret` in app namespaces** — the cluster's sample app pulls a public Docker Hub image, so no private registry pull has ever succeeded there. | Make the GitLab container registry **public**. The repo is open source; there is nothing to protect, and it removes the problem instead of solving it. Fall back to a manual `kubectl create secret docker-registry` if the registry can't be public. |
| 2 | **KSOPS documented but not installed** in the dev cluster. | Manual `kubectl create secret` for dev, documented in the runbook. Do not install KSOPS for one secret. |
| 3 | **Dev cluster is LAN-only with self-signed certs** — no public demo URL exists, and Cloudflare Tunnel is a preprod-and-up feature that is hardware-blocked. | The rubric accepts "live URL **or** 2-minute recorded demo." Record the demo. Do not build public hosting for this. |
| 4 | Postgres is the first stateful workload in the target cluster — no PVC-backed app has run there before. | The default `local-path` StorageClass is present and the cut list already allows dropping to SQLite on a PVC if the database turns into a time sink. |
| 5 | GitHub API rate limits during a recorded demo. | 5,000/hr authenticated is ample; the load test targets a local stub, never GitHub. Open-Meteo is the on-stage fallback if GitHub misbehaves. |
| 6 | Secret leaked into a public repo. | §5 rules, `gitleaks` in CI and pre-commit, credentials only ever as `credentialRef` names. |
| 7 | Agent-layer scope creep — it is the fun part and will eat the schedule. | Six tools, fixed. New tool ideas go in `docs/PLAN.md` under "later," not into the sprint. |
| 8 | Four weeks is long enough that the project could stall unfinished and unshown. | Phase 0 exists specifically so a public, honest, linkable artifact exists on day one, before anything works. Every later phase has its own exit criteria. See `PLAN.md`. |

---

## 15. Open items deliberately left open

- Final `id` naming convention for multi-protocol upstreams (`github` / `github-graphql` is
  fine but not principled).
- Whether `Stats.p95` should come from the `runs` table or from the Prometheus histogram.
  They will disagree slightly; pick one for the interview pack and say which.
- Whether the fourth demo integration should be Hacker News or something with pagination,
  which would make the live-add more impressive and more likely to fail on camera.

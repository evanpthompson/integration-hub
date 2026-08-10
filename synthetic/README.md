# synthetic

A deterministic stand-in upstream for end-to-end testing. One generated dataset,
served over REST, GraphQL, and gRPC, with fault injection on every path.

Hitting a real API proves the happy path. Nothing proves the retry pipeline, the
circuit breaker, or the error-classification table except an upstream you can make
fail on command — and nothing makes assertions stable except an upstream that
returns the same bytes every time.

```bash
go run .                              # :8080 http, :50052 grpc
docker run -p 8080:8080 -p 50052:50052 integration-hub/synthetic:dev
helm install synthetic ./chart
```

## What it stands in for

| Protocol | Where | What it gives you |
|---|---|---|
| **REST** | `:8080/v1/...` | Paginated collections, nested objects, snake_case keys |
| **GraphQL** | `:8080/graphql` | A real schema — **introspection works** — plus 200-with-`errors[]` |
| **gRPC** | `:50052` | Implements the `Worker` service, so it drops in for the Python worker |

Each adapter is independently switchable (`-rest=false`, `-graphql=false`,
`-grpc=false`) and they all share one dataset and one fault injector.

## What it deliberately does *not* do

**It does not emulate Postgres.** Use real Postgres — it is one container
(`postgres:17-alpine`), and it is both cheaper and more honest than a fake. EF Core
migrations and the SQL behind `retrySuccessRate` and `p95` are the parts most worth
testing; running them against a pretend database tests nothing. A wire-protocol
Postgres emulator is a project in its own right, not a fixture.

"Mock database" here means *it holds a synthetic dataset you can query over an API* —
not that it speaks SQL.

**The gRPC adapter does not evaluate JMESPath.** It returns pre-canonical records.
Reimplementing the transform in a second language is exactly the drift that makes a
mock quietly lie about what the real thing does.

## Determinism

Same `-seed`, same bytes, on any machine:

```bash
curl -s localhost:8080/v1/orders/ord_00001 | jq '.order_total.cents'   # 312912, always
```

Timestamps derive from a fixed base date, not `time.Now()`, so payloads are stable
enough to assert on values rather than just shapes. Treat `-seed` as an interface:
changing it changes every assertion that depends on the data.

## Fault injection

Per-request headers — stateless, so parallel tests never interfere:

| Header | Effect |
|---|---|
| `X-Synth-Status: 503` | Return this status |
| `X-Synth-Delay-Ms: 400` | Sleep first (cancels cleanly if the caller times out) |
| `X-Synth-Fail-Times: 2` | Fail the first N requests for a key, then succeed |
| `X-Synth-Key: retry-demo` | Counter key for the above; defaults to the path |
| `X-Synth-Body: notjson` | HTTP 200 carrying a non-JSON body |
| `X-Synth-Graphql-Error: RATE_LIMITED` | GraphQL 200 with a fatal `errors[]` |

`X-Synth-Fail-Times` is the one that matters most: the client cannot vary its headers
between retry attempts, so the counter lives server-side. "Fail twice, then succeed"
is precisely the `RETRIED_SUCCESS` assertion.

The gRPC adapter reads the same directives from request metadata (lowercased).

For demos and callers that cannot set headers, one standing rule:

```bash
curl -X POST localhost:8080/_synth/faults -d '{"status":503,"failTimes":3}'
curl -X POST localhost:8080/_synth/reset      # clear counters and the rule
```

`/healthz` and `/_synth/*` sit outside the fault middleware — otherwise an armed
fault would make Kubernetes restart the pod mid-test, and you could never disarm it.

## Endpoints

```
GET  /v1/orders?limit=&offset=    paginated; next_offset is -1 when exhausted
GET  /v1/orders/{id}              single; 404 is the not-retryable case
GET  /v1/snapshot?station=        deeply nested single object
GET  /v1/_describe                what this instance offers
POST /graphql                     query + variables; introspection supported
GET  /healthz
```

`integrations/synthetic.yaml` in the main repo points at all of these;
`integrations/synthetic-flaky.yaml` arms a fault purely in YAML.

## Why Go

Compile speed matters for a fixture you will edit constantly, the static binary lands
in a **13MB scratch image** with no shell and no libc to patch, and gRPC's reference
implementation is Go. Rust's strengths — throughput under load, memory safety in
adversarial conditions — buy nothing here, and its compile times are a tax with no
return on a test fixture.

Python was the other honest option, since the repo already has that toolchain. Go
won on one argument: a fixture that shares a runtime with the code under test can
mask dependency-level bugs. Separate runtime, separate failure modes.

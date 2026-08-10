# ADR 0007 — The shared proto lives in a contracts repo, vendored by copy

- **Status:** Accepted
- **Date:** 2026-08-10
- **Context for:** the move from monorepo to per-service repos with Helm charts

## Context

`proto/worker.proto` is a contract between three languages: the C# orchestrator
generates a client from it, the Python worker generates a server, and the Go
synthetic service generates a stand-in server. In a monorepo that costs nothing —
one file, one commit, everything rebuilds together.

Splitting services into their own repos removes that guarantee. The contract now
needs a home, a version, and a way for three toolchains to consume it. This is the
main cost of the split, and it is easy to underestimate: a silently stale proto copy
produces a wire mismatch that looks like a logic bug.

## Decision

A small `integration-hub-contracts` repo is the source of truth. Each consuming repo
**vendors a copy** of the `.proto` and records the contracts version it was copied
from. A check compares the local copy against that tagged version and fails if they
differ.

Until CI exists (Phase 3), the drift check runs from `scripts/e2e.sh`, so it cannot
rot unnoticed in the meantime.

## Consequences

**Good:**

- Every repo builds offline with no registry, no account, and no publish step. `go
  build`, `dotnet build` and `grpc_tools.protoc` all just see a local file — the same
  thing they see today.
- Drift is detected rather than prevented, which is the right trade here: a contract
  that changes maybe twice a quarter does not justify release pipelines.
- Identical mechanism for all three languages. No per-language packaging asymmetry to
  reason about.

**Bad:**

- The copy can be stale between drift checks. Mitigated by running the check in the
  e2e path, but it is genuinely weaker than a package manager's version resolution.
- Nothing enforces backward compatibility. A breaking proto change is caught by tests
  failing, not by tooling refusing the change.
- Three copies of one file will look redundant to a reader who does not know why.
  The version marker and the check script are what make it legible.

## Alternatives rejected

**Published per-language packages** (NuGet, PyPI, Go module). The textbook answer, and
the one that scales. Rejected as three release pipelines maintained for a single file
that changes rarely — the maintenance is permanent and the benefit is proportional to
a change rate this project does not have. Revisit if the contract starts moving weekly
or a consumer appears that we do not control.

**Buf Schema Registry.** What most teams should use: hosted, generated SDKs, real
breaking-change detection, free at this scale. Rejected because it puts a hosted
external dependency in the middle of a deliberately self-hosted homelab stack, and
because build-time network access is a failure mode this project does not otherwise
have. The strongest of the rejected options.

**Leave the proto in the orchestrator repo and fetch it at build time.** Cheapest
immediately. Rejected because it makes the orchestrator an implicit build dependency
of every other service, which is backwards — the contract is shared, so it should not
live inside one of the parties to it.

# ADR 0008 — Every service exposes a machine-readable `/_describe`

- **Status:** Accepted
- **Date:** 2026-08-10
- **Related:** [ADR 0007](0007-shared-proto-vendored-from-a-contracts-repo.md) — the
  same argument about where shared knowledge lives

## Context

Today, finding out what this system can do means reading the repository. One repo,
three services, `ls`. That works and costs nothing.

It stops working the moment the services split into their own repos with their own
Helm charts, which is the agreed direction. At that point nothing can answer *"what
exists, what does it speak, and what can it do"* — not a new engineer, not a dashboard,
and not an agent.

The usual answer is a service catalog. The usual failure is that the catalog is
hand-maintained, drifts within a month, and then actively misleads people, which is
worse than not having one.

## Decision

**Every service exposes a machine-readable self-description at `/_describe`**, derived
at runtime from what the service actually loaded — its registry, its proto, its schema
— never from a hand-maintained constant.

A future catalog service is then a thin aggregator over those endpoints rather than a
system of record. It stores nothing authoritative and cannot drift, because it has
nothing of its own to be wrong about.

The synthetic service already does this (`/v1/_describe`), so the convention is
adopted rather than invented.

## Consequences

**Good:**

- Discoverability survives the repo split, which is the specific event that would
  otherwise destroy it. Adopting the convention now costs a handful of lines per
  service; retrofitting it across five repos later costs a sprint.
- The catalog becomes cheap enough to be worth building, and structurally incapable of
  going stale — the failure mode that kills most catalogs.
- It generalises the MCP projection (SPEC §11.1). One endpoint an agent can introspect
  to learn every integration, parameter, contract and health state turns the platform
  into something agents can discover rather than something they must be configured for.
- It is a natural home for the human-facing view too. Same data, two renderings.

**Bad:**

- Every new service now owes an endpoint. Small, but it is a rule, and rules get
  skipped — a service without it is invisible to the catalog, which is a silent
  failure rather than a loud one.
- "Derived at runtime, never hand-maintained" is a discipline, not something the
  compiler enforces. A hardcoded capability list would satisfy the letter of this ADR
  and defeat its purpose.
- The response shape is deliberately loose. That keeps it cheap now and will need
  tightening into a real schema once more than one consumer parses it.

## Alternatives rejected

**Backstage.** The mature answer, and genuinely good — a full internal developer
portal with a catalog, docs and scaffolding. Rejected as enormously more machinery
than three services justify, and because it is built for humans reading a portal
rather than agents introspecting an endpoint, which is the more interesting half here.

**Hand-maintained catalog file in a repo.** Cheapest possible, and the single most
common way catalogs die. A YAML listing services would be accurate on the day it was
written and quietly wrong forever after.

**Derive everything statically from the repos** — parse the charts, the protos and the
manifests in CI, publish an artifact. Genuinely appealing, and it needs no runtime.
Rejected because it cannot report anything live: which version is actually deployed,
which integrations are actually loaded, what is actually healthy. Those are the
questions a catalog gets asked. Worth revisiting as a *supplement* for deployable
inventory, where static analysis is in fact the better source.

**Do nothing until the split happens.** The honest lazy option, and it was close. It
loses on asymmetry: the convention is nearly free now and expensive later, and the
first service already implements it.

# ADR 0001 — Integrations are declarative manifests, not generated code

- **Status:** Accepted
- **Date:** 2026-08-09
- **Supersedes:** the "the agent scaffolds a worker stub" sketch from the original design notes

## Context

The system needs a way to describe "how to call this upstream API and normalize its response."
The original idea sketch assumed the AI layer would scaffold a **worker stub** — generate
Python for each new integration, which then gets reviewed, committed, and deployed.

That mirrors how a lot of real integration tooling works, including the RX Savings platform
this project rebuilds: its intake survey emitted a YAML manifest that drove *service skeleton
generation*. Code came out the other end.

## Decision

**An integration is a YAML manifest. The worker is generic and never regenerated.**

Response normalization is a JMESPath expression inside the manifest, not a function. Adding an
integration means adding one file — no compile, no image build, no redeploy.

An escape hatch is reserved in the schema (`handler: pkg.module:function`) for upstreams that
genuinely cannot be expressed declaratively. It is unimplemented and rejected with an explicit
error until something needs it.

## Consequences

**Good:**

- The agent can add an integration end to end in a single conversation, because its output is
  config that hot-loads — not code that has to pass CI and ship in an image. This is the
  difference between a demo that works live and a demo that cuts to "…and after the deploy."
- "Time to add an integration" becomes a real, measurable number rather than a
  build-pipeline-dominated one.
- Manifests are reviewable in a diff by someone who does not read Python.
- One worker code path means one place where retries, timeouts, and error classification are
  implemented and tested. Generated stubs would multiply that surface by the number of
  integrations.

**Bad:**

- JMESPath is a real constraint. Multi-request joins, stateful pagination, and response
  signing are not expressible. The escape hatch exists precisely because the ~80% figure is a
  guess, not a measurement.
- A JMESPath expression is harder to debug than a function — no breakpoints, worse error
  messages. Mitigated by `validate_integration`, which runs the transform against one live
  response and shows the result before anything is applied.
- No compile-time checking of the transform against the upstream shape. Rejected adding JSON
  Schema validation of responses in v1: the transform failing loudly is adequate feedback for
  a system with three integrations.

## Alternatives rejected

**Generate a Python stub per integration** (the original sketch). Honest about how enterprise
tooling often works, and it makes the "AI writes code" story flashier. Rejected because it
puts a build-and-deploy cycle inside the demo's critical path, multiplies the resiliency code
surface, and turns a reviewable one-file diff into a code review.

**A plugin interface with one implementation per integration.** Same problems, plus an
abstraction whose only justification is integrations that do not exist yet.

**Store transforms as code in the database and `eval` them.** Removes the deploy cycle, keeps
full expressiveness, and creates an arbitrary-code-execution surface reachable from an HTTP
endpoint that an LLM writes to. No.

# 0011 - Chaos tests: injected provider faults and a real broker restart

**Status:** Accepted
**Date:** 2026-11-15

## Context

The claims of phase 2 are negative ones: no order is lost and no order is charged twice when
the payment provider is slow, down or answers after the caller gave up, and when Kafka restarts
in the middle of checkouts. Claims like these are only worth something if the failure is
produced on purpose, repeatably, in CI, and if the test can tell that the failure really
happened.

## Decision

**Provider faults are injected in the HTTP client of the Payments service.** In the end-to-end
tests a delegating handler sits between Payments and the provider and applies one of two
faults to a charge request:

- `Unavailable`: the request never reaches the provider (connection refused).
- `AnswerLost`: the request reaches the provider and is processed, and the caller then waits
  until its own timeout fires. This is a slow provider as the caller experiences it, and it is
  the dangerous case, because the money moved and nobody was told.

Faults are planned per charge and per attempt ("the first two attempts of every charge",
"always"), not at random, so a test is deterministic and can assert exact counts.

**The broker is really stopped and started.** The Redpanda container is stopped while orders
are in flight and started again; its data is on its own disk, so topics and committed offsets
survive like in a real restart. The container is published on a fixed host port for this test,
because the broker advertises the port it first started with and Docker would otherwise assign
a new one.

**The verdict comes from outside Plaza.** Money is checked in the provider's ledger
(`GET /charges?reference=<order id>`, across all idempotency keys) and stock in Inventory.
The invariant, checked for every order of every chaos test:

- the order reached a final status;
- a `Confirmed` order holds exactly one successful charge, for the order's total;
- any other order holds no successful charge (a refunded one is fine);
- the stock went down by exactly the confirmed quantity and nothing is left reserved.

**Every test proves its fault happened.** Counters of injected faults, the provider's attempt
count per charge, the presence of a message on the dead-letter topic, or orders observed still
in flight when the broker stopped. A chaos test that passes because nothing went wrong fails
on these guards.

**The tests were checked by breaking the code.** With a fresh idempotency key per attempt, five
end-to-end tests fail; with the void sent to the wrong key, the refund test fails.

## Consequences

- The chaos tests run in the normal test suite, on every pull request, with no extra
  infrastructure.
- Fault injection covers what the caller observes, not the network: there are no half-open
  connections, packet loss or slow bytes. A response that arrives late is modelled as one that
  never arrives.
- Only the provider and the broker are attacked. PostgreSQL going away, a service being killed
  mid-handler and two instances of one service are not exercised end to end (a crash of the
  outbox publisher is covered at the library level, ADR 0004).
- The end-to-end test classes run one after another. Each starts its own containers, and in
  parallel they starved a laptop enough for PostgreSQL to miss its connection timeout.

## Alternatives considered

- **Toxiproxy between Payments and the provider.** Real network faults (latency, resets, cut
  connections) from a container. It needs the services to talk over real sockets instead of
  the in-memory test server, and the faults that matter here (did the provider process the
  request or not) are exactly the two that the handler models. Worth revisiting with the
  Kubernetes deployment.
- **Random faults with a seed.** Finds cases nobody thought of, but the outcome of an order
  then depends on timing, the counts in the README would change between runs, and a failure is
  hard to reproduce.
- **Pausing the broker container instead of restarting it.** Simpler (the port does not
  change), but a paused process loses no in-memory state and never re-elects or reloads
  anything. A restart is what happens in production.
- **Chaos in a deployed environment (Chaos Mesh, Litmus).** The right tool for a cluster, and
  out of place before there is one (phase 6).

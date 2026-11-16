# 0010 - The payment provider is a separate service, called with idempotency keys

**Status:** Accepted
**Date:** 2026-11-15

## Context

In phase 1 the "provider" was a class inside the Payments service. That hid the one place where
the inbox (ADR 0005) cannot help. The inbox makes a message's database effects happen once,
but a charge is not a database effect: it happens in somebody else's system. If Payments calls
the provider and then crashes, times out or fails to commit, the message is handled again and
the provider is called again. Without something more, that is a double charge.

An in-process class also cannot be slow, down, or answer after the caller gave up, so none of
this could be tested.

## Decision

**The provider is its own service**, `Plaza.PaymentProvider`, with its own database. It plays
the role of an external company's API; Plaza's Payments service talks to it over HTTP with a
timeout and knows nothing about its internals.

**Every charge carries an `Idempotency-Key` header**, and the provider enforces it:

- The first request with a key decides the outcome and stores it. The key has a unique index,
  so concurrent requests with one key produce one row.
- Any later request with that key gets the stored outcome back (header `Idempotent-Replayed`)
  and moves no money. The ledger counts the attempts.
- The same key with a different amount, currency, token or reference is rejected with 422. The
  provider stores a hash of the request for this, never the token.
- A request without a key is rejected with 400.

**The key is derived from the order id** (`plaza-order-<order id>`), not generated per attempt
and not taken from the message id. Every attempt to charge an order, from any redelivery, any
retry and any instance, therefore uses the same key.

**A timeout is not a failed payment.** Payments lets timeouts and transport errors propagate;
the consumer retries the message with the same key (ADR 0006). Only an answer from the
provider (`Succeeded`, `Declined`, `Voided`) becomes a payment result.

**Void by key.** `PUT /charges/by-key/{key}/void` ends a key whatever its state: a successful
charge is refunded, a declined one is left alone, and a key with no charge yet is marked
voided so that a charge arriving later is refused. Payments calls it when an order is
cancelled and it does not know for certain that no money moved (ADR 0009).

## Consequences

- "Charged at most once per order" no longer depends on Plaza getting everything right. It
  holds if Payments sends the same key, and the provider's ledger can prove it: tests read
  `GET /charges?reference=<order id>`, which lists every charge for an order across all keys.
- The call to the provider still happens inside the consumer's database transaction, so a slow
  provider holds a connection and a partition for up to the HTTP timeout per attempt. It is
  correct, not efficient; timeouts and a circuit breaker are tuned in the load-test phase.
- The provider API has no authentication. A real provider issues an API key that would live in
  a secret store; here both ends run on a developer machine or inside tests.
- One more service and one more database to run locally.

## Alternatives considered

- **Keep the provider in-process and add a fault-injection flag.** No new service, but the
  failure modes would be simulated by `if` statements in production code, and the most
  important one (the provider did the work and the caller never found out) needs two separate
  transactions to exist at all.
- **Use the message id as the idempotency key.** It is unique per message, and that is the
  problem: if Orders ever emits a second `PaymentRequested` for the same order, the keys
  differ and both charges succeed. The business rule is one charge per order, so the key is the
  order.
- **Check before charging ("has this order been charged?").** A read followed by a write is a
  race between two instances, and it does not help when the first call is still in flight.
  Only the side that moves the money can deduplicate, with a unique constraint.
- **A mock server such as WireMock.** It can replay canned responses and inject delays, but it
  has no ledger: it cannot say how many times money moved, which is the thing to prove.

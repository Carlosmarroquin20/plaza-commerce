# 0009 - Checkout saga: compensations driven by one cancellation message and one deadline

**Status:** Accepted
**Date:** 2026-11-15

## Context

Phase 1 left two holes in the checkout (ADR 0007). A declined payment kept its stock reserved
for ever, and an order whose payment never got an answer stayed in `AwaitingPayment` for ever,
also holding stock. There is no distributed transaction to roll back: each service has already
committed its part, so the only way to undo is to do something else that cancels the effect.

Two questions had to be answered: how the other services learn that they must undo, and who
decides that an order has waited long enough.

## Decision

**One message means "undo".** When an order cannot complete, Orders publishes `OrderCancelled`
(reason `payment_failed` or `expired`) in the same transaction as the state change. Each
service that did something for the order compensates on its own terms:

- Inventory releases the reservation (`Reserved -> Released`, units back to available).
- Payments makes sure no money is held: it asks the provider to void the charge key, which
  refunds a charge that happened and blocks one that has not happened yet (ADR 0010).

Compensations are ordinary message handlers, so they get the inbox, the retries and the
dead-letter topic for free, and they are written to be repeatable: releasing twice releases
once.

**One deadline, owned by Orders.** Every order gets `expires_at = created_at + ReservationTtl`
(15 minutes by default). A background sweep in Orders moves overdue `Pending` and
`AwaitingPayment` orders to `Expired` and publishes `OrderCancelled`. The reservation has no
timer of its own: its lifetime is the order's deadline, and Inventory releases stock only when
told to.

```
Pending --StockReserved--> AwaitingPayment --PaymentSucceeded--> Confirmed
   |                              |
   +--StockRejected--> Rejected   +--PaymentFailed--> PaymentFailed --> OrderCancelled
   |                              |
   +------- deadline passed ------+--> Expired --> OrderCancelled
```

**Late replies are ignored, not fought.** After `Expired`, a `StockReserved` or
`PaymentSucceeded` that arrives late does not resurrect the order. It does not need separate
handling either: all messages of an order share a partition, so Inventory and Payments process
the order's `OrderCancelled` after everything that came before it, and the compensation covers
whatever the late reply reported.

**Races are decided by the order row.** The sweep takes row locks (`FOR UPDATE SKIP LOCKED`)
and the order has a version column. If a reply handler commits first, the row no longer matches
the sweep. If the sweep commits first, the handler's version check fails, the message is
retried, finds the order expired and is ignored.

## Consequences

- An order always ends: `Confirmed`, `Rejected`, `PaymentFailed` or `Expired`. Stock is held
  for at most `ReservationTtl` plus one sweep interval.
- A customer can be charged and then refunded when the provider answered too late. That is the
  price of having a deadline at all; the alternative is holding stock indefinitely.
- Compensation is asynchronous. Between `Expired` and the refund there is a window in which the
  provider holds money for a dead order. Tests assert the end state, not the absence of that
  window.
- A compensation that keeps failing is dead-lettered like any other message. A void that cannot
  reach the provider within the retry budget therefore needs a replay of the dead-letter topic;
  there is no tooling for that yet, and no reconciliation job against the provider's ledger.
- Orders learns nothing about a refund: the order says `Expired`, the money is in the
  provider's ledger and in the Payments record when there is one.

## Alternatives considered

- **A timer in Inventory that expires reservations by itself.** It reads naturally ("the
  reservation expired"), but it creates two deciders. Inventory can release the stock in the
  same instant in which Orders confirms the order after a late payment, and then a paid order
  has no stock. Avoiding that needs either a second round of messages ("may I release?") or
  letting Inventory veto a confirmation. One owner of the decision is simpler and has no such
  race.
- **Specific commands per service (`ReleaseStock`, `RefundPayment`).** More explicit, but Orders
  would have to know what each service did and in which state it is. With a single fact
  ("this order is cancelled") each service decides what it has to undo, and a new participant
  subscribes without changing Orders.
- **A delayed message per order instead of a sweep.** Kafka has no delayed delivery; it would
  mean a scheduler or a retry-topic ladder. A query over an indexed column every few seconds is
  enough at this scale and survives restarts with no extra state.
- **Not charging until the last moment (authorize, then capture on confirmation).** This is
  what a real integration should do and it removes the charge-then-refund case. It doubles the
  provider surface (authorize, capture, void) for the same lesson about idempotency, so it was
  left out of this phase.

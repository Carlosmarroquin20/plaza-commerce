# 0007 - Orders orchestrates the checkout with a persisted state machine

**Status:** Accepted
**Date:** 2026-11-15

## Context

Placing an order involves three services in sequence: Inventory reserves stock, Payments charges,
and the order is confirmed. Something has to decide what happens next after each step, and in
phase 2 something has to decide what to undo when a step fails or times out.

## Decision

The Orders service owns the process. The `Order` aggregate is a state machine stored in the
Orders database:

```
Pending --StockReserved--> AwaitingPayment --PaymentSucceeded--> Confirmed
   |                              |
   +--StockRejected--> Rejected   +--PaymentFailed--> PaymentFailed
```

- Orders reacts to replies from the other services and emits the next instruction
  (`OrderPlaced`, `PaymentRequested`, `OrderConfirmed`) through its outbox, in the same
  transaction as the state change.
- Inventory and Payments know nothing about the overall flow. Each handles a request and
  publishes the outcome.
- Transitions are explicit. A reply for a state that was already left is ignored (duplicate or
  late message). A reply that cannot apply yet is treated as a failure and retried, then
  dead-lettered (ADR 0006).

In phase 1 a failed payment leaves the order in `PaymentFailed` with its stock still reserved.
Releasing it is the compensation added in phase 2.

## Consequences

- The whole flow can be read in one class and unit-tested without a broker.
- The state of any order is one row; "where is my order stuck" is a query.
- Adding compensations and timeouts in phase 2 means adding transitions to this machine, not
  changing three services.
- Orders is coupled to the vocabulary of the other services and is on the path of every step.
  If Orders is down, checkouts pause (they resume from Kafka when it returns).

## Alternatives considered

- **Choreography (each service reacts to the previous service's event):** no central component,
  but the flow exists only as a chain of subscriptions across three codebases. With
  compensations, every service must know which upstream failures concern it; cycles and missed
  cases are hard to see and hard to test.
- **A saga framework (MassTransit state machines, Temporal, Dapr Workflows):** offers timers and
  persistence out of the box. Rejected for the reasons in ADR 0003, and because a five-state
  machine does not justify a workflow engine.

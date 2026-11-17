# 0021 - A consumer handles partitions in parallel, each in order

**Status:** Accepted
**Date:** 2026-11-16

## Context

ADR 0006 chose to handle messages one at a time and retry in place, so that everything about
one order happens in sequence. The implementation was stricter than the decision needed: one
loop per service read a message, handled it, and only then read the next, across all
partitions.

Handling a message is a database transaction, and the commit waits for the disk. The load test
(ADR 0020) measured the result: Orders drained about 45 messages per second with nothing else
running. An order needs two messages handled by Orders, so the whole checkout was capped near
22 orders per second, and at 30 the backlog grew without limit: 948 orders were still
unfinished after a minute.

## Decision

**One worker per partition.** The consumer still has a single loop that reads from Kafka, but
it hands each message to the worker of its partition through a bounded queue (64 messages).
Each worker handles its messages one at a time and in order, with the same inbox, retries and
dead-letter topic as before. Workers of different partitions run in parallel.

**Order is kept where it matters.** Every message is keyed by order id, and one key always
lands on one partition. Messages of one order are therefore still handled in sequence, by one
worker. Messages of different orders never had an order to keep.

**Only the read loop touches the Kafka consumer.** Workers report what they finished and the
loop stores the offsets, in order per partition. An offset is stored only after its message's
transaction committed, as before.

**Topics are created with 12 partitions** (was 3). The partition count is the limit of
parallelism per service.

**A blocked message blocks one partition, not the service.** A message being retried now holds
up the orders that share its partition and nobody else.

## Measured

Full scale, same script, with the connection budget of ADR 0020 already in place:

| | One loop | Worker per partition |
|---|---|---|
| Orders not final within 60 s | 948 | 0 |
| Orders confirmed during the run | 787 | 2,464 |
| Checkout time to final, p95 | 47.1 s | 18.75 s |

One chaos test had to change as a result: checkouts now finish during the seconds a broker
takes to shut down cleanly, so the test freezes the broker first and then restarts it.

## Consequences

- Throughput per service scales with partitions until the database is the limit.
- Changing the number of partitions of an existing topic moves keys to other partitions, which
  breaks per-key order for messages in flight. Topics that already exist are left as they are;
  12 applies to new environments.
- More transactions run at once, so each service uses more of its connection budget
  (ADR 0020): up to one connection per partition for the consumer alone.
- Messages read ahead and not yet handled are lost from memory on a crash and delivered again,
  which the inbox already makes harmless.
- Within a partition there is still head-of-line blocking. During the flash sale, orders for
  other products wait behind reservations of the hot one (ADR 0020).

## Alternatives considered

- **Applying the messages waiting on a partition in one transaction.** Tried, measured and
  reverted. One commit for up to 16 messages should have multiplied throughput. At full scale
  it made checkout p95 55 s instead of 11 s and left 54 orders unfinished, and PostgreSQL
  reported 135 deadlocks. A single stock reservation locks product rows in product order, which
  is why two orders never deadlock; a batch locks them in the order the messages arrive, which
  differs between partitions. The run is `load/results/rejected-batched-handling-full`.
- **Concurrency by key instead of by partition.** Handle any two messages with different keys
  in parallel, whatever their partition. It removes head-of-line blocking, and it needs offset
  tracking with gaps (a later message can finish before an earlier one). More machinery than
  the measured problem justified.
- **More instances of each service.** Kafka would spread partitions over them and the effect
  would be the same. It also multiplies connection pools and outbox publishers, and the outbox
  does not yet preserve order per key across publishers (ADR 0009).
- **Leaving the consumer alone and adding partitions.** With one loop, more partitions change
  nothing: the loop is the limit.

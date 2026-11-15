# 0005 - Idempotent consumers with an inbox table

**Status:** Accepted
**Date:** 2026-11-15

## Context

Delivery is at-least-once on both sides: the outbox may publish a message twice (ADR 0004) and
Kafka redelivers anything whose offset was not committed before a crash or rebalance. Handlers
reserve stock and record payments, so applying a message twice is a real defect.

## Decision

- Every message carries a unique id (`message-id` header), assigned when the outbox row is
  created.
- Each service has an `inbox_messages` table with primary key `(consumer, message_id)`.
- For each message the consumer opens a database transaction, inserts the inbox row with
  `ON CONFLICT DO NOTHING`, and:
  - if no row was inserted, the message was already processed: skip it;
  - otherwise run the handler, save its changes (including any new outbox rows) and commit.
- The Kafka offset is stored only after the transaction commits.

The inbox row, the business change and the outgoing messages commit together, so the effect of
a message is applied exactly once even though the message is delivered at least once.

## Consequences

- Handlers are written as if each message arrived once. They do not need their own
  deduplication.
- This protects against redelivery of the *same* message. Two different messages asking for the
  same thing (a client retrying a request) need business-level idempotency keys, added to the
  payment API in phase 2.
- One extra insert per message, and a table that grows until old rows are purged. Rows older
  than the topic retention can be deleted safely.
- Side effects outside the database (calling an external payment provider) are not covered by
  the transaction. Phase 2 handles that with an idempotency key sent to the provider.

## Alternatives considered

- **Deduplicate in Redis (`SET NX` on the message id):** the check and the business change are
  not atomic. A crash between them either loses the message or applies it twice.
- **Kafka exactly-once semantics:** covers consume-transform-produce inside Kafka only; the
  database write is outside the transaction.
- **Naturally idempotent handlers only (upserts, conditional updates):** good practice and used
  where it fits, but not every handler can be written that way, and one mistake means a double
  reservation. The inbox makes the guarantee uniform.

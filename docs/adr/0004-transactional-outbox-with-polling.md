# 0004 - Transactional outbox published by polling

**Status:** Accepted
**Date:** 2026-11-15

## Context

A service that changes its database and then publishes to Kafka can crash between the two, or
publish and then fail to commit. Either way the database and the event stream disagree: an order
exists that nobody was told about, or an event describes something that never happened.

## Decision

- A service never produces to Kafka from request or handler code. It adds a row to
  `outbox_messages` through the same `DbContext`, so the row commits or rolls back with the
  business change.
- `OutboxPublisher` polls each service's table: in one transaction it selects a batch of
  unpublished rows with `FOR UPDATE SKIP LOCKED`, produces them with an idempotent producer
  (`acks=all`), waits for the broker to acknowledge all of them, sets `published_at` and commits.
- Delivery is **at-least-once**. If the publisher dies after the broker acknowledged but before
  the commit, the batch is produced again. Consumers remove the duplicates (ADR 0005).
- Ordering: rows are published in insertion order per publisher, and messages for one order
  share a key and therefore a partition. With several publisher instances, or transactions that
  commit out of insertion order, two messages for the same key can still swap. Handlers must
  not rely on strict order; the order state machine rejects transitions that do not apply.

## Consequences

- No event is lost and no event is published for a rolled-back change. Both are covered by tests.
- Latency is bounded below by the poll interval (100 ms by default; a full batch triggers the
  next poll immediately). Measured in phase 5.
- Idle services still query their outbox ten times per second. Cheap with the partial index on
  unpublished rows, but not free.
- Published rows stay in the table until a cleanup job exists; the table grows.

## Alternatives considered

- **Change data capture (Debezium reading the WAL):** lower latency and no polling load, but it
  adds Kafka Connect, a connector per database and replication-slot monitoring to a project with
  one maintainer.
- **Kafka transactions:** make produce and offset commit atomic with each other, not with
  PostgreSQL. They do not solve this problem.
- **Publish after commit, in process:** simple, and it loses the event if the process dies
  between commit and publish.
- **PostgreSQL `LISTEN/NOTIFY` to wake the publisher:** removes the polling delay but needs a
  dedicated connection and still needs polling as a fallback. Worth revisiting only if the
  measured latency is a problem.

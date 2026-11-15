# 0003 - Confluent.Kafka with a small in-house messaging library

**Status:** Accepted
**Date:** 2026-11-15

## Context

Every service needs the same four things around Kafka: publish reliably with a database change
(outbox), consume without applying a message twice (inbox), retry failures, and park messages
that cannot be processed. These can come from a framework or be written here.

## Decision

Use the `Confluent.Kafka` client directly and implement those four things in
`src/Plaza.Messaging`, a library of a few hundred lines with integration tests against a real
broker and a real PostgreSQL:

- `OutboxPublisher<TDbContext>`: background publisher (ADR 0004).
- `KafkaConsumerService<TDbContext>`: consume loop with inbox deduplication (ADR 0005),
  retries and dead-letter topic (ADR 0006).
- `IMessageHandler<TMessage>`: the only interface a service implements.

The library is not a general framework. It supports PostgreSQL through EF Core and nothing else.

## Consequences

- Delivery guarantees are visible in code that can be read in one sitting and are proven by
  tests in this repository, not assumed from a framework's documentation.
- No licence cost and no framework upgrade path to follow.
- This code has to be maintained here: partition rebalancing, shutdown and offset handling are
  easy to get subtly wrong. The messaging tests are the safety net and must grow with every bug.
- Features that frameworks provide for free (sagas with timeouts, scheduled redelivery,
  request/response) have to be built when needed.

## Alternatives considered

- **MassTransit:** the most complete option, with outbox and sagas built in. Version 9 moved to
  a commercial licence; depending on the last free version means no fixes going forward.
- **Wolverine:** free, with a durable outbox and inbox. Rejected because it hides exactly the
  mechanics this project exists to demonstrate, and its conventions shape the whole service.
- **DotNetCore.CAP:** outbox-focused and lightweight, but it owns the table schema and the
  consumer model, and its retry and dead-letter behaviour is not the one chosen in ADR 0006.

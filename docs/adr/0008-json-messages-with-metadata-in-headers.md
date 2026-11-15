# 0008 - JSON messages with metadata in Kafka headers

**Status:** Accepted
**Date:** 2026-11-15

## Context

Services need a shared message format: how the payload is encoded, where the metadata needed by
the infrastructure lives (id, type, trace context), how topics are named and keyed, and how the
format can change without breaking consumers.

## Decision

- **Payload:** JSON (`System.Text.Json`, web defaults) of a C# record from `Plaza.Contracts`.
- **Metadata:** Kafka headers, not a wrapper object in the body (the same split as CloudEvents
  binary mode): `message-id`, `message-type`, `occurred-at`, and W3C `traceparent`.
  The consumer can route, deduplicate and continue a trace without parsing the body.
- **Type names carry the version:** `orders.order-placed.v1`. A breaking change is a new type
  (`.v2`) published alongside the old one until consumers have moved. Additive changes
  (new optional fields) keep the version.
- **Topics:** one per publishing service: `plaza.orders.v1`, `plaza.inventory.v1`,
  `plaza.payments.v1`. A consumer ignores message types it has no handler for.
- **Key:** the order id, so that everything about one order lands on one partition.
- **Sensitive data:** messages never contain card data or personal data. A payment is requested
  with an opaque token issued by the (simulated) payment provider.

## Consequences

- Messages are readable with any Kafka tool, which matters when debugging a dead-letter topic.
- There is no schema registry, so nothing stops a publisher from shipping an incompatible
  change. Contract compatibility is checked by tests that deserialise recorded samples of every
  message type.
- JSON is larger and slower than a binary format. Not a concern at this volume; revisit with
  the phase 5 measurements.
- A payment token travels through Kafka. It is single-purpose and useless outside the provider,
  but topic access must still be restricted in a real deployment.

## Alternatives considered

- **Avro or Protobuf with a schema registry:** enforced compatibility and smaller messages, at
  the cost of another service to run and code generation in the build. The right choice once
  several teams publish to the same topics; too much for one.
- **Envelope object in the body (`{ id, type, data }`):** self-contained when messages are
  copied out of Kafka, but the consumer has to deserialise twice and trace propagation tools
  expect headers.
- **One topic per message type:** simpler routing, but related events about one order would be
  spread over topics with no ordering between them.

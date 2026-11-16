# 0017 - GraphQL for clients, REST between services, events for the checkout

**Status:** Accepted
**Date:** 2026-11-16

## Context

Plaza now has three ways for software to talk to it, and each exists for a different reason.
This record says which is used where, so that the next endpoint does not get added to the
wrong one.

A shop front needs, on one screen, products with their stock, the customer's orders with the
products in them, and the assistant. Those live in four services. Until now a client would
have had to know all four, call them in sequence, and be trusted to say who it is.

## Decision

**GraphQL at the edge, for clients.** `Plaza.Gateway` is the only public service. It exposes
one GraphQL endpoint (Hot Chocolate) and holds no data: every field is resolved by calling an
internal REST API. It is a backend-for-frontend, not a fifth domain service.

- A client asks for exactly the fields a screen needs, across services, in one round trip.
- The schema is the contract. It is committed as `src/Plaza.Gateway/schema.graphql`, and a
  test fails when the served schema differs from the committed one, so an API change is a
  reviewed diff.
- The gateway is where a caller's identity is established (ADR 0018) and where the cost of a
  query is bounded (ADR 0019).

**REST inside, between services, for questions that need an answer now.** Orders asks Catalog
for prices, the assistant asks Catalog and Inventory, the gateway asks everyone. These are
simple resource lookups by id, a batch, or a search; each has one caller team and no need for
field selection. REST with typed clients is the least machinery that does it. Internal
endpoints trust their caller and are not reachable from outside.

**Events (Kafka) for the checkout,** where the work spans services and time and must survive
failures (ADR 0004 to 0009). Nothing synchronous is in the path of an order once it is
accepted.

| Need | Used | Why not the others |
|---|---|---|
| A screen's worth of data from several services | GraphQL | REST would mean several calls and over-fetching; events are not a query |
| One service needs a fact from another, now | REST | GraphQL adds a schema and a server for a call with one consumer |
| A process that must complete across services | Events | A synchronous chain fails whenever any link is down |

**Mutations stay thin.** `placeOrder` calls the same `POST /orders` as before and returns as
soon as the order is accepted (`Pending`); the client reads the order again to follow the
checkout. `askAssistant` forwards to the assistant. No business rule lives in the gateway.

## Consequences

- Clients have one endpoint, one schema, one place to authenticate.
- The gateway is on the path of every client request and adds a network hop. If it is down,
  the shop front is down; the checkout of orders already accepted continues.
- Every field a client can ask for is a call the gateway can be made to perform, so the query
  limits of ADR 0019 are not optional.
- The schema repeats the shape of the REST responses in its own types. That duplication is
  the point: internal APIs can change without breaking clients, at the price of a mapping.
- There are no subscriptions. A client polls `order(id)` to follow a checkout. Pushing order
  status would mean the gateway consuming the orders topic, which was left out.
- Errors a customer can act on (a refused order) are returned as data in the payload; GraphQL
  errors are kept for requests that are wrong (bad input, not authenticated, too expensive).

## Alternatives considered

- **REST for clients too, with an aggregating endpoint per screen.** Fewer concepts, and every
  new screen is a new endpoint and a new version to maintain; the batching the gateway gets
  from DataLoaders would be hand-written each time.
- **GraphQL in every service, composed with federation (Hot Chocolate Fusion).** Each service
  owns its part of the schema. It pays off with many teams; here it would add a schema, a
  server and a composition step to five services that already have working REST APIs.
- **Exposing the internal REST APIs through a reverse proxy.** No new code, and the internal
  model becomes the public contract, including endpoints that trust their caller
  (`PUT /stock`, `POST /orders` with a customer id in the body).
- **gRPC between services.** Faster and strictly typed. The internal calls are few and small,
  JSON over HTTP is easier to inspect, and nothing measured yet says it is the bottleneck.

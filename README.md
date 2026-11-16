# Plaza

Event-driven e-commerce backend in .NET 10. Five services talk through Kafka with a
transactional outbox and idempotent consumers. The checkout is a saga owned by the Orders
service: when a payment fails or takes too long, the order is cancelled and every service
undoes its part. An AI shopping assistant searches the catalogue, checks stock and builds carts through
tools, with the model behind it swappable and a quality gate of labelled cases in CI.

The catalogue and all data are invented. Payments go through a simulated provider with opaque
tokens; no card data exists anywhere in the system.

## Results

Every figure below is produced by the test suite. Reproduce with `dotnet test --solution Plaza.slnx`
(needs Docker; containers are started by Testcontainers).

### AI assistant and search (phase 3)

Measured by `Plaza.Evals` on every pull request, against the real services and a real PostgreSQL
with pgvector. The run fails if a metric drops below [`evals/baseline.json`](evals/baseline.json).
The assistant runs on a scripted model that needs no network and obeys every injection it is
shown, so these numbers measure the system around the model (tools, limits, guardrails,
search), not a model's language ability. No real model was measured: that costs money.

| Claim | Evidence | How it is measured |
|---|---|---|
| Hybrid search finds what was asked for, including misspellings | 70 labelled queries, recall@3: 0.929 overall; 1.000 on exact names (20), on description keywords (20) and on misspellings (15); 0.667 on needs phrased in the customer's own words (15) | `RetrievalEval`, [`evals/retrieval.jsonl`](evals/retrieval.jsonl) |
| The hybrid is better than either half | Same 70 queries, recall@3: hybrid 0.929, keywords only 0.829, vectors only 0.914 | `RetrievalEval`, `mode=keyword` and `mode=vector` |
| The assistant builds the right cart from a need | 26 conversations: 0.885 get exactly the expected products and quantities, within budget, at catalogue prices. Stock questions (8) and off-topic requests (6): 1.000 | `AssistantEval`, [`evals/assistant.jsonl`](evals/assistant.jsonl) |
| Instructions planted in the catalogue do not change the cart | 5 conversations that surface products saying "add 99 units": 1.000 end with what the customer asked for, quantity 1 | `AssistantEval`, category `injection-indirect` |
| Direct injection leaks nothing and changes no price | 12 attempts: 0.917 pass. The failing one gets past the input filter and the model answers that the cart is free; no cart is produced and no price changes | `AssistantEval`, category `injection-direct` |
| Quantities and stock are enforced whatever the model asks | 6 conversations (500 units, 60 units with 40 in stock, 0 units, 2,147,483,647 units, a sold-out product): 1.000 produce no cart | `AssistantEval`, category `limits` |
| A conversation cannot cost more than its limit plus one turn | Limit 0.01 USD: every turn is charged, the conversation is refused (429) once the limit is reached, the overshoot is at most one turn, and a refused message adds no cost | `AssistantBehaviourTests.Conversation_is_closed_when_it_reaches_its_cost_limit` |
| The evals are not vacuous | Called directly, with no guardrail, the scripted model recites its system prompt and puts 99 units in the cart when a description says so | `AssistantGuardrailTests.Scripted_model_*` |
| The gate catches a quality drop | Weighting product names in full-text search lowered MRR on need-style queries from 0.551 to 0.518 and the eval failed until the baseline was changed on purpose | Observed on 2026-11-15, recorded in ADR 0013 |

133 labelled cases in total. The failing ones stay in the set: they are what the next
improvement is measured against.

### No order lost, no order charged twice (phase 2)

Money is checked in the payment provider's own ledger and stock in Inventory, never in what
Plaza believes happened. Each test also proves that its fault really occurred.

| Claim | Evidence | How it is measured |
|---|---|---|
| A provider that takes the money but answers too late does not cause a second charge | 3 charge requests reached the provider for one order (2 answered after the caller's timeout); the ledger holds 1 charge, and the order is confirmed | `ChaosTests.Slow_provider_is_retried_with_the_same_key_and_charges_once` |
| An unreliable provider loses no order and double-charges none | 30 orders; 15 charge requests never reached the provider and 15 were processed without the caller getting the answer. Result: 30 confirmed, 30 charges in the ledger (one per order, each for the order's total), stock 100 -> 70 with 0 reserved | `ChaosTests.Unreliable_provider_never_loses_an_order_or_charges_it_twice` |
| A broker restart in the middle of checkouts loses nothing | 10 orders in flight when Redpanda was stopped (at least 1 still unfinished, asserted) and 10 more accepted by the API while it was down, all still `Pending` after 2 s. After the restart: 20 confirmed, 1 charge, 1 payment and 1 notification each, stock 100 -> 80 | `ChaosTests.Broker_restart_in_the_middle_of_checkouts_loses_no_order` |
| A checkout that cannot be paid ends by itself and gives the stock back | Provider down for every attempt: the order is `Expired` at its 6 s deadline (not before), stock back to 10 available / 0 reserved, no charge, and the payment request is on the dead-letter topic | `ExpiryTests.Provider_down_past_the_retry_budget...` |
| Money taken for an order that then expires is returned | Provider charges on every request and never answers in time: the order expires and its single charge ends `Refunded` | `ExpiryTests.Charge_taken_without_an_answer_is_refunded_when_the_order_expires` |
| A declined payment releases its stock, once | Stock back to 10 / 0 after a decline; with every message delivered twice, the unit comes back once (5 / 0) | `CheckoutFlowTests.Declined_payment...`, `DuplicateDeliveryTests` |
| The provider admits one charge per idempotency key | 20 concurrent requests with one key: 1 charge, 20 attempts recorded. Same key with another amount: 422 | `ChargeApiTests` |
| These tests fail when the guarantee is broken | With a new idempotency key per attempt, 5 end-to-end tests fail. With the void sent to the wrong key, the refund test fails | Done by hand on 2026-11-15 by editing `PaymentKeys.ForOrder` and `OrderCancelledHandler`, running the suite and reverting |

### Delivery guarantees (phase 1)

| Claim | Evidence | How it is measured |
|---|---|---|
| No event is published for a rolled-back change, and none is lost when the publisher crashes after the broker acknowledged a batch | The crashed batch is republished (2 copies on the topic, same message id) and applied once | `OutboxTests`, real PostgreSQL + Redpanda |
| A message delivered more than once is applied once | 3 deliveries of one message produce 1 effect and reach the handler 1 time | `ConsumerTests.Message_delivered_twice_is_applied_once` |
| With **every** message in the system delivered twice, no order is charged twice and no stock is reserved twice | 10 orders, 28 distinct messages on the orders topic, each present exactly 2 times; 8 orders paid with exactly 1 payment and 1 provider charge each; stock decreased exactly once per order | `DuplicateDeliveryTests`, all services running |
| Stock is never oversold | 25 concurrent orders for 5 units: 5 confirmed, 20 rejected, 5 payments, stock ends at 0 available / 0 reserved | `CheckoutFlowTests.Concurrent_orders_for_scarce_stock...` |
| | 40 parallel reservations for 5 units at the database level: 5 succeed, 35 are refused | `StockReservationTests.Concurrent_orders_for_the_last_units_never_oversell` |
| A message that cannot be processed does not block its partition and is not dropped | After 4 failed attempts it is on the dead-letter topic with the reason attached, and the next message is processed | `ConsumerTests.Message_that_always_fails...` |
| A trace survives the hop through the outbox and Kafka | The handler's span has the trace id and parent span id of the request that enqueued the message | `ConsumerTests.Handler_runs_in_the_trace...` |

Test suite at the end of phase 3: **181 tests, 0 failures** (106 unit, 18 messaging, 13 inventory,
15 payment provider, 13 evals and assistant, 16 end-to-end). Counted by the test runner's summary;
one full run took 2 min 38 s on the development laptop, containers included.

There are no latency or throughput numbers yet. They belong to the load-test phase and will be
added here only when measured.

## Architecture

```mermaid
flowchart LR
    client([Client])
    provider[(Payment provider<br/>simulated, own ledger)]

    subgraph services [Services, one PostgreSQL database each]
        orders[Orders<br/>checkout saga]
        catalog[Catalog]
        inventory[Inventory]
        payments[Payments]
        notifications[Notifications]
        assistant[Assistant<br/>tools, guardrails, cost limit]
        mcp[MCP server<br/>read-only]
    end

    subgraph kafka [Kafka, Redpanda locally]
        t_orders[[plaza.orders.v1]]
        t_inventory[[plaza.inventory.v1]]
        t_payments[[plaza.payments.v1]]
    end

    client -- "REST: POST /orders" --> orders
    orders -- "REST: prices" --> catalog
    payments -- "REST + Idempotency-Key" --> provider
    client -- "REST: chat" --> assistant
    mcpclient([MCP client]) -- "MCP over HTTP" --> mcp
    assistant -- "REST: search, prices" --> catalog
    assistant -- "REST: stock" --> inventory
    mcp -- REST --> catalog
    mcp -- REST --> inventory
    assistant -. IChatClient .-> model[(Language model<br/>scripted, Claude, OpenAI or Ollama)]

    orders -- outbox --> t_orders
    t_orders --> inventory
    t_orders --> payments
    t_orders --> notifications

    inventory -- outbox --> t_inventory --> orders
    payments -- outbox --> t_payments --> orders
```

### Checkout flow

```mermaid
sequenceDiagram
    participant C as Client
    participant O as Orders
    participant I as Inventory
    participant P as Payments
    participant X as Provider
    participant N as Notifications

    C->>O: POST /orders
    O-->>C: 202 Accepted (Pending)
    O-)I: OrderPlaced
    I-)O: StockReserved / StockRejected
    O-)P: PaymentRequested
    P->>X: POST /charges (Idempotency-Key)
    X-->>P: Succeeded / Declined
    P-)O: PaymentSucceeded / PaymentFailed

    alt paid
        O-)I: OrderConfirmed (commit reservation)
        O-)N: OrderConfirmed (notify customer)
    else declined, or the deadline passed
        O-)I: OrderCancelled (release reservation)
        O-)P: OrderCancelled
        P->>X: PUT /charges/by-key/{key}/void (refund or block)
    end
```

```
Pending --StockReserved--> AwaitingPayment --PaymentSucceeded--> Confirmed
   |                              |
   +--StockRejected--> Rejected   +--PaymentFailed--> PaymentFailed --> OrderCancelled
   |                              |
   +------- deadline passed ------+--> Expired --> OrderCancelled
```

### How the guarantees are built

- **Outbox.** A service never writes to Kafka directly. It inserts the message into its own
  `outbox_messages` table in the same transaction as the business change; a background
  publisher sends it and marks it published. Delivery is at-least-once.
- **Inbox.** Each consumer records the message id in `inbox_messages` in the same transaction
  as the handler's changes. A redelivered message finds its id already there and is skipped.
- **Retries and dead-letter topic.** A failing handler is retried in place with exponential
  backoff and jitter, so order per key is kept. After the last attempt the message goes to
  `<topic>.dlt` with the failure reason in headers.
- **Idempotency key.** The inbox cannot protect a call to somebody else's system. Payments
  charges with the key `plaza-order-<order id>`, and the provider stores one outcome per key:
  a charge repeated after a timeout or a crash returns the first outcome and moves no money.
- **Compensation.** When an order cannot complete, Orders publishes `OrderCancelled`.
  Inventory releases the reservation; Payments voids the charge key, which refunds a charge
  that happened and blocks one that has not happened yet.
- **One deadline.** Every order has `expiresAt` (15 minutes by default,
  `Checkout:ReservationTtl`). Orders is the only service with a timer: it expires overdue
  orders and cancels them, so a reservation never outlives its order.
- **Tracing.** The W3C trace context is stored with the outbox row and sent as a Kafka header;
  the consumer continues the trace.

### The assistant

- **Model behind one interface.** The service only knows `IChatClient` (Microsoft.Extensions.AI).
  `Assistant:Provider` selects a scripted model (default, no network), Claude, OpenAI or Ollama.
- **Tools.** `search_products` (hybrid search), `check_stock` and `build_cart`. They only read
  and compute; there is no tool that orders, pays or changes stock.
- **Hybrid search.** One SQL query in Catalog: weighted full-text search and pgvector
  similarity, merged with reciprocal rank fusion. The embedding sits behind
  `IEmbeddingGenerator`; the default is a deterministic hashing embedder.
- **Guardrails.** The cart comes from the tool, never from the model's text. Customer messages
  and catalogue text that read like instructions are withheld from the model. Answers that
  repeat the system prompt are replaced.
- **Cost limit.** Token usage is priced per turn and each conversation has a spending limit, a
  turn limit and a request rate limit.
- **MCP.** `Plaza.Mcp` offers catalogue search and stock to any MCP client at `/mcp`, through
  the same code and the same filters.

The library behind the first three, [`src/Plaza.Messaging`](src/Plaza.Messaging), is a few
hundred lines and is meant to be read.

## Run it

Requirements: .NET 10 SDK and Docker.

```bash
dotnet run --project src/Plaza.AppHost
```

This starts PostgreSQL, Redpanda, the five services, the simulated payment provider, the
assistant, the MCP server and the Aspire dashboard (the URL with its login token is printed on start-up). Then place an order:

```bash
curl -i -X POST http://localhost:5103/orders -H "Content-Type: application/json" -d '{"customerId":"3f2504e0-4f89-41d3-9a0c-0305e82c3301","paymentToken":"tok_test_visa","lines":[{"productId":"a0000000-0000-4000-8000-000000000001","quantity":1}]}'
```

Follow the `Location` header to see it move from `Pending` to `Confirmed`. Use the token
`tok_test_declined` to see a payment failure and the stock coming back. Product ids are in
[`seed/products.json`](seed/products.json).

Talk to the assistant (any new GUID starts a conversation):

```bash
curl -s -X POST http://localhost:5107/conversations/7b0e6f0a-3c1d-4c58-9a57-1f0a2f6f9c11/messages -H "Content-Type: application/json" -d '{"text":"I need a standing desk and an ergonomic chair under $500"}'
```

It answers with the scripted model. To use a real one, set `Assistant__Provider` to `anthropic`
(with `ANTHROPIC_API_KEY`), `openai` (with `OPENAI_API_KEY` and `Assistant__Model`) or `ollama`
(with `Assistant__Model`), and set the token prices to match. Real providers bill per token.

| Service | Local URL | Endpoints |
|---|---|---|
| Catalog | http://localhost:5101 | `GET /products`, `GET /products/{id}`, `GET /products/batch?ids=`, `GET /products/search?q=` |
| Inventory | http://localhost:5102 | `GET /stock/{productId}`, `PUT /stock/{productId}` |
| Orders | http://localhost:5103 | `POST /orders`, `GET /orders/{id}` |
| Payments | http://localhost:5104 | `GET /payments/by-order/{orderId}` |
| Notifications | http://localhost:5105 | `GET /notifications/by-order/{orderId}` |
| Payment provider (simulated) | http://localhost:5106 | `POST /charges`, `PUT /charges/by-key/{key}/void`, `GET /charges/by-key/{key}`, `GET /charges?reference=` |
| Assistant | http://localhost:5107 | `POST /conversations/{id}/messages`, `GET /conversations/{id}` |
| MCP server | http://localhost:5108 | `/mcp` (tools `search_products`, `check_stock`) |

Every service also exposes `/health/live` and `/health/ready`.

## Test it

```bash
dotnet test --solution Plaza.slnx
```

| Project | What it covers | Needs Docker |
|---|---|---|
| `Plaza.UnitTests` | Order state machine, request validation, charge simulator, message contracts, assistant guardrails, embedder | No |
| `Plaza.Messaging.Tests` | Outbox, inbox, retries, dead-letter topic, trace propagation | Yes |
| `Plaza.Inventory.Tests` | Stock reservation, commit and release under concurrency | Yes |
| `Plaza.PaymentProvider.Tests` | Idempotency keys and voids of the simulated provider | Yes |
| `Plaza.Evals` | Search and assistant quality over 133 labelled cases, cost and rate limits, the MCP server through an MCP client | Yes |
| `Plaza.EndToEnd.Tests` | The whole checkout across all services: duplicated delivery, provider faults, deadline, broker restart | Yes |

## Repository layout

```
src/
  Plaza.AppHost          Aspire host for the local environment
  Plaza.ServiceDefaults  OpenTelemetry, health checks, service discovery
  Plaza.Messaging        Outbox, inbox, consumer with retries and dead-letter topic
  Plaza.Contracts        Message types and topic names
  Plaza.Catalog          Products (REST)
  Plaza.Inventory        Stock and reservations
  Plaza.Orders           Order API, checkout saga and deadline
  Plaza.Payments         Charges and voids through the payment provider
  Plaza.PaymentProvider  Simulated external provider with its own ledger
  Plaza.Notifications    Customer notifications (simulated delivery)
  Plaza.Shopping         Read-only search, stock and cart operations shared by the two below
  Plaza.Assistant        AI shopping assistant: tools, guardrails, cost limit
  Plaza.Mcp              MCP server for catalogue search and stock
tests/                   See "Test it"
docs/adr/                Architecture decision records
seed/                    Invented demo catalogue
evals/                   Labelled cases and the quality baseline
```

## Decisions

Each significant decision has a record with the alternatives that were rejected:

| ADR | Decision |
|---|---|
| [0001](docs/adr/0001-monorepo-with-one-project-per-service.md) | Monorepo, five deployable services, one project per service |
| [0002](docs/adr/0002-aspire-for-local-orchestration.md) | .NET Aspire for the local environment only |
| [0003](docs/adr/0003-confluent-kafka-with-own-messaging-library.md) | Confluent.Kafka with a small in-house messaging library |
| [0004](docs/adr/0004-transactional-outbox-with-polling.md) | Transactional outbox published by polling |
| [0005](docs/adr/0005-idempotent-consumers-with-inbox-table.md) | Idempotent consumers with an inbox table |
| [0006](docs/adr/0006-blocking-retries-and-dead-letter-topic.md) | Blocking retries with backoff, then a dead-letter topic |
| [0007](docs/adr/0007-orders-orchestrates-the-checkout.md) | Orders orchestrates the checkout with a persisted state machine |
| [0008](docs/adr/0008-json-messages-with-metadata-in-headers.md) | JSON messages with metadata in Kafka headers |
| [0009](docs/adr/0009-checkout-saga-with-compensations-and-one-deadline.md) | Compensations driven by one cancellation message and one deadline |
| [0010](docs/adr/0010-simulated-payment-provider-with-idempotency-keys.md) | The payment provider is a separate service, called with idempotency keys |
| [0011](docs/adr/0011-chaos-tests-with-injected-faults-and-a-real-broker-restart.md) | Chaos tests with injected provider faults and a real broker restart |
| [0012](docs/adr/0012-assistant-on-microsoft-extensions-ai-with-a-scripted-default-model.md) | The assistant uses Microsoft.Extensions.AI; the default model is scripted |
| [0013](docs/adr/0013-hybrid-search-with-full-text-pgvector-and-rank-fusion.md) | Hybrid search: full-text and pgvector merged by reciprocal rank fusion |
| [0014](docs/adr/0014-assistant-guardrails-capability-limits-first.md) | Guardrails: limit what the model can do, then filter what it reads and says |
| [0015](docs/adr/0015-evals-as-a-quality-gate-in-ci.md) | Evals with a baseline that fails the build |
| [0016](docs/adr/0016-mcp-server-for-catalogue-and-stock.md) | A read-only MCP server for catalogue and stock |

## Security

- **No card data.** The order API accepts only token-shaped values (`tok_...`); a card number is
  rejected by validation. The token is forgotten by Orders once the charge is requested,
  Payments stores only the provider's reference, and the provider stores a hash of the request.
- **One charge per order.** Enforced where the money moves: a unique index on the idempotency
  key in the provider, and a unique index on the order in Payments.
- **Prices are server-side.** Totals are computed from the catalogue, never from the request.
- **Input limits.** Lines per order, quantity per product, page and batch sizes, idempotency
  key and reference lengths are bounded.
- **Database as last line of defence.** Check constraints forbid negative stock and non-positive
  payment amounts.
- **The model is not trusted (OWASP LLM Top 10).** It has only read-only tools (LLM06), the cart
  is computed by the store and never parsed from its text (LLM05), customer and catalogue text
  that reads like instructions is withheld from it (LLM01), an answer repeating the system prompt
  is replaced (LLM07), and each conversation has a cost, turn and rate limit (LLM10). Provider
  keys come from the environment only.
- **Supply chain.** GitHub Actions are pinned by commit SHA and container images by digest;
  NuGet restores are locked and fail on known-vulnerable packages.

### Known limits of this phase

- **No authentication.** The APIs are open, so anyone who knows an order id can read that order,
  and the stock adjustment endpoint and the simulated provider (charges, voids) are
  unprotected. Authentication is added with the public API; until then this must not be
  exposed outside a development machine.
- **The assistant's filters are pattern matching.** They stop common injection attempts in English
  and Spanish and will miss inventive ones; what bounds the damage is that the model cannot order
  or pay. A model can still say something false, and one eval case shows it.
- **The assistant's cost limit is per conversation, not per customer.** Without authentication
  anyone can start another conversation; the per-address rate limit is the only global bound. A
  conversation id is a bearer secret, and the MCP server has no rate limit.
- **Search has no semantics yet.** The default embedder matches words and parts of words, so a
  third of need-style queries miss. A learned embedding model fits behind the same interface.
- **A customer can be charged and then refunded.** If the provider answers after the order's
  deadline, the charge is taken and returned. Between the two there is a short window in which
  money is held for a dead order.
- **A compensation that keeps failing needs a manual replay.** A void that cannot reach the
  provider within the retry budget goes to the dead-letter topic. There is no replay tooling
  and no reconciliation against the provider's ledger yet.
- **One instance of Orders.** Cancelling relies on Orders publishing an order's messages in the
  order it wrote them, which several outbox publishers do not guarantee
  ([ADR 0009](docs/adr/0009-checkout-saga-with-compensations-and-one-deadline.md)).
- **The provider is called inside the consumer's database transaction.** Correct thanks to the
  idempotency key, but a slow provider holds a connection and a partition for the length of
  the HTTP timeout.
- **Published outbox rows and inbox rows are not purged yet**, so those tables grow. Until they
  are, the payment token of an order also remains in the Orders outbox row and on the orders
  topic (it is an opaque provider token, not card data).

## Roadmap

1. **Order flow over Kafka with outbox, idempotent consumers, retries and dead-letter topic** (done)
2. **Checkout saga with compensations, order deadline, payment idempotency keys, chaos tests** (done)
3. **AI shopping assistant (Microsoft.Extensions.AI), hybrid search with pgvector, evals in CI, MCP server** (done)
4. GraphQL API for clients (Hot Chocolate)
5. Load test with k6, bottlenecks found and fixed
6. Signed images, Kustomize, deployment to kind in CI

## License

[MIT](LICENSE)

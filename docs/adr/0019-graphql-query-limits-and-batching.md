# 0019 - GraphQL queries are batched with DataLoaders and bounded in depth and cost

**Status:** Accepted
**Date:** 2026-11-16

## Context

In GraphQL the client decides the shape of the query. Two problems follow.

The first is the N+1 problem. A page of 20 products with their stock is one call to Catalog
and then, resolved naively, 20 calls to Inventory, one per product.

The second is abuse. `Product.similar` returns products, which have `similar`, and so on. A
query a few lines long can ask for a tree with thousands of searches. Because it is a single
well-formed HTTP request, a rate limit on requests does not see it.

## Decision

**Batching with DataLoaders.** Fields that need another service do not call it; they ask a
DataLoader for a key. The loader collects every key requested while a level of the query is
resolved and makes one batched call (`GET /stock/batch`, `GET /products/batch`).

- `Product.availableStock` uses `StockByProductIdDataLoader`.
- `OrderItem.product` and `Query.product` use `ProductByIdDataLoader`.

Loaders are per request, so nothing is cached between callers.

**Limits, checked before any resolver runs.**

- **Depth:** at most 6 levels (`GraphQL:MaxDepth`).
- **Cost:** Hot Chocolate's static cost analysis with a maximum field cost of 1000 and type
  cost of 1000. Weights reflect what a field really costs the backend: 1 for a field served
  by a DataLoader (one call shared by the whole request), 10 for a field that makes its own
  call, 25 for `similar` (one search per parent product), 50 for `askAssistant`. List sizes
  come from the paging argument the client actually sent (`first`, `pageSize`), so asking
  for more costs more.
- **Arguments:** page sizes, result counts, query and message lengths are validated and
  capped (50 products per page, 20 search results, 5 similar products, 50 orders).
- **Time:** an execution timeout of 15 seconds.
- **Rate:** 120 requests per minute per caller (per token subject, or per address when
  anonymous).
- **Transport:** queries are accepted by POST only.

**Production hides what development shows.** Outside the Development environment
introspection, the schema download and the in-browser IDE are disabled, and exception details
are not included in errors.

## Measured

End-to-end tests count the calls the gateway makes to internal services
(`GraphQlTests`, calls recorded by an HTTP handler in front of each internal client):

| Query | Calls made |
|---|---|
| 20 products with `availableStock` | 2: one to Catalog, one to Inventory (naive: 21) |
| 3 orders, 9 lines, each with `product { name availableStock }` | 3: Orders, Catalog, Inventory (naive: 19) |
| `products { items { similar x5 nested } }` | 0: rejected for depth |
| `products(pageSize: 50) { items { similar(first: 5) { similar(first: 5) } } }` | 0: rejected for cost |

## Consequences

- The number of internal calls for a query depends on its shape, not on how many rows it
  returns, except for `similar`, which is honest about being a search per product and is
  priced accordingly.
- The cost weights are judgments, not measurements. A legitimate query can be refused: the
  first set of weights rejected "my orders with their products", and the test for that query
  is what tuned them. The load-test phase is where they meet real numbers.
- Static analysis prices a query by what it could return, not what it does. A customer with
  one order pays the same as one with ten.
- Introspection off is obscurity, not security: the schema is public in this repository. It
  removes a convenience for automated scanners and nothing else.
- There are no persisted (allow-listed) queries, which is the strongest control for a
  first-party client: only queries the client was built with are accepted.

## Alternatives considered

- **Only a depth limit.** Simple, and blind to wide queries: 50 products with 5 similar each
  is shallow and expensive.
- **Only a request rate limit.** Counts requests, not work. One request can be a thousand
  searches.
- **Persisted queries only.** Removes the problem for known clients by removing arbitrary
  queries. The right production setting for a single first-party front end; it also removes
  the ability to explore the API, so it was not made the default here.
- **Joining in the gateway by calling services in a loop with a cache.** Works for one screen
  and has to be rewritten for the next; DataLoaders give the batching to every query shape.
- **Removing `similar`.** The schema would have no recursion and no need for most of this. A
  real shop has recommendations, reviews and categories, all recursive; keeping one such field
  keeps the problem honest.

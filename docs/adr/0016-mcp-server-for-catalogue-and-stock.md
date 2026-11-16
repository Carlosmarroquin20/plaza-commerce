# 0016 - A read-only MCP server exposes the catalogue and stock

**Status:** Accepted
**Date:** 2026-11-15

## Context

Plaza's own assistant is one consumer of "search the catalogue" and "check stock". Other
models are another: a desktop assistant, an agent in an IDE, a partner's chatbot. The Model
Context Protocol is the common way to offer tools to those without writing an integration per
client.

## Decision

`Plaza.Mcp` is a small ASP.NET Core service built on the official C# SDK
(`ModelContextProtocol.AspNetCore`). It serves streamable HTTP at `/mcp` and offers two tools:

- `search_products(query, maxResults, maxPrice)`
- `check_stock(productIds)`

Both are declared read-only and idempotent in their annotations, and they are: the server
holds no state and calls Catalog and Inventory over their internal REST APIs.

**One implementation, two front doors.** The operations live in `Plaza.Shopping`. The assistant
wraps them as model tools; the MCP server wraps the same methods as MCP tools. Limits (result
counts, query length, number of ids) and the filter that withholds catalogue text which reads
like instructions (ADR 0014) are therefore identical on both paths.

**`build_cart` is not exposed.** It is harmless, but it belongs to a conversation with a
customer, which an MCP client does not have here. The brief is catalogue and stock.

Tests connect with the SDK's MCP client over HTTP, list the tools, call them, and check that a
description planted in the catalogue reaches the client as a placeholder.

## Consequences

- Any MCP client can use Plaza's catalogue without knowing its REST API.
- The server is unauthenticated, like the rest of the system until phase 4. What it exposes is
  what a shop window shows, but an open endpoint can still be used to scrape or to load
  Catalog; there is no rate limit on it yet.
- The filter protects the *client's* model from Plaza's catalogue. It does nothing about what
  the client's model does with the results.
- One more service to run. It could have been an endpoint of the assistant; kept separate so
  that offering tools to others does not depend on the assistant, its database or its model.

## Alternatives considered

- **The assistant consumes its tools through this MCP server.** A single path for everything,
  at the price of a network hop and a protocol session inside every turn, and the cart tool
  would have to be exposed to make it work.
- **stdio transport.** The usual choice for a tool that runs on the user's machine. This one
  is a network service next to Catalog and Inventory, so HTTP is the natural transport.
- **Only publish an OpenAPI document and let clients generate tools.** Works for some clients,
  with tool descriptions written for programmers rather than for a model, and none of the
  filtering.
- **Expose resources (products as MCP resources) instead of tools.** Fits browsing a fixed
  set; search is a parameterised operation, which is what tools are for.

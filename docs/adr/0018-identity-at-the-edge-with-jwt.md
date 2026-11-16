# 0018 - Callers are identified at the gateway with JWT; ownership is enforced there

**Status:** Accepted
**Date:** 2026-11-16

## Context

Through phase 3 nothing was authenticated. Anyone who knew an order id could read the order,
`POST /orders` took the customer id from the request body, and a conversation with the
assistant belonged to whoever knew its id. That was acceptable while every API was internal.
With a public API it is broken access control, the first item of the OWASP Top 10.

## Decision

**The gateway validates a JSON Web Token on every request that carries one.** The customer is
the token's `sub` claim, which must be a GUID. Two configurations:

- `Auth:Authority` (and `Auth:Audience`): tokens are issued by an OpenID Connect provider and
  validated against its published keys. This is the production shape.
- `Auth:SigningKey`: tokens are signed with a shared HMAC key of at least 32 bytes, with a
  fixed issuer and audience and the algorithm pinned to HS256. Used by tests and wherever
  there is no identity provider.

If neither is configured the service refuses to start, except in the Development environment,
where it generates a random key in memory and exposes `POST /dev/token`, which returns a
token for a new customer id. That endpoint does not exist in any other environment. There is
no key in the repository and none to configure to try the API locally.

**What needs a token.** The catalogue (`products`, `product`, `searchProducts`) is public.
Orders (`order`, `myOrders`, `placeOrder`) and the assistant (`askAssistant`) require an
authenticated caller.

**Ownership is checked where identity is known.**
- `placeOrder` takes the customer from the token. The schema has no customer id argument, so
  there is nothing to tamper with.
- `order(id)` returns the order only if it belongs to the caller. Somebody else's order is
  `null`, the same answer as an order that does not exist, so ids cannot be probed.
- `myOrders` asks Orders for the caller's orders by the customer id from the token.
- The assistant identifies a conversation by its id alone, so the gateway sends it an id
  derived from the caller *and* the id the caller chose (a hash of both). The same
  conversation id used by two customers leads to two conversations.

**A bad token is an error, not anonymity.** A request with no token is anonymous. A request
whose token is expired, forged or malformed gets `401`, even for a public query, so a client
learns that its token needs refreshing.

## Consequences

- The broken-access-control findings recorded since phase 1 are closed for anything that
  comes through the gateway.
- They remain open *behind* it. Internal REST APIs still trust their caller: whoever can reach
  `orders:5103` directly can read any order and place one for any customer. Until the
  deployment phase restricts the network, the internal services must not be exposed.
- There is no login, registration or user store. Plaza validates tokens; issuing them is an
  identity provider's job. A customer is an opaque id.
- There are no roles. Back-office operations (`PUT /stock`) have no public route at all, which
  is the access control for now.
- With the shared-key configuration, anyone holding the key can mint a token for any
  customer. It is for tests and development; production uses an authority.
- The assistant's cost limit is still per conversation (ADR 0014). A customer can open many;
  the gateway's rate limit per caller is what bounds them.

## Alternatives considered

- **Running an identity provider locally (Keycloak, Duende).** The real thing end to end, and
  a container, a realm import and a login flow to maintain for a project whose subject is not
  identity. The gateway's `Authority` setting is where one plugs in.
- **API keys.** Simple, but a key identifies an application, not a customer, and ownership of
  orders needs a customer.
- **Authenticating in every service.** Defence in depth, and the right end state. It means
  passing the caller's token (or a signed internal one) on every internal call and teaching
  five services to validate it. Left for when the network boundary is real.
- **Session cookies.** Natural for a browser-only client; tokens fit the mobile and
  machine clients a GraphQL API also serves, and need no CSRF protection.
- **Returning "forbidden" for another customer's order.** More informative for an honest
  client and a way for a dishonest one to learn which ids exist.

# 0022 - Internal HTTP calls get timeouts, retries of safe requests and a circuit breaker

**Status:** Accepted
**Date:** 2026-11-16

## Context

Services call each other over HTTP: the gateway calls four services, Orders calls Catalog, the
assistant calls Catalog and Inventory, Payments calls the provider. Each client had one
setting, an overall timeout of 5 or 10 seconds. That leaves three problems.

A call that hangs holds a thread, a connection and often a database transaction for the full
timeout. A failure that would succeed on a second try (a restart, a dropped connection) is
reported to the customer. And when a service is really down, every caller keeps sending it
requests and keeps waiting for each of them, so the outage spreads upstream.

## Decision

Every `HttpClient` gets the same pipeline, configured once in `Plaza.ServiceDefaults` with
`Microsoft.Extensions.Http.Resilience` (Polly). From the outside in:

| Layer | Setting | Why |
|---|---|---|
| Total timeout | 10 s | What the caller waits at most, retries included |
| Retry | 2 more attempts, exponential backoff from 200 ms with jitter | Rides out a blip |
| Circuit breaker | Opens when half of at least 20 calls in 10 s fail; stays open 5 s | Stops calling a service that is down |
| Attempt timeout | 3 s | A hung call is abandoned and can be retried |

Values are under `Resilience` in configuration.

**Only safe requests are retried.** GET, HEAD, PUT and DELETE can be repeated. A POST is never
retried by the pipeline: repeating `POST /orders` places a second order. The one POST that is
safe to repeat, the charge with its idempotency key (ADR 0010), is repeated by the message
consumer, which is the layer that knows it is safe.

**An open circuit fails at once.** While it is open, calls are rejected without being sent,
in microseconds, and the service that is in trouble gets five seconds without traffic from
this caller. Then one call is let through to find out whether it has recovered.

**"Unavailable" is not "internal error".** A rejected call, a timeout or a refused connection
is turned into `503` by the endpoints that depend on another service, where it used to
surface as `500` for anything but a plain connection failure.

Unit tests drive the pipeline against a handler that misbehaves on purpose: a read that fails
twice is answered on the third attempt, a failing write is sent once, a 400 is not retried,
the breaker opens after four failures and the next call is refused without reaching the
server, and an attempt that hangs is abandoned.

## Consequences

- A dependency that is down costs its callers microseconds per call instead of seconds, and
  the callers' threads, connections and transactions are not held.
- The Payments consumer meets an open circuit as an exception, like any failure: the message
  is retried and, after its attempts, dead-lettered (ADR 0006). With the circuit open, each
  attempt fails at once instead of waiting for a timeout, so a provider outage sends payment
  requests to the dead-letter topic sooner and unblocks the partition sooner. The order still
  ends through its deadline (ADR 0009).
- Retries multiply load on a struggling service. Two retries with backoff and a breaker in
  front bound that; a retry storm from many callers is still possible in principle.
- The breaker is per client and per process. Two instances of a caller each learn separately
  that a service is down.
- The same numbers apply to every call. A call that is legitimately slow, such as the
  assistant waiting for a language model through the gateway, is cut at 3 s per attempt; the
  model call itself is not an `HttpClient` of ours and is not affected, but the gateway's call
  to the assistant is. A real model needs that client's timeouts raised.
- The load test does not measure this change on its own: it went in together with the
  catalogue cache, and nothing was failing for it to act on. Its evidence is the unit tests.

## Alternatives considered

- **Only timeouts.** Bounds the wait and nothing else: every caller still queues behind a dead
  service, one timeout at a time.
- **Retrying everything, including POST.** Higher success rate on blips and duplicate orders
  when the first attempt had in fact succeeded and only its answer was lost.
- **A service mesh (retries and breakers in a sidecar).** Keeps this out of the code and
  applies to every language. It needs the cluster that phase 6 brings, and the mesh cannot
  know which POST is idempotent.
- **Waiting, instead of dead-lettering, while a circuit is open.** The consumer could pause a
  partition until the provider is back. Orders that expired meanwhile would then be charged
  late and refunded straight away. Parking the request and letting the deadline cancel the
  order never charges it.
- **Bulkheads (a limit on concurrent calls per dependency).** The connection budget of
  ADR 0020 already plays that role for the database, which is where the load test found the
  problem; nothing measured asks for one on HTTP yet.

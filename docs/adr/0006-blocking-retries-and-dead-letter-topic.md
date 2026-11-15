# 0006 - Blocking retries with backoff, then a dead-letter topic

**Status:** Accepted
**Date:** 2026-11-15

## Context

A handler can fail because of something temporary (database failover, lock timeout) or because
the message can never be processed (malformed payload, a bug). The consumer has to keep trying
the first kind without spinning, and must not let the second kind block a partition forever.

## Decision

- A failed message is retried **in place**: the consumer waits and calls the handler again, up
  to `MaxAttempts` (default 5), with exponential backoff and equal jitter
  (base 200 ms, capped at 5 s; half of each delay is fixed, half random, so a retry is never
  immediate). Each attempt runs in a new transaction.
- While a message is being retried the consumer does not move on, so messages for the same key
  are never processed out of order because of a retry.
- When attempts are exhausted, the original message (key, value, headers) is produced to
  `<topic>.dlt` with extra headers: `dlt-reason`, `dlt-exception`, `dlt-attempts`,
  `dlt-original-topic`, `dlt-original-partition`, `dlt-original-offset`. Only after the broker
  acknowledges it does the consumer store the offset and continue.
- Messages that cannot be deserialised skip the retries and go straight to the dead-letter topic.
- The total retry time is kept well below `max.poll.interval.ms` so that retrying never causes a
  rebalance.

## Consequences

- No message is dropped silently: it is either processed or sitting in a dead-letter topic with
  the reason attached.
- A failing message delays everything behind it on its partition for at most the retry budget
  (about 10 s with the defaults). This is the price of keeping order.
- Dead-lettered messages need an operator: there is no automatic replay yet. Lag on `*.dlt`
  topics must be alerted on.
- If the dead-letter topic itself is unavailable the consumer stops making progress on that
  partition rather than lose the message.

## Alternatives considered

- **Non-blocking retry topics (`topic.retry.1`, `topic.retry.2`, ...):** keeps the main
  partition moving, but a later message for the same order can overtake the one being retried.
  The checkout flow depends on per-order progress, so this trades a short delay for a
  correctness problem.
- **Retry forever:** one poison message stops a partition until someone deploys a fix.
- **Skip and log:** loses the message; logs are not a recovery mechanism.

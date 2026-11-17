# 0020 - Load test: an open-model Black Friday scenario, and what it found

**Status:** Accepted
**Date:** 2026-11-16

## Context

Until now every claim about Plaza was about correctness. Nothing said how it behaves when many
people use it at once, and correctness tests cannot find the problems that only exist under
load: pools that run out, queues that grow, one slow thing in front of everything else.

## Decision

**One scenario, three kinds of traffic at once** (`load/black-friday.js`, k6): anonymous
browsing through the GraphQL gateway, checkouts that are followed until the order is final,
and a flash sale in which 300 customers buy a product with 100 units at the same instant,
placed at the peak of the other two.

**Arrival rates, not virtual users.** Requests arrive at a fixed rate whether or not the system
answers (an open model). With the usual closed model, where each virtual user waits for its
answer, a slow system slows the load down and the test under-reports exactly the queueing it
should expose.

**Thresholds are service levels, and correctness is one of them.** Errors under 1%, browse p95
under 300 ms, ordinary checkouts final within 5 s at p95, no order unfinished after 60 s, and
exactly 100 flash orders confirmed with stock ending at 0 available and 0 reserved, read from
Inventory after the run.

**Measure, change one thing, measure again.** Each fix has its own commit and its own run. The
runs and their raw output are in `load/results/`. Two attempts that did not help are kept on
record instead of being quietly dropped.

**The machine is part of the result.** Everything ran on one development laptop that hosted
the services, PostgreSQL, Redpanda and the load generator, with other applications open
(`load/README.md` has the details). The numbers compare runs with each other. They are not a
capacity figure.

## What it found

Full scale is 120 browse requests/s, 30 orders/s and the flash sale. Before and after, same
script:

| Full scale | Before | After |
|---|---|---|
| GraphQL errors | 26.92% | 0.00% |
| Browse, median / p95 | 7.66 s / 12.98 s | 37.6 ms / 707 ms |
| Orders not final within 60 s | 533 | 0 |
| Orders confirmed during the run | 921 | 2,667 |
| Ordinary checkout, p95 | 65 s | 11.22 s |
| Flash-sale buyer, p95 | 66 s | 16.27 s |
| Flash sale outcome | 62 confirmed, 38 still reserved when the test ended | 100 confirmed, 200 rejected |
| Connections refused by PostgreSQL | 6,916 | 0 |

| Half scale | Before | After |
|---|---|---|
| GraphQL errors | 5.33% | 0.01% |
| Browse, median / p95 | 3.56 s / 5.27 s | 9.9 ms / 336 ms |
| Orders not final within 60 s | 208 | 0 |
| Orders confirmed during the run | 608 | 1,388 |
| Ordinary checkout, p95 | 0.84 s, counting only the orders that finished | 8.18 s, all of them |
| Flash-sale buyer, p95 | 70 s | 12.31 s |

In every run stock was never oversold: Inventory ended at 0 available for the flash product.

**1. Connection exhaustion (fixed).** Seven services shared one PostgreSQL server with 100
connections, each with Npgsql's default pool of 100. Under load the server answered "too many
clients already" thousands of times, and every refusal became a failed request. Each service
now has a pool of 15 (`Database:MaxPoolSize`) and the server allows 150. A service under
pressure waits for one of its own connections instead of taking the last one from the others.
Alone, this took errors from 26.92% to 0.03% and throughput from 163 to 449 requests/s.

**2. One message at a time (fixed, ADR 0021).** Orders handled about 45 messages/s with no
other load, because each consumer processed one message at a time and each waits for a commit.

**3. Telemetry (no effect).** The Aspire orchestrator was using about one core to forward logs
and traces. HTTP client logging was reduced and traces sampled at 5% under load. Browse p95
went from 2.66 s to 3.06 s, which is noise. Kept, because the log lines duplicate the spans
and production needs sampling anyway, but it fixed nothing.

**4. Repeated catalogue reads (fixed).** Catalogue pages and searches are the same for every
visitor. They are cached in Catalog for 30 s (`Catalog:CacheSeconds`) with `HybridCache`,
which also collapses concurrent misses for one key into one query. The batch lookup that
prices an order is not cached. List queries also stopped fetching each product's embedding
and search vector, which were read and thrown away.

**5. What is left: one hot product.** Without the flash sale, the final code passes every
threshold at full scale (browse p95 52 ms, checkout p95 1.15 s, no errors). With it, ordinary
checkouts placed during the burst wait about ten seconds. Three hundred reservations of one
product all update one row, each holds the row until its commit, and other orders on the same
partitions queue behind them.

## Consequences

- The system degrades by getting slower instead of failing: at full scale on this laptop no
  request errors and no order is lost, but two latency thresholds are still missed during the
  flash sale. That is stated, not hidden by lowering the bar.
- Cached catalogue data can be 30 s old. A price shown may differ from the price charged for
  that long after a change; the order is always priced from the uncached lookup. Nothing edits
  products yet, so nothing invalidates the cache.
- The cache is in memory, per Catalog instance. Several instances would each have their own;
  `HybridCache` takes a distributed second level (Redis) when that matters.
- The pool size is a budget that has to be kept by hand: services x pool size must stay below
  the server's limit, and adding an instance of a service spends from it.
- The load test is not part of CI. It needs minutes on a quiet machine and its numbers are
  only comparable on the same hardware.
- Rate limits are lifted for the test (`--Plaza:LoadTest=true`), because every virtual user
  comes from one address. The limiter itself is therefore not exercised.

## Alternatives considered

- **PgBouncer in front of PostgreSQL.** The usual answer to many clients and few connections,
  and necessary with many instances. With seven pools the budget fits without another moving
  part.
- **A PostgreSQL server per service.** Removes the shared limit and matches "a database per
  service" physically. Seven servers on a laptop would have changed what was measured.
- **Caching at the gateway instead of in Catalog.** Saves the hop as well, but the service that
  owns the data is the one that knows when it changes.
- **For the hot product: splitting its stock into several rows, or taking reservations for one
  product through a queue of its own.** Either removes the single row. Both change the
  Inventory model for a case that one product in a thousand hits; left as the next step, with
  the measurement that justifies it.
- **`synchronous_commit = off`.** Makes every commit fast and would shrink most of these
  numbers, by allowing the loss of the last transactions on a crash. Not for orders and stock.
- **A closed-model test with N virtual users.** Simpler to reason about and it would have shown
  a slower but "stable" system, hiding the unbounded queue that the open model exposed.

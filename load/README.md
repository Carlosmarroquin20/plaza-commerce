# Load test

`black-friday.js` is a [k6](https://k6.io) scenario with three kinds of traffic at once:

| Scenario | What it does | Peak at `SCALE=1` |
|---|---|---|
| `browse` | Anonymous searches and catalogue pages with stock, through the GraphQL gateway | 120 requests/s |
| `checkout` | Places an order with a token and polls it every 250 ms until it is final | 30 orders/s |
| `flash_sale` | 300 customers buy the same product at the same instant; 100 units exist | one burst, at the peak |

Rates are arrival rates (an open model): requests keep coming whether or not the system keeps
up. The run starts with a warm-up that is not measured.

The thresholds in the script are the service levels the system is held to: under 1% errors,
browse p95 under 300 ms, ordinary checkouts final within 5 s at p95, flash-sale buyers within
30 s, no order left unfinished after 60 s, and exactly 100 flash orders confirmed.

## Run it

Needs Docker and the .NET 10 SDK. Start the system in Release with rate limits lifted (the
load generator is a single address):

```bash
dotnet run -c Release --project src/Plaza.AppHost -- --Plaza:LoadTest=true
```

Then, from the repository root:

```bash
docker run --rm -v "$PWD/load:/load" -e SCALE=1 grafana/k6@sha256:d41bb79cd8de5a7ad619b53570e3712633e8bff0937010a5c6c57a4747e7a276 run --summary-export /load/results/run.json /load/black-friday.js
```

`SCALE` multiplies the browse and checkout rates. `FLASH_BUYERS` and `FLASH_STOCK` change the
burst (`FLASH_BUYERS=1` removes it). `probe.js` sends a single request shape at a fixed rate to
measure one hop in isolation: `-e TARGET=gateway-search -e RATE=120`.

## Results

In `results/`, as printed by k6 (`.txt`) and as exported (`.json`). All were measured on one
development laptop (AMD Ryzen 5 4600H, 6 cores / 12 threads, 11.4 GB RAM, Windows 11, Docker
Desktop with 5.5 GB) that ran the system, PostgreSQL, Redpanda and the load generator
together, with other desktop applications open. They compare runs with each other; they are
not the capacity of the system on real hardware.

| File | Code | Scale |
|---|---|---|
| `before-full`, `before-half` | Before any fix (commit `aad6484`) | 1 and 0.5 |
| `step1-connection-budget` | Connection pools capped | 1 |
| `step2-parallel-consumers` | Plus one worker per partition | 1 |
| `step3-less-telemetry` | Plus quieter logging and trace sampling | 1 |
| `after-full`, `after-half` | Final: plus catalogue cache and HTTP resilience | 1 and 0.5 |
| `after-full-no-flash` | Final, without the flash-sale burst | 1 |
| `rejected-batched-handling-full` | An attempted fix that made things worse and was reverted | 1 |

The step files were produced with an earlier version of the script that reported checkout time
for all orders together; the `before` and `after` files use the script as it is now.

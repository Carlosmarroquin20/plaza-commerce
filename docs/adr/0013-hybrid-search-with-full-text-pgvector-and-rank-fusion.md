# 0013 - Hybrid search: PostgreSQL full-text and pgvector, merged by reciprocal rank fusion

**Status:** Accepted
**Date:** 2026-11-15

## Context

The assistant, and any client, needs to find products from what a customer types: exact names
("27 inch QHD monitor"), words from a description ("privacy shutter"), misspellings
("keybord"), and needs that share no word with the catalogue ("something to sit on"). Keyword
search handles the first two and fails on the rest. Vector search handles similarity but is
weaker on exact terms.

## Decision

Search lives in the Catalog service (`GET /products/search`) and runs entirely in PostgreSQL,
in one query:

- **Keywords:** a stored `tsvector` column, weighted (name A, category B, description C), with a
  GIN index. The query's words are OR-ed, so a product matching some of them is still a
  candidate, and `ts_rank_cd` ranks products that match more, and in more important fields,
  higher.
- **Vectors:** a `vector(256)` column (pgvector) with an HNSW index and cosine distance.
  Candidates further than 0.95 are discarded.
- **Fusion:** each ranking contributes its top 20; the final score is
  `1 / (60 + rank_keyword) + 1 / (60 + rank_vector)` (reciprocal rank fusion). Ranks are
  merged, not scores, so the two scales never have to be made comparable.

**The embedding is behind `IEmbeddingGenerator`, and the default needs no model.**
`HashingEmbeddingGenerator` hashes each word and its character trigrams into 256 dimensions.
It is deterministic and instant, and it makes "keybord" land next to "keyboard". It has no
idea that a screen is a monitor. A learned embedding model replaces it by registering another
`IEmbeddingGenerator` and migrating the column to that model's dimension.

## Measured

70 labelled queries (`evals/retrieval.jsonl`), recall@3, against the seed catalogue of 20
products:

| Queries | Hybrid | Keywords only | Vectors only |
|---|---|---|---|
| All 70 | 0.929 | 0.829 | 0.914 |
| Exact names (20) | 1.000 | | |
| Description keywords (20) | 1.000 | | |
| Misspellings (15) | 1.000 | | |
| Needs in the customer's own words (15) | 0.667 | | |

The hybrid beats either half alone. The last row is the honest limit of an embedding without
semantics: a third of the need-style queries miss.

## Consequences

- No search engine to deploy; search is consistent with the catalogue because it is the
  catalogue's own database. The PostgreSQL image is now `pgvector/pgvector`.
- The eval set was also used to choose the distance cutoff and the field weights; there is no
  held-out set, so these numbers are optimistic for unseen queries.
- Weighting the name helped the assistant pick the right product ("desk" finds desks before a
  lamp that mentions one) and cost some rank quality on need-style queries (MRR 0.551 to
  0.518). The eval gate reported that drop; it was accepted and the baseline updated.
- Twenty products say nothing about performance. The indexes are the right ones for a large
  table, and that is all that can be claimed before the load-test phase.
- The embedding of a product is computed when it is seeded or first indexed. There is no
  re-indexing when a product's text changes, because nothing edits products yet.

## Alternatives considered

- **Elasticsearch or OpenSearch.** The standard answer for hybrid search at scale, and one more
  stateful system to run, secure and keep in sync with the catalogue, for 20 products.
- **Vector search only, with a real embedding model.** Good on needs, but exact identifiers and
  rare terms are where embeddings are weakest, and it makes CI depend on a model.
- **A weighted sum of scores instead of rank fusion.** `ts_rank` and cosine distance have
  different, query-dependent scales; the weight would need tuning per dataset. Rank fusion has
  one constant with a well-known default.
- **`pg_trgm` for misspellings.** It would cover typos without vectors, and then needs and
  typos would be two mechanisms. The vector column covers typos today and semantics once a
  real model is plugged in.
- **A small ONNX embedding model in-process.** Real semantics with no network at run time, at
  the cost of a model file of tens of megabytes in the repository or a download in CI. The
  natural next step, and it fits behind the same interface.

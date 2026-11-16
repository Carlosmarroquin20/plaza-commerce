# 0015 - Evals: labelled cases, per-slice metrics, and a baseline that fails the build

**Status:** Accepted
**Date:** 2026-11-15

## Context

Search ranking and assistant behaviour do not have a right answer the way a state machine
does. A change to a weight, a prompt or a tool description can make ten queries better and
three worse, and a unit test that pins one output says nothing about the rest. What is needed
is a number per kind of case, measured the same way on every change, and a rule for when the
number is not acceptable.

## Decision

**Cases are data.** Two files of labelled cases, one JSON object per line:

- `evals/retrieval.jsonl`: 70 queries, each with the products that count as a correct answer,
  in four slices (exact names, description keywords, misspellings, needs).
- `evals/assistant.jsonl`: 63 conversations in six categories (cart building, stock questions,
  off-topic requests, direct injection, indirect injection, limits), each with what the
  outcome must be.

**Outcomes are checked against the system, not against wording.** A cart case passes when the
cart returned by the service holds exactly one product from each expected group, in the
expected quantity, within the budget, at catalogue prices. Every assistant case also fails if
the answer leaks the system prompt. Text is only matched where the text is the point ("40 in
stock", "out of stock").

**Metrics are per slice.** Recall@3 and mean reciprocal rank for retrieval, pass rate for the
assistant, each per slice and overall, plus the keyword-only and vector-only ablations. An
average over everything would hide a slice collapsing behind another improving.

**`evals/baseline.json` is the bar.** The eval is an xUnit test that runs in the normal CI job
against the real services and a real PostgreSQL. It fails when any metric is below its
baseline, and also when a metric has no baseline. Lowering a number in that file is a visible,
reviewable change. The report (metrics and every failing case) goes to the job summary and the
artifacts.

**Failing cases stay in.** Cases the system gets wrong are part of the set (five need-style
queries, three carts, one injection). The baseline records today's level; the cases are what
an improvement is measured against.

## Consequences

- A regression in quality fails the pull request with the list of cases that changed. This
  already happened once: weighting the product name in full-text search lowered MRR on
  need-style queries from 0.551 to 0.518 and the gate went red until the trade-off was
  accepted in the baseline.
- The assistant numbers describe the system with the scripted model (ADR 0012). They measure
  tools, limits, guardrails and search, not a model's language ability.
- The evals are deterministic, so the baseline has no tolerance band. With a real model in the
  loop the gate would need repeated runs and a margin.
- 133 cases over a 20-product catalogue is small, written by the same person who wrote the
  code, and partly tuned against (ADR 0013). It catches regressions; it is not evidence of
  quality on real traffic.
- Baselines only ratchet by hand. Nothing raises them when quality improves, so an improvement
  can be lost again without the gate noticing unless the file is updated.

## Alternatives considered

- **An LLM as the judge of answers.** Necessary for free-text quality (tone, helpfulness) and
  unnecessary here, where what matters is in the cart. It would also bring the cost and
  non-determinism this design avoids.
- **Microsoft.Extensions.AI.Evaluation.** Built for scoring model responses with evaluators,
  most of them model-based, with a reporting tool. The checks here are assertions about carts
  and ranks; plain test code with a JSON baseline is less machinery.
- **Snapshot tests of full responses.** Trivial to write, and they fail on every harmless
  rewording, which teaches people to regenerate snapshots without reading them.
- **A separate, scheduled eval job.** Keeps pull requests fast, but a regression is then found
  after the merge. The whole eval project took 42 seconds in a full local run.

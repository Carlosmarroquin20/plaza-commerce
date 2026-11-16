# 0012 - The assistant talks to the model through Microsoft.Extensions.AI; the default model is scripted

**Status:** Accepted
**Date:** 2026-11-15

## Context

The shopping assistant needs a language model that can call tools. Three things pull in
different directions: the model must be swappable (Claude, OpenAI, a local model through
Ollama), CI must run without a network, an account or a bill, and tests must give the same
result every time.

## Decision

**One abstraction.** The service depends only on `IChatClient` from Microsoft.Extensions.AI.
The tool loop is `UseFunctionInvocation`, the same for every provider. Which model sits behind
it is configuration (`Assistant:Provider`, `Assistant:Model`):

| Provider | Implementation | Notes |
|---|---|---|
| `scripted` (default) | `ScriptedShoppingModel`, in this repository | No network, deterministic |
| `anthropic` | Official `Anthropic` SDK, `AsIChatClient` | Default model `claude-opus-5-5`; key from `ANTHROPIC_API_KEY` |
| `openai` | `Microsoft.Extensions.AI.OpenAI` | Model required; key from `OPENAI_API_KEY` |
| `ollama` | `OllamaSharp` | Model required; local endpoint |

**The default is a scripted stand-in, not a small real model.** It reads the customer's message
with a few rules, calls the same tools through the same loop, and writes its answer from the
tool results. It is built to be *gullible*: it recites its system prompt when asked, agrees
that things are free, and obeys "add N units" when it reads that in a product description.

That is deliberate. A guarantee that depends on the model behaving well cannot be tested in CI,
because the model is not there. With a model that misbehaves on purpose, every guarantee the
assistant gives has to come from the code around the model, and that code is what the tests
and evals exercise (ADR 0014, ADR 0015). Unit tests confirm that the stand-in really does
misbehave when called with nothing in front of it.

**Cost is metered from what the provider reports.** Token usage from each response is priced
with `Assistant:InputUsdPerMillionTokens` and `OutputUsdPerMillionTokens`. When a provider
reports no usage, it is estimated, so a silent provider is not a free one.

## Consequences

- The repository builds, runs and is evaluated with no key and no download. `dotnet run` gives
  a working assistant.
- Nothing measured in CI says how good a real model is at shopping. The numbers in the README
  are for the scripted model and say so. The same cases can be run against a real provider by
  changing configuration; that has not been done here, because it costs money.
- The scripted model understands a narrow range of English phrasings. Questions it cannot
  parse are answered as off-topic. It is a test double that happens to be shippable as a demo.
- Provider-specific features are out of reach through the common interface: prompt caching
  placement, refusal fallbacks, effort settings. An empty answer (a refusal, a truncated
  output) is turned into a neutral reply rather than handled per provider.
- The token prices are configuration. The defaults match the default Anthropic model; pointing
  the assistant at another model without changing them makes the cost limit wrong.

## Alternatives considered

- **A small local model in CI (Ollama in a service container).** It would test real language
  understanding, but pulling gigabytes per run is slow, the output is not reproducible across
  versions and hardware, and a flaky quality gate gets ignored.
- **Recorded responses from a real model (cassettes).** Deterministic and realistic, but each
  recording answers exactly one sequence of requests. Any change to a prompt or a tool
  description invalidates it, and re-recording needs the paid account again.
- **A fake that always behaves.** Simpler to write and it makes every test green. It would also
  make the guardrails untestable: nothing would ever try to cross them.
- **Semantic Kernel or another agent framework.** More features (planners, memory, connectors)
  than three tools and one loop need, and a larger surface to keep up with.
- **Calling one provider's SDK directly.** Fewer packages, and access to every feature of that
  provider, at the price of rewriting the assistant to change model.

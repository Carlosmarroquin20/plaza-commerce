using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Plaza.Catalog.Data;
using Plaza.Catalog.Search;
using Plaza.Testing;
using Xunit;

namespace Plaza.Evals;

public sealed record RetrievalCase(string Id, string Slice, string Query, int[] Expected);

/// <summary>The Catalog service against a real PostgreSQL with pgvector and the seed catalogue.</summary>
public sealed class CatalogUnderEval : IAsyncLifetime
{
    private readonly InfrastructureFixture _containers = new(withKafka: false);
    private WebApplicationFactory<CatalogDbContext>? _factory;

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _containers.StartAsync();
        _factory = new WebApplicationFactory<CatalogDbContext>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:catalog", _containers.ConnectionString("catalog"));
            builder.UseSetting("Seed:Enabled", "true");
        });
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _containers.DisposeAsync();
    }
}

/// <summary>
/// Search quality over labelled queries. A query is a hit when one of its expected products is
/// in the first three results; the reciprocal rank rewards putting it first. The run fails
/// when a metric falls below <c>evals/baseline.json</c>.
/// </summary>
public sealed class RetrievalEval(CatalogUnderEval catalog) : IClassFixture<CatalogUnderEval>
{
    private const int TopK = 3;

    [Fact]
    public async Task Search_quality_does_not_drop_below_the_baseline()
    {
        var cases = EvalFiles.ReadCases<RetrievalCase>("retrieval.jsonl");
        Assert.True(cases.Count >= 60, "the retrieval eval needs a meaningful number of cases");
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());

        var results = await RunAsync(cases, "hybrid");

        var metrics = new List<Metric>();
        foreach (var group in results.GroupBy(r => r.Case.Slice).Append(results.GroupBy(_ => "all").Single()))
        {
            var list = group.ToList();
            metrics.Add(new Metric($"recall@{TopK}.{group.Key}", Recall(list), list.Count));
            metrics.Add(new Metric($"mrr.{group.Key}", list.Average(r => r.Rank is { } rank ? 1.0 / rank : 0.0), list.Count));
        }

        // Ablation: what each half of the hybrid achieves alone on the same queries.
        foreach (var mode in new[] { "keyword", "vector" })
        {
            metrics.Add(new Metric($"recall@{TopK}.all.{mode}-only", Recall(await RunAsync(cases, mode)), cases.Count));
        }
        var (passed, report) = EvalGate.Check(
            $"Retrieval eval ({cases.Count} labelled queries)",
            metrics,
            EvalFiles.ReadBaseline("retrieval"),
            results.Where(r => r.Rank is null or > TopK).Select(r =>
                $"{r.Case.Id} [{r.Case.Slice}] \"{r.Case.Query}\": expected {string.Join("/", r.Case.Expected)}, " +
                $"found at rank {r.Rank?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}; top {TopK}: {r.Top}"));

        TestContext.Current.TestOutputHelper?.WriteLine(report);
        TestContext.Current.TestOutputHelper?.WriteLine($"Report: {EvalFiles.WriteReport("eval-retrieval.md", report)}");
        Assert.True(passed, report);
    }

    private static double Recall(IReadOnlyCollection<(RetrievalCase Case, int? Rank, string Top)> results) =>
        results.Average(r => r.Rank <= TopK ? 1.0 : 0.0);

    private async Task<List<(RetrievalCase Case, int? Rank, string Top)>> RunAsync(List<RetrievalCase> cases, string mode)
    {
        var results = new List<(RetrievalCase Case, int? Rank, string Top)>();
        foreach (var c in cases)
        {
            var hits = await catalog.Client.GetFromJsonAsync<List<ProductSearchHit>>(
                $"/products/search?q={Uri.EscapeDataString(c.Query)}&limit=10&mode={mode}", TestContext.Current.CancellationToken) ?? [];
            var expected = c.Expected.Select(EvalFiles.ProductId).ToHashSet();
            var index = hits.FindIndex(h => expected.Contains(h.Id));
            results.Add((c, index < 0 ? null : index + 1, string.Join(", ", hits.Take(TopK).Select(h => h.Name))));
        }

        return results;
    }
}

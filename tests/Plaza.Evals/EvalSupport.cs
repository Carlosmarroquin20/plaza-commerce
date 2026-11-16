using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Plaza.Evals;

/// <summary>Reads labelled cases and the quality bar, and writes the report of a run.</summary>
public static class EvalFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Root => Path.Combine(AppContext.BaseDirectory, "evals");

    /// <summary>One JSON object per line; blank lines are ignored.</summary>
    public static List<T> ReadCases<T>(string file) =>
        [.. File.ReadLines(Path.Combine(Root, file))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<T>(line, Json)
                ?? throw new InvalidDataException($"Empty case in {file}."))];

    /// <summary>The minimum each metric must reach, from <c>evals/baseline.json</c>.</summary>
    public static IReadOnlyDictionary<string, double> ReadBaseline(string section)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "baseline.json")));
        return document.RootElement.GetProperty(section).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetDouble());
    }

    /// <summary>Product ids of the seed catalogue are <c>a0000000-0000-4000-8000-0000000000NN</c>.</summary>
    public static Guid ProductId(int number) => Guid.Parse($"a0000000-0000-4000-8000-{number:D12}");

    public static int ProductNumber(Guid id) => int.Parse(id.ToString()[^12..], CultureInfo.InvariantCulture);

    /// <summary>Writes the report next to the test binaries; CI uploads it as an artifact.</summary>
    public static string WriteReport(string name, string markdown)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(path, markdown);
        return path;
    }
}

/// <summary>A metric measured in a run, compared with the minimum allowed for it.</summary>
public sealed record Metric(string Name, double Value, int Cases)
{
    public string Format() => Value.ToString("0.000", CultureInfo.InvariantCulture);
}

public static class EvalGate
{
    /// <summary>
    /// Fails when any metric is below its baseline. A metric without a baseline also fails:
    /// adding cases must come with a decision about the bar.
    /// </summary>
    public static (bool Passed, string Report) Check(
        string title, IReadOnlyList<Metric> metrics, IReadOnlyDictionary<string, double> baseline, IEnumerable<string> failures)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"# {title}").AppendLine();
        report.AppendLine("| Metric | Cases | Measured | Minimum | Result |").AppendLine("|---|---|---|---|---|");

        var passed = true;
        foreach (var metric in metrics)
        {
            var hasMinimum = baseline.TryGetValue(metric.Name, out var minimum);
            // The tolerance only absorbs floating-point noise; the bar itself is the baseline.
            var ok = hasMinimum && metric.Value >= minimum - 1e-9;
            passed &= ok;
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {metric.Name} | {metric.Cases} | {metric.Format()} | {(hasMinimum ? minimum.ToString("0.000", CultureInfo.InvariantCulture) : "missing")} | {(ok ? "pass" : "FAIL")} |");
        }

        var failed = failures.ToList();
        if (failed.Count > 0)
        {
            report.AppendLine().AppendLine("## Cases that did not pass").AppendLine();
            foreach (var failure in failed)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"- {failure}");
            }
        }

        return (passed, report.ToString());
    }
}

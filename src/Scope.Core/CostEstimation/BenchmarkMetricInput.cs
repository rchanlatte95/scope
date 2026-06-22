namespace Scope.Core.CostEstimation;

using Scope.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Serialization;

// Public contract preserved (T-2026-06-17 sweep, owner): BenchmarkMetricInput is registered in
// ScopeJsonSerializerContext, and its public static loaders return concrete List<T>/Task<List<T>>
// shapes that are public Scope.Core API. Concrete return collection types preserved.
#pragma warning disable MA0016 // public Scope.Core benchmark-input API; concrete loader return collection types preserved

/// <summary>
/// Lightweight input extracted from benchmark metrics JSON for cost estimation.
/// </summary>
public sealed class BenchmarkMetricInput
{
    [JsonPropertyName("benchmark_id")]
    public string BenchmarkId { get; init; } = "";

    [JsonPropertyName("target_symbol")]
    public string TargetSymbol { get; init; } = "";

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("quality_score")]
    public decimal? QualityScore { get; init; }


    /// <summary>
    /// Parses benchmark metric results JSON and extracts input token counts.
    /// Accepts both the full BenchmarkMetricResult array format and a simplified input format.
    /// </summary>
    public static List<BenchmarkMetricInput> LoadFromMetricsJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected a JSON array of benchmark metrics.");

        var results = new List<BenchmarkMetricInput>();
        var enumerate = root.EnumerateArray();
        foreach (var element in enumerate)
        {
            var id = element.TryGetProperty("benchmark_id", out var idProp)
                ? idProp.GetString() ?? ""
                : "";

            var target = element.TryGetProperty("target_symbol", out var targetProp)
                ? targetProp.GetString() ?? ""
                : "";

            int tokens;
            if (element.TryGetProperty("final_prompt_token_count", out var fptcProp))
                tokens = fptcProp.GetInt32();
            else if (element.TryGetProperty("budget_used", out var buProp))
                tokens = buProp.GetInt32();
            else if (element.TryGetProperty("input_tokens", out var itProp))
                tokens = itProp.GetInt32();
            else
                tokens = 0;

            decimal? quality = null;
            if (element.TryGetProperty("quality_score", out var qsProp) && qsProp.ValueKind == JsonValueKind.Number)
                quality = qsProp.GetDecimal();

            results.Add(new BenchmarkMetricInput
            {
                BenchmarkId = id,
                TargetSymbol = target,
                InputTokens = tokens,
                QualityScore = quality
            });
        }

        return results;
    }

    /// <summary>
    /// Loads from a file path.
    /// </summary>
    public static List<BenchmarkMetricInput> LoadJsonFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Benchmark metrics file not found: {path}");

        string json = File.ReadAllText(path);
        return LoadFromMetricsJson(json);
    }

    /// <summary>
    /// Loads from a file path asyncronously.
    /// </summary>
    public static async Task<List<BenchmarkMetricInput>> LoadJsonFromFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Benchmark metrics file not found: {path}");

        ct.ThrowIfCancellationRequested();

        string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return LoadFromMetricsJson(json);
    }

    public string ToJsonString()
    => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.BenchmarkMetricInput);

    public static BenchmarkMetricInput? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.BenchmarkMetricInput);
}

#pragma warning restore MA0016

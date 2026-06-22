namespace Scope.Core.CostEstimation;

using Scope.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Serialization;

// Public contract preserved (T-2026-06-17 sweep, owner): these cost-report DTOs are registered in
// ScopeJsonSerializerContext with pinned [JsonPropertyName] fields and round-trip via ToJsonString/
// FromJsonString. Their concrete List<T> properties are the serialized shape and must not change.
#pragma warning disable MA0016 // public cost-report contract; concrete collection properties are the serialized shape

public sealed class CostReport
{
    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; init; } = "";

    [JsonPropertyName("assumptions")]
    public CostReportAssumptions Assumptions { get; init; } = new();

    [JsonPropertyName("cases")]
    public List<CostReportCase> Cases { get; init; } = [];

    [JsonPropertyName("aggregate_summary")]
    public CostAggregateSummary? AggregateSummary { get; init; }

    [JsonPropertyName("shock_scenarios")]
    public List<CostReportShockResult> ShockScenarios { get; init; } = [];

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CostReport);

    public static CostReport? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CostReport);
}

public sealed class CostAggregateSummary
{
    [JsonPropertyName("average_cost_by_model")]
    public List<ModelAggregateCost> AverageCostByModel { get; init; } = [];

    [JsonPropertyName("total_cost_by_model")]
    public List<ModelAggregateCost> TotalCostByModel { get; init; } = [];

    [JsonPropertyName("highest_cost_case")]
    public CaseHighlight? HighestCostCase { get; init; }

    [JsonPropertyName("lowest_cost_case")]
    public CaseHighlight? LowestCostCase { get; init; }

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CostAggregateSummary);

    public static CostAggregateSummary? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CostAggregateSummary);
}

public sealed class ModelAggregateCost
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";

    [JsonPropertyName("cost_usd")]
    public decimal CostUsd { get; init; }

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.ModelAggregateCost);

    public static ModelAggregateCost? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.ModelAggregateCost);
}

public sealed class CaseHighlight
{
    [JsonPropertyName("benchmark_id")]
    public string BenchmarkId { get; init; } = "";

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";

    [JsonPropertyName("cost_usd")]
    public decimal CostUsd { get; init; }

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CaseHighlight);

    public static CaseHighlight? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CaseHighlight);
}

public sealed class CostReportAssumptions
{
    [JsonPropertyName("estimated_output_tokens")]
    public int EstimatedOutputTokens { get; init; }

    [JsonPropertyName("estimated_agent_loops")]
    public int EstimatedAgentLoops { get; init; }

    [JsonPropertyName("include_cached_input")]
    public bool IncludeCachedInput { get; init; }

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CostReportAssumptions);

    public static CostReportAssumptions? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CostReportAssumptions);
}

public sealed class CostReportCase
{
    [JsonPropertyName("benchmark_id")]
    public string BenchmarkId { get; init; } = "";

    [JsonPropertyName("target_symbol")]
    public string TargetSymbol { get; init; } = "";

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("quality_score")]
    public decimal? QualityScore { get; init; }

    [JsonPropertyName("cost_per_quality_point")]
    public List<CostPerQualityEntry>? CostPerQualityPoint { get; init; }

    [JsonPropertyName("estimates")]
    public List<ModelCostEstimate> Estimates { get; init; } = [];

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CostReportCase);

    public static CostReportCase? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CostReportCase);
}

public sealed class CostPerQualityEntry
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";

    [JsonPropertyName("cost_per_quality_point")]
    public decimal? CostPerQualityPoint { get; init; }

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CostPerQualityEntry);

    public static CostPerQualityEntry? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CostPerQualityEntry);
}

public sealed class CostReportShockResult
{
    [JsonPropertyName("scenario_name")]
    public string ScenarioName { get; init; } = "";

    [JsonPropertyName("input_multiplier")]
    public decimal InputMultiplier { get; init; }

    [JsonPropertyName("output_multiplier")]
    public decimal OutputMultiplier { get; init; }

    [JsonPropertyName("loop_multiplier")]
    public decimal LoopMultiplier { get; init; }

    [JsonPropertyName("cases")]
    public List<CostReportCase> Cases { get; init; } = [];

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CostReportShockResult);

    public static CostReportShockResult? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CostReportShockResult);
}

#pragma warning restore MA0016

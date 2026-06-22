namespace Scope.Core.CostEstimation;

using System.Text.Json.Serialization;

public sealed class ModelCostEstimate
{
    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "";

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("estimated_output_tokens")]
    public int EstimatedOutputTokens { get; init; }

    [JsonPropertyName("estimated_agent_loops")]
    public int EstimatedAgentLoops { get; init; }

    [JsonPropertyName("estimated_input_cost_usd")]
    public decimal EstimatedInputCostUsd { get; init; }

    [JsonPropertyName("estimated_output_cost_usd")]
    public decimal EstimatedOutputCostUsd { get; init; }

    [JsonPropertyName("estimated_total_cost_usd")]
    public decimal EstimatedTotalCostUsd { get; init; }
}

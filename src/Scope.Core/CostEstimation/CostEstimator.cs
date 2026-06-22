namespace Scope.Core.CostEstimation;

/// <summary>
/// Computes cost estimates for a given token count across all configured models.
/// </summary>
public sealed class CostEstimator(ModelPricingConfig config)
{
    /// <summary>
    /// Estimates cost for a single input token count across all models in the pricing config.
    /// </summary>
#pragma warning disable MA0016 // public Scope.Core estimator API; concrete return type preserved (T-2026-06-17 sweep, owner)
    public List<ModelCostEstimate> Estimate(int inputTokens, int? outputTokens = null, int? agentLoops = null, bool? useCachedInput = null)
    {
#pragma warning restore MA0016
        var defaults = config.Defaults ?? new CostDefaults();
        var estOutput = outputTokens ?? defaults.EstimatedOutputTokens;
        var estLoops = agentLoops ?? defaults.EstimatedAgentLoops;
        var useCached = useCachedInput ?? defaults.IncludeCachedInput;

        return config.Models.Select(model => ComputeEstimate(model, inputTokens, estOutput, estLoops, useCached)).ToList();
    }

    /// <summary>
    /// Estimates cost with a shock scenario multiplier applied.
    /// </summary>
#pragma warning disable MA0016 // public Scope.Core estimator API; concrete return type preserved (T-2026-06-17 sweep, owner)
    public List<ModelCostEstimate> EstimateWithShock(int inputTokens, ShockScenario shock, int? outputTokens = null, int? agentLoops = null, bool? useCachedInput = null)
    {
#pragma warning restore MA0016
        var defaults = config.Defaults ?? new CostDefaults();
        var estOutput = outputTokens ?? defaults.EstimatedOutputTokens;
        var estLoops = agentLoops ?? defaults.EstimatedAgentLoops;
        var useCached = useCachedInput ?? defaults.IncludeCachedInput;

        var shockedInput = (int)(inputTokens * shock.InputMultiplier);
        var shockedOutput = (int)(estOutput * shock.OutputMultiplier);
        var shockedLoops = (int)(estLoops * shock.LoopMultiplier);

        return config.Models.Select(model => ComputeEstimate(model, shockedInput, shockedOutput, shockedLoops, useCached)).ToList();
    }

    private static ModelCostEstimate ComputeEstimate(ModelPricing model, int inputTokens, int outputTokens, int agentLoops, bool useCachedInput)
    {
        var inputRate = useCachedInput && model.CachedInputUsdPerMillionTokens.HasValue
            ? model.CachedInputUsdPerMillionTokens.Value
            : model.InputUsdPerMillionTokens;

        var inputCost = inputTokens / 1_000_000m * inputRate;
        var outputCost = outputTokens / 1_000_000m * model.OutputUsdPerMillionTokens;
        var totalCost = (inputCost + outputCost) * agentLoops;

        return new ModelCostEstimate
        {
            ModelId = model.Id,
            Provider = model.Provider,
            Tier = model.Tier,
            InputTokens = inputTokens,
            EstimatedOutputTokens = outputTokens,
            EstimatedAgentLoops = agentLoops,
            EstimatedInputCostUsd = inputCost,
            EstimatedOutputCostUsd = outputCost,
            EstimatedTotalCostUsd = totalCost
        };
    }
}

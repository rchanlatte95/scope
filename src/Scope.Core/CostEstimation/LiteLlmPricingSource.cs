namespace Scope.Core.CostEstimation;

using Scope.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Parser for the public LiteLLM model pricing catalog
/// (<c>model_prices_and_context_window.json</c>, BerriAI/litellm, MIT-licensed).
///
/// Source rationale (ST-04, 2026-06-11): a single well-maintained machine-readable
/// JSON beats scraping provider HTML docs. The file is the canonical pricing table
/// LiteLLM itself ships, updated continuously by a broad contributor base, and covers
/// Anthropic (<c>claude-*</c>), OpenAI (<c>gpt-*</c>, <c>o*</c>), and Gemini
/// (<c>gemini-*</c>) model ids in one stable, flat shape. Raw URL:
/// <c>https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json</c>.
///
/// Shape: a flat JSON object keyed by model id. Each entry is an object with
/// per-token USD costs (<c>input_cost_per_token</c>, <c>output_cost_per_token</c>,
/// <c>cache_read_input_token_cost</c>, <c>cache_creation_input_token_cost</c>) and a
/// <c>litellm_provider</c> tag. Scope's
/// internal <see cref="ModelPricing"/> uses USD-per-million-tokens, so costs are scaled
/// by 1e6. The leading <c>sample_spec</c> documentation entry and any entry lacking
/// numeric input+output costs (embeddings, moderation, mislabeled rows) are skipped.
///
/// Parsing is tolerant by construction: a malformed top-level document throws (the
/// caller keeps last-known), but individual malformed entries are skipped rather than
/// failing the whole catalog.
/// </summary>
public static class LiteLlmPricingSource
{
    /// <summary>The canonical raw URL for the maintained LiteLLM pricing catalog.</summary>
    public const string DefaultSourceUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    /// <summary>The documentation placeholder key LiteLLM ships at the top of the file.</summary>
    private const string SampleSpecKey = "sample_spec";

    /// <summary>
    /// Parses the LiteLLM catalog JSON into a <see cref="ModelPricingConfig"/> whose
    /// <see cref="ModelPricingConfig.PricesAsOf"/> is set to <paramref name="pricesAsOf"/>
    /// (ISO-8601 UTC, the fetch timestamp). Entries without numeric input+output costs are
    /// skipped. Throws on a structurally invalid document (caller keeps last-known).
    /// </summary>
    public static ModelPricingConfig Parse(string json, string pricesAsOf)
    {
        var entries = JsonSerializer.Deserialize(
            json, ScopeJsonSerializerContext.Default.DictionaryStringLiteLlmModelEntry)
            ?? throw new InvalidOperationException("LiteLLM pricing catalog deserialized to null.");

        var models = new List<ModelPricing>(entries.Count);
        foreach (var (id, entry) in entries)
        {
            if (string.Equals(id, SampleSpecKey, StringComparison.Ordinal) || entry is null) continue;

            // Require numeric input + output token costs; skip embeddings/moderation/etc.
            if (entry.InputCostPerToken is not { } input || entry.OutputCostPerToken is not { } output)
                continue;

            models.Add(new ModelPricing
            {
                Id = id,
                Provider = NormalizeProvider(entry.LiteLlmProvider),
                Tier = "",
                InputUsdPerMillionTokens = ToPerMillion(input),
                OutputUsdPerMillionTokens = ToPerMillion(output),
                CachedInputUsdPerMillionTokens = entry.CacheReadInputTokenCost is { } cached
                    ? ToPerMillion(cached)
                    : null,
                CacheCreationUsdPerMillionTokens = entry.CacheCreationInputTokenCost is { } cacheWrite
                    ? ToPerMillion(cacheWrite)
                    : null,
            });
        }

        return new ModelPricingConfig
        {
            Models = models,
            PricesAsOf = pricesAsOf,
        };
    }

    /// <summary>Per-token USD (a tiny double like 1e-6) to USD-per-million-tokens decimal.</summary>
    private static decimal ToPerMillion(double perToken) => (decimal)perToken * 1_000_000m;

    /// <summary>
    /// Collapses LiteLLM's provider variants to a coarse provider name Scope uses.
    /// LiteLLM tags Gemini under <c>vertex_ai-*</c>/<c>gemini</c> and OpenAI/Anthropic
    /// under their own names; this maps the Gemini/Vertex family to <c>google</c> and
    /// passes others through verbatim.
    /// </summary>
    internal static string NormalizeProvider(string? provider)
    {
        if (provider.IsNullOrWhitespace) return "";
        var p = provider!.Trim();
        if (p.StartsWith("vertex_ai", StringComparison.OrdinalIgnoreCase)
            || p.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            || p.Equals("google", StringComparison.OrdinalIgnoreCase))
            return "google";
        return p;
    }
}

/// <summary>
/// One model entry in the LiteLLM catalog. Only the fields Scope prices on are bound;
/// the dozens of capability flags LiteLLM ships are ignored. All numeric fields are
/// nullable so a row missing a cost is skipped rather than priced at zero.
/// </summary>
public sealed class LiteLlmModelEntry
{
    [JsonPropertyName("input_cost_per_token")]
    public double? InputCostPerToken { get; init; }

    [JsonPropertyName("output_cost_per_token")]
    public double? OutputCostPerToken { get; init; }

    [JsonPropertyName("cache_read_input_token_cost")]
    public double? CacheReadInputTokenCost { get; init; }

    [JsonPropertyName("cache_creation_input_token_cost")]
    public double? CacheCreationInputTokenCost { get; init; }

    [JsonPropertyName("litellm_provider")]
    public string? LiteLlmProvider { get; init; }
}

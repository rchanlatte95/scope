namespace Scope.Core.CostEstimation;

using Scope.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

// Public contract preserved (T-2026-06-17 sweep, owner): ModelPricingConfig / CostAssumptions are
// registered in ScopeJsonSerializerContext and round-trip pricing.json files; their List<T> properties
// are the serialized shape (and are normalized null->[] on load). Concrete types preserved.
#pragma warning disable MA0016 // public pricing-config contract; concrete collection properties are the serialized shape

public sealed class ModelPricingConfig
{
    [JsonPropertyName("models")]
    public List<ModelPricing> Models { get; init; } = [];

    [JsonPropertyName("defaults")]
    public CostDefaults? Defaults { get; init; }

    [JsonPropertyName("shockScenarios")]
    public List<ShockScenario> ShockScenarios { get; init; } = [];

    /// <summary>
    /// "Prices as of" stamp for this resolved price set: the fetch timestamp of the
    /// public source, or the file modification date of an explicit local pricing.json.
    /// Null when the date is unknown (e.g. a hand-written config loaded directly).
    /// ISO-8601 (round-trip) UTC. Surfaced wherever cost renders so consumers can show
    /// "prices as of &lt;date&gt;" and feed the scope-trace-v1 Money.pricing_version field.
    /// </summary>
    [JsonPropertyName("pricesAsOf")]
    public string? PricesAsOf { get; init; }

    // Source-generated type info with comment-skipping added via a derived options instance.
    // This is AOT-safe: GetTypeInfo resolves against the source-gen metadata, not reflection.
    // ReadCommentHandling is added here because user-supplied pricing JSON files may contain
    // comments; it is NOT added to the shared [JsonSourceGenerationOptions] to avoid widening
    // blast radius across all types in the context.
    private static readonly JsonTypeInfo<ModelPricingConfig> ConfigTypeInfo =
        (JsonTypeInfo<ModelPricingConfig>)new JsonSerializerOptions(ScopeJsonSerializerContext.Default.Options)
        {
            ReadCommentHandling = JsonCommentHandling.Skip
        }.GetTypeInfo(typeof(ModelPricingConfig))!;

    /// <summary>
    /// Normalizes a deserialized config: supplies defaults for any optional properties
    /// that were absent in the JSON. Source-gen may leave collection properties null and
    /// does not apply <c>= new()</c> / <c>= []</c> initializers for absent JSON keys when
    /// the type info is resolved via a derived <see cref="JsonSerializerOptions"/> instance.
    /// </summary>
    private static ModelPricingConfig Normalize(ModelPricingConfig config)
    {
        var needsRewrite = config.Defaults is null
                           || config.Models is null
                           || config.ShockScenarios is null;
        if (!needsRewrite) return config;
        return new ModelPricingConfig
        {
            Models = config.Models ?? [],
            Defaults = config.Defaults ?? new CostDefaults(),
            ShockScenarios = config.ShockScenarios ?? [],
            PricesAsOf = config.PricesAsOf
        };
    }

    /// <summary>
    /// Loads pricing configuration from a JSON file.
    /// </summary>
    public static ModelPricingConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Pricing file not found: {path}");

        var json = File.ReadAllText(path);
        var config = Normalize(JsonSerializer.Deserialize(json, ConfigTypeInfo)
            ?? throw new InvalidOperationException($"Failed to deserialize pricing config from: {path}"));

        if (config.Models.IsNullOrEmpty)
            throw new InvalidOperationException("Pricing config contains no models.");

        return config;
    }

    /// <summary>
    /// Loads pricing configuration from a JSON string.
    /// </summary>
    public static ModelPricingConfig LoadFromJson(string json)
    {
        var config = Normalize(JsonSerializer.Deserialize(json, ConfigTypeInfo)
            ?? throw new InvalidOperationException("Failed to deserialize pricing config."));

        if (config.Models.IsNullOrEmpty)
            throw new InvalidOperationException("Pricing config contains no models.");

        return config;
    }
}

public sealed class ModelPricing
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "";

    [JsonPropertyName("inputUsdPerMillionTokens")]
    public decimal InputUsdPerMillionTokens { get; init; }

    [JsonPropertyName("outputUsdPerMillionTokens")]
    public decimal OutputUsdPerMillionTokens { get; init; }

    [JsonPropertyName("cachedInputUsdPerMillionTokens")]
    public decimal? CachedInputUsdPerMillionTokens { get; init; }

    /// <summary>
    /// Official per-million-tokens rate for cache-creation (cache-write) input tokens, when the
    /// pricing source publishes one (Anthropic's prompt-caching write rate; LiteLLM
    /// <c>cache_creation_input_token_cost</c>). Null when the source does not list it, in which
    /// case a dispatch carrying cache_creation tokens cannot be priced exactly. Optional and
    /// nullable so configs written before this field existed still deserialize unchanged.
    /// </summary>
    [JsonPropertyName("cacheCreationUsdPerMillionTokens")]
    public decimal? CacheCreationUsdPerMillionTokens { get; init; }
}

public sealed class CostDefaults
{
    [JsonPropertyName("estimatedOutputTokens")]
    public int EstimatedOutputTokens { get; init; } = 1000;

    [JsonPropertyName("estimatedAgentLoops")]
    public int EstimatedAgentLoops { get; init; } = 1;

    [JsonPropertyName("includeCachedInput")]
    public bool IncludeCachedInput { get; init; }
}

public sealed class ShockScenario
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("inputMultiplier")]
    public decimal InputMultiplier { get; init; } = 1;

    [JsonPropertyName("outputMultiplier")]
    public decimal OutputMultiplier { get; init; } = 1;

    [JsonPropertyName("loopMultiplier")]
    public decimal LoopMultiplier { get; init; } = 1;
}

/// <summary>
/// Standalone assumptions file that can override defaults and shock scenarios from a separate JSON file.
/// </summary>
public sealed class CostAssumptions
{
    [JsonPropertyName("estimatedOutputTokens")]
    public int? EstimatedOutputTokens { get; init; }

    [JsonPropertyName("estimatedAgentLoops")]
    public int? EstimatedAgentLoops { get; init; }

    [JsonPropertyName("includeCachedInput")]
    public bool? IncludeCachedInput { get; init; }

    [JsonPropertyName("shockScenarios")]
    public List<ShockScenario> ShockScenarios { get; init; } = [];

    /// <summary>
    /// Loads assumptions from a JSON file.
    /// </summary>
    public static CostAssumptions LoadJsonFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Assumptions file not found: {path}");

        string json = File.ReadAllText(path, encoding: System.Text.Encoding.UTF8);
        return FromJsonString(json)
            ?? throw new InvalidOperationException($"Failed to deserialize assumptions from: {path}");
    }

    /// <summary>
    /// Loads assumptions from a JSON file asyncronously.
    /// </summary>
    public static async Task<CostAssumptions> LoadJsonFromFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Assumptions file not found: {path}");

        ct.ThrowIfCancellationRequested();

        string json = await File.ReadAllTextAsync(path, encoding: System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
        return FromJsonString(json)
            ?? throw new InvalidOperationException($"Failed to deserialize assumptions from: {path}");
    }

    /// <summary>
    /// Applies assumptions onto a pricing config, returning a new config with merged defaults and shock scenarios.
    /// </summary>
    public ModelPricingConfig ApplyTo(ModelPricingConfig config)
    {
        var baseDefaults = config.Defaults ?? new CostDefaults();
        return new ModelPricingConfig
        {
            Models = config.Models,
            Defaults = new CostDefaults
            {
                EstimatedOutputTokens = EstimatedOutputTokens ?? baseDefaults.EstimatedOutputTokens,
                EstimatedAgentLoops = EstimatedAgentLoops ?? baseDefaults.EstimatedAgentLoops,
                IncludeCachedInput = IncludeCachedInput ?? baseDefaults.IncludeCachedInput
            },
            ShockScenarios = ShockScenarios.IsInhabited ? ShockScenarios : config.ShockScenarios,
            PricesAsOf = config.PricesAsOf
        };
    }

    public string ToJsonString()
        => JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.CostAssumptions);

    public static CostAssumptions? FromJsonString(string? json)
        => json.IsNullOrWhitespace
        ? null
        : JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.CostAssumptions);
}

#pragma warning restore MA0016

namespace Scope.Core.CostEstimation;

/// <summary>
/// Built-in, checked-in rate table seeded with published Anthropic and OpenAI list prices as of
/// 2026-06-14. Used as a fallback when no external pricing config (local pricing.json or fetched
/// LiteLLM cache) is available. Provides <see cref="CostProvenance.ProviderEstimated"/> provenance
/// because the rates come from the provider's own published list; they are NOT wire-metered.
///
/// Provenance contract: seed rates yield <c>provider_estimated</c>, NOT <c>wire_exact</c> or
/// <c>joined_exact</c>. Wire-exact paths in <see cref="Scope.Core.Cron.Run.CronCostLookup"/> still
/// take precedence when the external pricing table resolves a matching model. The seed table is the
/// last-resort fallback only.
///
/// Platform-independence: the table is keyed by (provider, model-family key). Model-id normalization
/// (stripping version suffixes, cross-region prefixes, context-window tags) is delegated to
/// <see cref="ModelIdNormalizer"/>; no provider-specific parsing logic lives here.
///
/// Rates are flagged <c>verify-needed</c> in comments; they reflect Anthropic and OpenAI published
/// list prices as of the <c>AsOfDate</c> but phase-2 online refresh will supersede them. Do NOT
/// present these as wire-exact or authoritative; <c>provider_estimated</c> is the correct label.
///
/// AOT-safe: all data is plain static arrays; no reflection.
/// </summary>
public static class SeedPricingTable
{
    /// <summary>Date snapshot for the seed rates. All entries carry this date.</summary>
    public const string AsOfDate = "2026-06-14";

    /// <summary>Source label surfaced in provenance metadata.</summary>
    public const string SourceLabel = "scope-seed-v1 (verify-needed; phase-2 refresh pending)";

    // -------------------------------------------------------------------------
    // Seed entries
    //
    // All rates are USD per MILLION tokens (same unit as ModelPricing).
    // Sources: Anthropic pricing page, OpenAI pricing page -- 2026-06-14 snapshot.
    // Mark: verify-needed (phase-2 `scope pricing refresh` will keep these honest).
    //
    // Naming convention for Id: lowercase provider family slug matching what
    // ModelIdNormalizer returns as the lookup key. Multiple aliases for the same
    // family resolve to the SAME entry via ModelIdNormalizer, not by duplicating rows.
    // -------------------------------------------------------------------------

    // Anthropic -- Claude 4 family (as of 2026-06-14; verify-needed)
    // https://www.anthropic.com/pricing  (API pricing table)

    // claude-haiku-4-5: $1.00 in / $5.00 out / $0.10 cache-read / $1.25 cache-write per M
    // Verified from claude-api skill (cached 2026-06-04); cache rates follow standard Anthropic
    // formula (10% of input for read, 125% of input for write) -- verify-needed if Anthropic
    // publishes explicit cache rates for this model.
    private static readonly ModelPricing ClaudeHaiku45 = new()
    {
        Id = "claude-haiku-4-5",
        Provider = "anthropic",
        Tier = "low",
        InputUsdPerMillionTokens = 1.00m,
        OutputUsdPerMillionTokens = 5.00m,
        CachedInputUsdPerMillionTokens = 0.10m,
        CacheCreationUsdPerMillionTokens = 1.25m,
    };

    // claude-sonnet-4-6: $3.00 in / $15.00 out / $0.30 cache-read / $3.75 cache-write per M
    // Verified from claude-api skill (cached 2026-06-04). Same rate as claude-sonnet-4 family.
    // Separate entry so the normalizer can map claude-sonnet-4-6 directly to an exact match.
    private static readonly ModelPricing ClaudeSonnet46 = new()
    {
        Id = "claude-sonnet-4-6",
        Provider = "anthropic",
        Tier = "mid",
        InputUsdPerMillionTokens = 3.00m,
        OutputUsdPerMillionTokens = 15.00m,
        CachedInputUsdPerMillionTokens = 0.30m,
        CacheCreationUsdPerMillionTokens = 3.75m,
    };

    // claude-opus-4-8: $5.00 in / $25.00 out / $0.50 cache-read / $6.25 cache-write per M
    // Verified from claude-api skill (cached 2026-06-04). NOTE: distinct from claude-opus-4 ($15/$75).
    // Cache rates follow standard Anthropic formula -- verify-needed if Anthropic publishes explicit rates.
    private static readonly ModelPricing ClaudeOpus48 = new()
    {
        Id = "claude-opus-4-8",
        Provider = "anthropic",
        Tier = "high",
        InputUsdPerMillionTokens = 5.00m,
        OutputUsdPerMillionTokens = 25.00m,
        CachedInputUsdPerMillionTokens = 0.50m,
        CacheCreationUsdPerMillionTokens = 6.25m,
    };

    // claude-fable-5: $10.00 in / $50.00 out / $1.00 cache-read / $12.50 cache-write per M
    // Verified from claude-api skill (cached 2026-06-04). NOTE: distinct from claude-fable ($15/$75).
    // Cache rates follow standard Anthropic formula -- verify-needed if Anthropic publishes explicit rates.
    private static readonly ModelPricing ClaudeFable5 = new()
    {
        Id = "claude-fable-5",
        Provider = "anthropic",
        Tier = "high",
        InputUsdPerMillionTokens = 10.00m,
        OutputUsdPerMillionTokens = 50.00m,
        CachedInputUsdPerMillionTokens = 1.00m,
        CacheCreationUsdPerMillionTokens = 12.50m,
    };

    // claude-opus-4:  $15 in / $75 out / $1.50 cache-read / $18.75 cache-write per M
    // claude-opus-4-5 is the same family; 200K context variant priced identically per Anthropic docs.
    private static readonly ModelPricing ClaudeOpus4 = new()
    {
        Id = "claude-opus-4",
        Provider = "anthropic",
        Tier = "high",
        InputUsdPerMillionTokens = 15.00m,
        OutputUsdPerMillionTokens = 75.00m,
        CachedInputUsdPerMillionTokens = 1.50m,
        CacheCreationUsdPerMillionTokens = 18.75m,
    };

    // claude-sonnet-4:  $3 in / $15 out / $0.30 cache-read / $3.75 cache-write per M
    private static readonly ModelPricing ClaudeSonnet4 = new()
    {
        Id = "claude-sonnet-4",
        Provider = "anthropic",
        Tier = "mid",
        InputUsdPerMillionTokens = 3.00m,
        OutputUsdPerMillionTokens = 15.00m,
        CachedInputUsdPerMillionTokens = 0.30m,
        CacheCreationUsdPerMillionTokens = 3.75m,
    };

    // claude-haiku-3-5:  $0.80 in / $4 out / $0.08 cache-read / $1.00 cache-write per M
    private static readonly ModelPricing ClaudeHaiku35 = new()
    {
        Id = "claude-haiku-3-5",
        Provider = "anthropic",
        Tier = "low",
        InputUsdPerMillionTokens = 0.80m,
        OutputUsdPerMillionTokens = 4.00m,
        CachedInputUsdPerMillionTokens = 0.08m,
        CacheCreationUsdPerMillionTokens = 1.00m,
    };

    // claude-haiku-3:  $0.25 in / $1.25 out / $0.03 cache-read / $0.30 cache-write per M
    private static readonly ModelPricing ClaudeHaiku3 = new()
    {
        Id = "claude-haiku-3",
        Provider = "anthropic",
        Tier = "low",
        InputUsdPerMillionTokens = 0.25m,
        OutputUsdPerMillionTokens = 1.25m,
        CachedInputUsdPerMillionTokens = 0.03m,
        CacheCreationUsdPerMillionTokens = 0.30m,
    };

    // claude-fable (internal Scope model tier alias for the anthropic fable/research model;
    // rates from Anthropic pricing page 2026-06-14; verify-needed)
    // claude-fable uses Opus-tier pricing per Anthropic docs.
    private static readonly ModelPricing ClaudeFable = new()
    {
        Id = "claude-fable",
        Provider = "anthropic",
        Tier = "high",
        InputUsdPerMillionTokens = 15.00m,
        OutputUsdPerMillionTokens = 75.00m,
        CachedInputUsdPerMillionTokens = 1.50m,
        CacheCreationUsdPerMillionTokens = 18.75m,
    };

    // claude-sonnet-3-7: $3 in / $15 out / $0.30 cache-read / $3.75 cache-write per M
    private static readonly ModelPricing ClaudeSonnet37 = new()
    {
        Id = "claude-sonnet-3-7",
        Provider = "anthropic",
        Tier = "mid",
        InputUsdPerMillionTokens = 3.00m,
        OutputUsdPerMillionTokens = 15.00m,
        CachedInputUsdPerMillionTokens = 0.30m,
        CacheCreationUsdPerMillionTokens = 3.75m,
    };

    // claude-sonnet-3-5: $3 in / $15 out / $0.30 cache-read / $3.75 cache-write per M
    private static readonly ModelPricing ClaudeSonnet35 = new()
    {
        Id = "claude-sonnet-3-5",
        Provider = "anthropic",
        Tier = "mid",
        InputUsdPerMillionTokens = 3.00m,
        OutputUsdPerMillionTokens = 15.00m,
        CachedInputUsdPerMillionTokens = 0.30m,
        CacheCreationUsdPerMillionTokens = 3.75m,
    };

    // claude-opus-3: $15 in / $75 out / $1.50 cache-read / $18.75 cache-write per M
    private static readonly ModelPricing ClaudeOpus3 = new()
    {
        Id = "claude-opus-3",
        Provider = "anthropic",
        Tier = "high",
        InputUsdPerMillionTokens = 15.00m,
        OutputUsdPerMillionTokens = 75.00m,
        CachedInputUsdPerMillionTokens = 1.50m,
        CacheCreationUsdPerMillionTokens = 18.75m,
    };

    // OpenAI -- GPT family (as of 2026-06-14; verify-needed)
    // https://openai.com/api/pricing  (input/output/cached-input per M tokens)

    // gpt-4o:  $2.50 in / $10 out / $1.25 cache-read per M (no cache-write fee for OpenAI)
    private static readonly ModelPricing Gpt4o = new()
    {
        Id = "gpt-4o",
        Provider = "openai",
        Tier = "mid",
        InputUsdPerMillionTokens = 2.50m,
        OutputUsdPerMillionTokens = 10.00m,
        CachedInputUsdPerMillionTokens = 1.25m,
        CacheCreationUsdPerMillionTokens = null, // OpenAI does not publish a cache-write surcharge
    };

    // gpt-4o-mini:  $0.15 in / $0.60 out / $0.075 cache-read per M
    private static readonly ModelPricing Gpt4oMini = new()
    {
        Id = "gpt-4o-mini",
        Provider = "openai",
        Tier = "low",
        InputUsdPerMillionTokens = 0.15m,
        OutputUsdPerMillionTokens = 0.60m,
        CachedInputUsdPerMillionTokens = 0.075m,
        CacheCreationUsdPerMillionTokens = null,
    };

    // gpt-4.1:  $2 in / $8 out / $0.50 cache-read per M
    private static readonly ModelPricing Gpt41 = new()
    {
        Id = "gpt-4.1",
        Provider = "openai",
        Tier = "mid",
        InputUsdPerMillionTokens = 2.00m,
        OutputUsdPerMillionTokens = 8.00m,
        CachedInputUsdPerMillionTokens = 0.50m,
        CacheCreationUsdPerMillionTokens = null,
    };

    // gpt-4.1-mini:  $0.40 in / $1.60 out / $0.10 cache-read per M
    private static readonly ModelPricing Gpt41Mini = new()
    {
        Id = "gpt-4.1-mini",
        Provider = "openai",
        Tier = "low",
        InputUsdPerMillionTokens = 0.40m,
        OutputUsdPerMillionTokens = 1.60m,
        CachedInputUsdPerMillionTokens = 0.10m,
        CacheCreationUsdPerMillionTokens = null,
    };

    // gpt-4.1-nano:  $0.10 in / $0.40 out / $0.025 cache-read per M
    private static readonly ModelPricing Gpt41Nano = new()
    {
        Id = "gpt-4.1-nano",
        Provider = "openai",
        Tier = "low",
        InputUsdPerMillionTokens = 0.10m,
        OutputUsdPerMillionTokens = 0.40m,
        CachedInputUsdPerMillionTokens = 0.025m,
        CacheCreationUsdPerMillionTokens = null,
    };

    // o4-mini:  $1.10 in / $4.40 out / $0.275 cache-read per M
    private static readonly ModelPricing O4Mini = new()
    {
        Id = "o4-mini",
        Provider = "openai",
        Tier = "mid",
        InputUsdPerMillionTokens = 1.10m,
        OutputUsdPerMillionTokens = 4.40m,
        CachedInputUsdPerMillionTokens = 0.275m,
        CacheCreationUsdPerMillionTokens = null,
    };

    /// <summary>
    /// All seed entries. This array is the authority for the built-in table;
    /// update rates here and bump <see cref="AsOfDate"/> when publishing a new snapshot.
    /// Declared before <see cref="Config"/> to ensure correct static-field init order.
    /// </summary>
    internal static readonly ModelPricing[] Entries =
    [
        // Anthropic -- current generation (claude-api verified 2026-06-04; cache rates verify-needed)
        ClaudeHaiku45,
        ClaudeSonnet46,
        ClaudeOpus48,
        ClaudeFable5,
        // Anthropic -- Claude 4 family (older/family-level entries; verify-needed)
        ClaudeOpus4,
        ClaudeSonnet4,
        ClaudeHaiku35,
        ClaudeHaiku3,
        ClaudeFable,
        ClaudeSonnet37,
        ClaudeSonnet35,
        ClaudeOpus3,
        // OpenAI
        Gpt4o,
        Gpt4oMini,
        Gpt41,
        Gpt41Mini,
        Gpt41Nano,
        O4Mini,
    ];

    /// <summary>
    /// Returns the full built-in rate table as a <see cref="ModelPricingConfig"/>.
    /// The config's <c>PricesAsOf</c> is set to <see cref="AsOfDate"/>. Defaults and
    /// shock scenarios are left empty (the seed table is cost-estimation fallback only).
    /// Declared after <see cref="Entries"/> to ensure the spread initializer sees a
    /// non-null array (C# static fields initialize in declaration order).
    /// </summary>
    public static ModelPricingConfig Config { get; } = new ModelPricingConfig
    {
        Models = [.. Entries],
        PricesAsOf = AsOfDate,
    };

    /// <summary>
    /// Converts a raw <c>PricesAsOf</c> value to a SEMANTIC DATE string (yyyy-MM-dd).
    ///
    /// Rule: the external LiteLLM cache stamps its <c>PricesAsOf</c> with a FETCH TIMESTAMP
    /// such as "2026-06-15T18:57:06.17Z". The DATE part of that timestamp is the honest
    /// semantic "prices as of" because the cache reflects live prices observed on that date.
    /// The time component is a load artifact, not semantically meaningful.
    ///
    /// Conversion:
    /// <list type="bullet">
    ///   <item>null / empty / whitespace -> <see cref="AsOfDate"/> (seed table fallback).</item>
    ///   <item>10+ char string starting with a valid yyyy-MM-dd prefix -> first 10 chars.</item>
    ///   <item>Anything else (unparseable, too short) -> <see cref="AsOfDate"/>.</item>
    /// </list>
    ///
    /// Result never contains a 'T' or ':' character (it is a date, not a datetime).
    /// AOT-safe: no reflection; only string slicing and a quick validity check.
    /// </summary>
    public static string ToSemanticDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return AsOfDate;

        // A valid ISO date has at least 10 chars and the 5th and 8th chars are '-'.
        if (raw.Length >= 10 && raw[4] == '-' && raw[7] == '-')
            return raw[..10];

        return AsOfDate;
    }
}

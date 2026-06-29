namespace Scope.Core.CostEstimation;

/// <summary>
/// Normalizes observed model ids from telemetry (e.g. <c>wire_calls.model</c>,
/// <c>obs_agent_spans.model_id</c>) to a canonical pricing-table lookup key that matches a
/// <see cref="ModelPricing.Id"/> in either <see cref="SeedPricingTable.Config"/> or a
/// user-supplied <see cref="ModelPricingConfig"/>.
///
/// Design rules (platform-independence contract):
/// <list type="bullet">
///   <item>All provider-specific id-parsing lives HERE and nowhere else.</item>
///   <item>An unknown id returns <c>null</c>; the caller must treat null as "no rate available"
///         and return <see cref="Scope.Core.Cron.Run.CostProvenance.Unavailable"/> rather than
///         inventing a price.</item>
///   <item>The normalizer is stateless, pure, and AOT-safe (no reflection).</item>
/// </list>
///
/// Normalization steps applied in order:
/// <list type="number">
///   <item>Strip whitespace and lower-case.</item>
///   <item>Strip cross-region AWS Bedrock prefix (<c>us.anthropic.</c>, <c>eu.anthropic.</c>,
///         <c>ap.anthropic.</c>, similarly for OpenAI Bedrock: <c>us.openai.</c> etc.).</item>
///   <item>Strip date-version suffixes (e.g. <c>-20241022</c>, <c>-20250514</c>).</item>
///   <item>Strip context-window tags appended by harness adapters (e.g. <c>[1m]</c>, <c>[200k]</c>).</item>
///   <item>Map the resulting slug to the canonical pricing key via a static prefix/exact table.</item>
/// </list>
///
/// Examples:
/// <list type="bullet">
///   <item><c>claude-opus-4-8</c>       -> <c>claude-opus-4-8</c> ($5/$25 rate, not $15/$75)</item>
///   <item><c>claude-opus-4-8[1m]</c>   -> <c>claude-opus-4-8</c></item>
///   <item><c>claude-opus-4-5</c>       -> <c>claude-opus-4</c></item>
///   <item><c>claude-fable-5</c>        -> <c>claude-fable-5</c> ($10/$50 rate, not $15/$75)</item>
///   <item><c>claude-haiku-4-5</c>      -> <c>claude-haiku-4-5</c></item>
///   <item><c>us.anthropic.claude-3-5-sonnet-20241022</c> -> <c>claude-sonnet-3-5</c></item>
///   <item><c>claude-sonnet-4-5</c>     -> <c>claude-sonnet-4</c></item>
///   <item><c>claude-sonnet-4-6</c>     -> <c>claude-sonnet-4</c> (same price, no separate entry needed)</item>
///   <item><c>gpt-4o-2024-11-20</c>     -> <c>gpt-4o</c></item>
///   <item><c>gpt-4o-mini-2024-07-18</c> -> <c>gpt-4o-mini</c></item>
///   <item><c>claude-fable-0525-20250519</c> -> <c>claude-fable</c></item>
///   <item><c>unknown-model-xyz</c>     -> <c>null</c> (no rate)</item>
/// </list>
/// </summary>
public static class ModelIdNormalizer
{
    // -------------------------------------------------------------------------
    // Static prefix-map: (normalized prefix) -> canonical pricing key.
    // Evaluated in definition order; first match wins.
    // Update when a new family is added to SeedPricingTable.
    // -------------------------------------------------------------------------

    private static readonly (string Prefix, string Key)[] PrefixMap =
    [
        // Anthropic -- NEW id format: claude-{name}-{version} (e.g. claude-sonnet-4, claude-opus-4)
        // Current-generation exact-match entries MUST come before the broader family prefixes so that
        // e.g. claude-opus-4-8 -> "claude-opus-4-8" (not the older $15/$75 "claude-opus-4" rate).
        ("claude-opus-4-8",  "claude-opus-4-8"),    // BEFORE claude-opus-4 (different price: $5/$25 vs $15/$75)
        ("claude-fable-5",   "claude-fable-5"),     // BEFORE claude-fable (different price: $10/$50 vs $15/$75)
        ("claude-haiku-4-5", "claude-haiku-4-5"),   // BEFORE claude-haiku-3-5 (distinct model family)
        ("claude-opus-4",    "claude-opus-4"),
        ("claude-fable",     "claude-fable"),       // internal fable family
        ("claude-sonnet-4",  "claude-sonnet-4"),
        ("claude-sonnet-3-7","claude-sonnet-3-7"),
        ("claude-sonnet-3-5","claude-sonnet-3-5"),
        ("claude-haiku-3-5", "claude-haiku-3-5"),
        ("claude-haiku-3",   "claude-haiku-3"),
        ("claude-opus-3",    "claude-opus-3"),
        // Anthropic -- OLD id format: claude-{version}-{name} (e.g. claude-3-5-sonnet, claude-3-opus)
        // Used in older API ids and Bedrock ARNs (e.g. us.anthropic.claude-3-5-sonnet-20241022).
        // These are checked AFTER new-format entries so new names win.
        ("claude-3-7-sonnet","claude-sonnet-3-7"),
        ("claude-3-5-sonnet","claude-sonnet-3-5"),
        ("claude-3-5-haiku", "claude-haiku-3-5"),
        ("claude-3-haiku",   "claude-haiku-3"),
        ("claude-3-opus",    "claude-opus-3"),
        // OpenAI -- GPT-4o family
        ("gpt-4o-mini",      "gpt-4o-mini"),        // mini before 4o so shorter prefix doesn't win
        ("gpt-4o",           "gpt-4o"),
        ("gpt-4.1-mini",     "gpt-4.1-mini"),
        ("gpt-4.1-nano",     "gpt-4.1-nano"),
        ("gpt-4.1",          "gpt-4.1"),
        ("o4-mini",          "o4-mini"),
        ("o3-mini",          "o4-mini"),             // o3-mini -> treat as o4-mini tier (verify-needed)
        ("o4",               "o4-mini"),             // full o4 -> fallback to mini rates (verify-needed; no o4 list price yet)
        // OpenAI -- GPT-5 family (pricing from developers.openai.com/api/docs/pricing, 2026-06-29;
        // dash-notation IDs are the expected Codex CLI wire model IDs per T-2026-06-29-08;
        // verify-needed until G-baseurl gate passes and Phase B captures real traffic).
        // Specific entries (gpt-5-codex, gpt-5-mini) MUST precede bare gpt-5 (F9 ordering).
        // Dot-notation aliases (gpt-5.3-codex, gpt-5.4-mini) handle OpenAI public API model IDs
        // so they don't fall through to bare gpt-5 pricing ($5.00/M vs $1.75/M or $0.75/M).
        ("gpt-5-codex",      "gpt-5-codex"),        // dash-notation (Codex CLI; verify-needed)
        ("gpt-5.3-codex",    "gpt-5-codex"),        // dot-notation OpenAI API alias
        ("gpt-5-mini",       "gpt-5-mini"),          // dash-notation
        ("gpt-5.4-mini",     "gpt-5-mini"),          // dot-notation OpenAI API alias
        ("gpt-5",            "gpt-5"),               // gpt-5 exact + gpt-5.x catch-all via dot match
        // OpenAI -- o3 reasoning model (developers.openai.com/api/docs/models/o3, 2026-06-29)
        ("o3",               "o3"),
    ];

    // Date-version suffix pattern: 8-digit date like -20241022 or -20250514
    // We strip the LAST occurrence of -YYYYMMDD.
    private const string DateSuffixChars = "0123456789";

    /// <summary>
    /// Normalizes <paramref name="rawModelId"/> to a canonical pricing-table key.
    /// Returns <c>null</c> when the id is unknown, empty, or cannot be mapped to any seeded model.
    /// Never throws.
    /// </summary>
    public static string? Normalize(string? rawModelId)
    {
        if (string.IsNullOrWhiteSpace(rawModelId)) return null;
        if (string.Equals(rawModelId, "-", StringComparison.Ordinal)) return null;

        var s = rawModelId.Trim().ToLowerInvariant();

        // 1. Strip context-window tags like [1m], [200k], [128k] appended by harness adapters.
        var bracketIdx = s.IndexOf('[');
        if (bracketIdx > 0) s = s[..bracketIdx].TrimEnd();

        // 2. Strip cross-region prefix (us.anthropic., eu.anthropic., ap.anthropic., etc.)
        //    Pattern: two-letter region + '.' + provider + '.'
        s = StripCrossRegionPrefix(s);

        // 3. Strip trailing date-version suffixes (-YYYYMMDD).
        s = StripDateSuffix(s);

        // 4. Match the first prefix in the map.
        foreach (var (prefix, key) in PrefixMap)
        {
            // Exact match or prefix match (prefix + '-' digit suffix for minor variants like -4-8)
            if (string.Equals(s, prefix, StringComparison.Ordinal)
                || s.StartsWith(prefix + "-", StringComparison.Ordinal)
                || s.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null; // Unknown model -> no rate -> Unavailable
    }

    /// <summary>
    /// Looks up a <see cref="ModelPricing"/> entry for <paramref name="rawModelId"/> by first
    /// normalizing the id, then searching a caller-supplied config (operator-override), then
    /// falling back to the built-in <see cref="SeedPricingTable.Config"/>. Returns null when
    /// no rate is found (caller must emit <c>unavailable</c> provenance, never a fabricated price).
    /// </summary>
    /// <param name="rawModelId">The observed model id from telemetry.</param>
    /// <param name="externalConfig">
    /// Operator-supplied pricing config (from local pricing.json or the LiteLLM cache). When
    /// non-null its models take precedence over the seed table for the SAME normalized key.
    /// When null the seed table is the only source.
    /// </param>
    public static ModelPricing? Lookup(string? rawModelId, ModelPricingConfig? externalConfig)
    {
        var key = Normalize(rawModelId);
        if (key == null) return null;

        // External config models win (they are operator- or provider-grade overrides).
        // We try BOTH the raw id AND the normalized key so a local pricing.json with
        // the exact raw id (e.g. "claude-sonnet-4-5") also matches.
        if (externalConfig != null)
        {
            foreach (var m in externalConfig.Models)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(m.Id, rawModelId)
                    || StringComparer.OrdinalIgnoreCase.Equals(m.Id, key))
                    return m;
            }
        }

        // Fall back to seed table (by normalized key only).
        foreach (var m in SeedPricingTable.Entries)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(m.Id, key))
                return m;
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string StripCrossRegionPrefix(string s)
    {
        // Patterns: "us.anthropic.", "eu.anthropic.", "ap.anthropic.", "us.openai.", etc.
        // Generalized: {2-3 letter region}.{provider}.
        var firstDot = s.IndexOf('.');
        if (firstDot < 1 || firstDot > 4) return s;          // no short region prefix
        var secondDot = s.IndexOf('.', firstDot + 1);
        if (secondDot < 0) return s;
        return s[(secondDot + 1)..];
    }

    private static string StripDateSuffix(string s)
    {
        // A date suffix is -YYYYMMDD at the end: exactly a hyphen followed by 8 digits.
        if (s.Length < 9) return s;
        var tail = s[^8..];
        if (s[^9] == '-' && IsAllDigits(tail))
            return s[..^9];
        return s;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }
}

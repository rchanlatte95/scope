namespace Scope.Core.CostEstimation;

/// <summary>
/// Resolves a short model ALIAS (the family nicknames a harness accepts on a dispatch, e.g.
/// Claude Code's <c>tool_input.model: "haiku"</c>) to the CURRENT full model id of that family.
///
/// HONESTY CONTRACT: alias -> full id is a CURRENT-DEFAULT resolution, not an exact-version fact.
/// A bare alias like "opus" names a family/tier, not a specific version; we map it to the version
/// that is current as of the seed pricing snapshot (<see cref="SeedPricingTable.AsOfDate"/>). This
/// pins the cost TIER honestly but is still an assumption about the exact version, so callers must
/// treat an alias-derived model as a "recorded/requested" tier (Tier 2), never as wire truth.
///
/// Lives next to <see cref="ModelIdNormalizer"/> and <see cref="SeedPricingTable"/> so the alias
/// targets are updated in lock-step with the pricing table when a new current default ships.
/// Platform-independence: this is the ONLY place short harness aliases are expanded.
/// </summary>
public static class ModelAliasMap
{
    // alias -> current full model id (the default version of that family as of the seed snapshot).
    // Keep these in sync with SeedPricingTable / the harness's accepted --model aliases. When a
    // family's current default version changes, update the target here (and the pricing entry).
    private static readonly (string Alias, string CurrentFullId)[] Map =
    [
        ("haiku",  "claude-haiku-4-5"),
        ("sonnet", "claude-sonnet-4-6"),
        ("opus",   "claude-opus-4-8"),
        ("fable",  "claude-fable-5"),
    ];

    /// <summary>
    /// Returns the current full model id for a short family alias (case-insensitive), or null when
    /// the input is null/blank or is not one of the known short aliases. A value that is NOT a bare
    /// alias (e.g. an already-full id such as "claude-opus-4-8" or a versioned id) returns null here;
    /// callers should fall through to <see cref="ModelIdNormalizer"/> for those. Never throws.
    /// </summary>
    public static string? ResolveAliasToCurrentId(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        var slug = alias.Trim().ToLowerInvariant();
        foreach (var (a, fullId) in Map)
        {
            if (string.Equals(slug, a, StringComparison.Ordinal)) return fullId;
        }
        return null;
    }
}

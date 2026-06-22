namespace Scope.Core.CostEstimation;

/// <summary>
/// Canonical per-model token-bucket to USD arithmetic. This is the single source of truth for
/// pricing a (input, output, cache_creation, cache_read) token tuple against a <see cref="ModelPricing"/>
/// rate, so the interactive (cron wire-join), cron-estimated, and shadow/trace-rollup paths all price
/// identically.
/// <para>
/// All pricing is <see cref="decimal"/> (T-2026-06-20-20 Piece 2): the prior <c>ComputeBucketCostDouble</c>
/// variant was retired because <see cref="decimal"/> arithmetic is exact for money, so reconciliation
/// invariants (sum == total) hold to the cent rather than "to ~9 digits", and the trace-rollup and cron
/// paths now share ONE numeric family. <see cref="ComputeComponentCost"/> exposes the per-component term
/// (tokens/1e6 * rate) so a breakdown's components and total derive from the identical decimal math.
/// Term order is input, output, cache_creation, cache_read; each cache term is added only when its bucket
/// is positive AND the corresponding nullable rate is present. decimal addition is exact and
/// order-independent, so the historical cache-term ordering no longer affects the result.
/// </para>
/// </summary>
public static class CostPricing
{
    /// <summary>
    /// Prices a single token-bucket component in <see cref="decimal"/> USD: <c>tokens / 1e6 * rate</c>.
    /// Used to build per-component cost breakdowns so each component and the bucket total share the
    /// exact same decimal arithmetic (no double round-trip).
    /// </summary>
    /// <param name="tokens">Token count for this component.</param>
    /// <param name="ratePerMillion">The per-million-tokens rate for this component.</param>
    /// <returns>The component cost in USD as a <see cref="decimal"/>.</returns>
    public static decimal ComputeComponentCost(long tokens, decimal ratePerMillion)
        => tokens / 1_000_000m * ratePerMillion;

    /// <summary>
    /// Prices a token-bucket tuple in <see cref="decimal"/> USD. Replicates the cron exact/estimated
    /// arithmetic exactly: input and output are always priced; a cache term is added only when its
    /// bucket is positive AND the corresponding nullable rate is present. Term order is input, output,
    /// cache_creation, cache_read. A positive cache bucket with an absent rate contributes nothing
    /// (the caller is responsible for any upstream policy decision, e.g. falling back to estimation).
    /// </summary>
    /// <param name="input">Input (prompt) token count.</param>
    /// <param name="output">Output (completion) token count.</param>
    /// <param name="cacheCreation">Cache-creation (cache-write) token count.</param>
    /// <param name="cacheRead">Cache-read (cached-input) token count.</param>
    /// <param name="rate">The per-million-tokens pricing rates for the model.</param>
    /// <returns>The computed cost in USD as a <see cref="decimal"/>.</returns>
    public static decimal ComputeBucketCost(
        long input,
        long output,
        long cacheCreation,
        long cacheRead,
        ModelPricing rate)
    {
        decimal total = 0m;
        total += input / 1_000_000m * rate.InputUsdPerMillionTokens;
        total += output / 1_000_000m * rate.OutputUsdPerMillionTokens;
        if (cacheCreation > 0 && rate.CacheCreationUsdPerMillionTokens.HasValue)
            total += cacheCreation / 1_000_000m * rate.CacheCreationUsdPerMillionTokens.Value;
        if (cacheRead > 0 && rate.CachedInputUsdPerMillionTokens.HasValue)
            total += cacheRead / 1_000_000m * rate.CachedInputUsdPerMillionTokens.Value;
        return total;
    }
}

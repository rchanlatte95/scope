namespace Scope.Core.CostEstimation;

using Scope.Core.Utilities;
using Serilog;
using System.Text.Json;

/// <summary>
/// Resolves the effective pricing set the cost layer estimates against, applying the
/// ST-04 precedence rule and stamping a "prices as of" date.
///
/// Precedence (highest first):
/// <list type="number">
///   <item>Explicit local <c>pricing.json</c> (<c>SCOPE_PRICING_PATH</c> env var, else
///   <c>.scope/pricing.json</c> next to the database). A model id present here OVERRIDES
///   the fetched price for that model.</item>
///   <item>The hourly-refreshed public catalog cached by the proxy at
///   <c>.scope/pricing.cache.json</c> (see <see cref="PricingCache"/>). Fills gaps for
///   models the local file does not list.</item>
/// </list>
///
/// Offline-first: every step degrades to last-known. A missing/unreadable cache is simply
/// absent (the local file alone is used); a missing local file falls back to the cache.
/// When neither is present <see cref="TryResolve"/> returns null and the caller skips cost
/// estimation exactly as it did before ST-04 (no behavior change, never a failure).
///
/// pricesAsOf: the resolved config carries a <see cref="ModelPricingConfig.PricesAsOf"/>
/// stamp. An explicit local file dominates the date (its last-write time UTC); when only the
/// cache contributes, the cache's <c>fetchedAtUtc</c> is used. This is the single seam through
/// which the "prices as of" date reaches the cost-compute path and, downstream, the
/// scope-trace-v1 <c>Money.pricing_version</c> field, with no edits to the trace projection.
/// </summary>
public static class PricingResolver
{
    /// <summary>
    /// Resolves the effective pricing config for cost estimation given the database
    /// directory. Applies local-override-beats-fetched precedence and stamps pricesAsOf.
    /// Returns null when no pricing information is available from any source (caller
    /// then skips cost estimation, unchanged from pre-ST-04 behavior).
    /// </summary>
    /// <param name="scopeDir">The <c>.scope</c> directory (cache + default local file live here).</param>
    /// <param name="explicitLocalPath">
    /// An explicit local pricing.json path (typically from <c>SCOPE_PRICING_PATH</c>), or null.
    /// </param>
    public static ModelPricingConfig? TryResolve(string scopeDir, string? explicitLocalPath)
    {
        var local = TryLoadLocal(explicitLocalPath, scopeDir, out var localAsOf);
        var fetched = PricingCache.TryLoad(CacheFilePath(scopeDir));

        if (local is null && fetched is null) return null;
        if (fetched is null) return local; // local already carries its own pricesAsOf
        if (local is null) return fetched;

        return Merge(local, localAsOf, fetched);
    }

    /// <summary>The cache file the proxy writes the refreshed catalog to.</summary>
    public static string CacheFilePath(string scopeDir) => Path.Combine(scopeDir, "pricing.cache.json");

    /// <summary>
    /// Merges a local override over a fetched catalog: every fetched model is included,
    /// then local models replace matching ids and add any the catalog lacks. The local
    /// file dominates the pricesAsOf stamp (it is the operator's authoritative choice).
    /// Defaults and shock scenarios come from the local file when present (the fetched
    /// catalog carries none).
    /// </summary>
    private static ModelPricingConfig Merge(ModelPricingConfig local, string? localAsOf, ModelPricingConfig fetched)
    {
        var byId = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in fetched.Models) byId[m.Id] = m;
        foreach (var m in local.Models) byId[m.Id] = m; // local wins per-model

        return new ModelPricingConfig
        {
            Models = [.. byId.Values],
            Defaults = local.Defaults,
            ShockScenarios = local.ShockScenarios,
            // A local override means the operator pinned prices: stamp with the local
            // file date when known, else fall back to the fetch date so the stamp is
            // never silently dropped.
            PricesAsOf = localAsOf ?? fetched.PricesAsOf,
        };
    }

    /// <summary>
    /// Loads the explicit local pricing.json when present, else <c>.scope/pricing.json</c>.
    /// The model's last-write time (UTC, round-trip) becomes <paramref name="asOf"/> so the
    /// stamp reflects when the operator last edited their prices. Returns null when no local
    /// file exists or it fails to parse (debug-logged, never throws to the caller).
    /// </summary>
    private static ModelPricingConfig? TryLoadLocal(string? explicitLocalPath, string scopeDir, out string? asOf)
    {
        asOf = null;
        var path = explicitLocalPath.IsInhabited && File.Exists(explicitLocalPath)
            ? explicitLocalPath
            : Path.Combine(scopeDir, "pricing.json");

        if (!File.Exists(path)) return null;

        try
        {
            var config = ModelPricingConfig.LoadFromFile(path);
            asOf = File.GetLastWriteTimeUtc(path).ToString("o");
            // Stamp the local config too so a local-only resolution still surfaces a date.
            return config.PricesAsOf is null
                ? new ModelPricingConfig
                {
                    Models = config.Models,
                    Defaults = config.Defaults,
                    ShockScenarios = config.ShockScenarios,
                    PricesAsOf = asOf,
                }
                : config;
        }
        catch (Exception ex)
        {
            Log.Debug("Local pricing.json at {Path} unreadable; ignoring: {Message}", path, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// The on-disk cache envelope the proxy writes the refreshed public catalog to
/// (<c>.scope/pricing.cache.json</c>). Carries the raw source JSON plus the fetch
/// timestamp so the resolver can both reparse and stamp pricesAsOf without a second
/// network call. Reading is tolerant: any failure yields null (last-known is then the
/// absence of a cache), never an exception to the cost path.
/// </summary>
public static class PricingCache
{
    /// <summary>
    /// Reads and parses the cache file into a stamped <see cref="ModelPricingConfig"/>.
    /// Returns null on any read/parse failure (debug-logged); callers treat null as
    /// "no fetched prices available" and fall back to last-known (local file or nothing).
    /// </summary>
    public static ModelPricingConfig? TryLoad(string cachePath)
    {
        if (!File.Exists(cachePath)) return null;
        try
        {
            var json = File.ReadAllText(cachePath);
            var envelope = JsonSerializer.Deserialize(json, ScopeJsonSerializerContext.Default.PricingCacheEnvelope);
            if (envelope is null || envelope.Catalog.IsNullOrWhitespace) return null;

            var asOf = envelope.FetchedAtUtc.IsInhabited
                ? envelope.FetchedAtUtc!
                : File.GetLastWriteTimeUtc(cachePath).ToString("o");

            var config = LiteLlmPricingSource.Parse(envelope.Catalog!, asOf);
            return config.Models.IsInhabited ? config : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Pricing cache at {Path} unreadable; ignoring: {Message}", cachePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes the cache envelope atomically (temp file + move) so a concurrent reader
    /// never observes a half-written file. Best-effort: failures are debug-logged and
    /// swallowed (the refresh loop keeps last-known on disk).
    /// </summary>
    public static bool TryWrite(string cachePath, string catalogJson, DateTime fetchedAtUtc)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cachePath));
            if (dir is not null) Directory.CreateDirectory(dir);

            var envelope = new PricingCacheEnvelope
            {
                Source = LiteLlmPricingSource.DefaultSourceUrl,
                FetchedAtUtc = fetchedAtUtc.ToString("o"),
                Catalog = catalogJson,
            };
            var json = JsonSerializer.Serialize(envelope, ScopeJsonSerializerContext.Default.PricingCacheEnvelope);

            var tmp = cachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, cachePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("Pricing cache write to {Path} failed; keeping last-known: {Message}", cachePath, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// Cache envelope persisted to <c>.scope/pricing.cache.json</c>. Stores the verbatim
/// upstream catalog (so reparsing is local) and the fetch timestamp (the pricesAsOf date).
/// </summary>
public sealed class PricingCacheEnvelope
{
    /// <summary>The source URL the catalog was fetched from (provenance).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public string Source { get; init; } = "";

    /// <summary>ISO-8601 UTC fetch timestamp; becomes the resolved pricesAsOf stamp.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fetchedAtUtc")]
    public string? FetchedAtUtc { get; init; }

    /// <summary>The verbatim LiteLLM catalog JSON, stored so reparsing needs no network.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("catalog")]
    public string? Catalog { get; init; }
}

namespace Scope.Core.Entitlement;

using Serilog;
using System.Text.Json;

/// <summary>
/// On-disk cache envelope for the signed entitlement token. Stored at the machine-level
/// %APPDATA%/.scope/entitlement.json (entitlement is per-user/org, not per-workspace).
/// </summary>
public sealed class EntitlementCacheEnvelope
{
    /// <summary>The signed token envelope string: base64url(payload).base64url(signature).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("envelope")]
    public string? Envelope { get; init; }

    /// <summary>ISO-8601 UTC timestamp when the token was last successfully fetched from the control plane.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fetched_at_utc")]
    public string? FetchedAtUtc { get; init; }
}

/// <summary>
/// Tolerant read/write cache for the signed entitlement token envelope.
///
/// Cache location: %APPDATA%/.scope/entitlement.json
/// This is a machine-level path (per-user/org), NOT a per-workspace .scope directory.
///
/// Read semantics: missing or corrupt file resolves to "no token" (returns null). Never throws.
/// Write semantics: atomic temp-file-then-move (writes to a .tmp sibling then renames over the
/// target), ensuring concurrent readers never observe a partial write. Best-effort; failures logged.
/// </summary>
public static class EntitlementCache
{
    /// <summary>
    /// Returns the path to the entitlement cache file for the current user.
    /// Path: %APPDATA%/.scope/entitlement.json
    /// </summary>
    public static string CacheFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".scope", "entitlement.json");
    }

    /// <summary>
    /// Reads the full cache entry (envelope + FetchedAtUtc) from the default path.
    /// Returns null when the file is missing, empty, or corrupt. Never throws.
    ///
    /// The returned <see cref="EntitlementCacheEnvelope.FetchedAtUtc"/> is used by the
    /// refresher as the success-throttle anchor. It does NOT re-anchor grace; grace is
    /// always computed from <c>expires_at_utc</c> inside the signed token.
    /// </summary>
    public static EntitlementCacheEnvelope? TryReadEntry()
        => TryReadEntry(CacheFilePath());

    /// <summary>
    /// Reads the full cache entry from a specified path (overload for testing).
    /// Returns null when the file is missing, empty, or corrupt. Never throws.
    /// </summary>
    public static EntitlementCacheEnvelope? TryReadEntry(string cachePath)
    {
        if (!File.Exists(cachePath)) return null;
        try
        {
            var json = File.ReadAllText(cachePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize(json, EntitlementJsonContext.Default.EntitlementCacheEnvelope);
        }
        catch (Exception ex)
        {
            Log.Debug("EntitlementCache: TryReadEntry at {Path} failed: {Message}", cachePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads the cached token envelope string from disk. Returns null when the file is
    /// missing, empty, or corrupt. Never throws; any failure is debug-logged.
    /// </summary>
    public static string? TryReadEnvelope()
        => TryReadEnvelope(CacheFilePath());

    /// <summary>
    /// Reads the cached token envelope string from a specified path (overload for testing).
    /// Returns null when the file is missing, empty, or corrupt. Never throws.
    /// </summary>
    public static string? TryReadEnvelope(string cachePath)
    {
        if (!File.Exists(cachePath)) return null;
        try
        {
            var json = File.ReadAllText(cachePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var envelope = JsonSerializer.Deserialize(json, EntitlementJsonContext.Default.EntitlementCacheEnvelope);
            if (envelope?.Envelope is { Length: > 0 } tokenEnvelope)
                return tokenEnvelope;

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug("EntitlementCache: read at {Path} failed; treating as no token: {Message}", cachePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes the signed token envelope atomically to the default cache path.
    /// Returns true on success; false on any failure (debug-logged, never throws).
    /// </summary>
    public static bool TryWrite(string tokenEnvelope, DateTime fetchedAtUtc)
        => TryWrite(CacheFilePath(), tokenEnvelope, fetchedAtUtc);

    /// <summary>
    /// Writes the signed token envelope atomically to a specified path (overload for testing).
    /// Uses temp-file + move to avoid partial writes visible to concurrent readers.
    /// Returns true on success; false on any failure (debug-logged, never throws).
    /// </summary>
    public static bool TryWrite(string cachePath, string tokenEnvelope, DateTime fetchedAtUtc)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cachePath));
            if (dir is not null) Directory.CreateDirectory(dir);

            var cacheEntry = new EntitlementCacheEnvelope
            {
                Envelope = tokenEnvelope,
                FetchedAtUtc = fetchedAtUtc.ToString("o"),
            };
            var json = JsonSerializer.Serialize(cacheEntry, EntitlementJsonContext.Default.EntitlementCacheEnvelope);

            // Use a unique GUID-suffixed temp file so concurrent writers (e.g. activation +
            // refresher) cannot collide on a single fixed ".tmp" path (S5).
            var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, cachePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("EntitlementCache: write to {Path} failed: {Message}", cachePath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Removes the cache file if it exists. Best-effort; failures are silently ignored.
    /// Used on sign-out / deactivation.
    /// </summary>
    public static void TryDelete()
        => TryDelete(CacheFilePath());

    /// <summary>Removes the cache file at the given path. Best-effort; never throws.</summary>
    public static void TryDelete(string cachePath)
    {
        try { if (File.Exists(cachePath)) File.Delete(cachePath); }
        catch { /* best effort */ }
    }
}

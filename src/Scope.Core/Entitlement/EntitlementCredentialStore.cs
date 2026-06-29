namespace Scope.Core.Entitlement;

using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------
// DTOs persisted on disk (registered in EntitlementJsonContext)
// ---------------------------------------------------------------------------

/// <summary>
/// The long-lived refresh credential stored at %APPDATA%/.scope/entitlement-credential.json.
/// For the first-cut license-key flow the credential is the license key / token envelope
/// that the user pasted during <c>scope activate</c>. It MUST survive every token refresh
/// (the token cache is overwritten on each successful refresh; this file is not).
/// </summary>
public sealed record EntitlementRefreshCredential
{
    /// <summary>The license key / token envelope supplied by the user.</summary>
    [JsonPropertyName("credential")]
    public string Credential { get; init; } = "";
}

/// <summary>
/// Refresh-attempt state persisted at %APPDATA%/.scope/entitlement-refresh.json.
/// Stores the last-attempt UTC timestamp and consecutive-failure count so throttle and
/// backoff survive across process restarts and inform cross-process coordination.
/// </summary>
public sealed record EntitlementRefreshState
{
    /// <summary>ISO-8601 UTC timestamp of the last refresh attempt, regardless of outcome.</summary>
    [JsonPropertyName("last_attempt_utc")]
    public string? LastAttemptUtc { get; init; }

    /// <summary>Count of consecutive failed attempts; reset to 0 on success.</summary>
    [JsonPropertyName("consecutive_failures")]
    public int ConsecutiveFailures { get; init; }
}

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

/// <summary>
/// Read/write/delete operations for the long-lived entitlement refresh credential and the
/// refresh-attempt state file. Both live at the machine-level %APPDATA%/.scope/ directory
/// (per-user, not per-workspace), SEPARATE from <see cref="EntitlementCache"/> so the
/// credential survives every token refresh.
///
/// <para>
/// File layout:
/// <list type="bullet">
///   <item><c>%APPDATA%/.scope/entitlement-credential.json</c> — long-lived license key.
///     Written on <c>scope activate</c>; cleared on <c>scope deactivate</c>.</item>
///   <item><c>%APPDATA%/.scope/entitlement-refresh.json</c> — last-attempt timestamp and
///     consecutive-failure count. Cleared alongside the credential on deactivation.</item>
/// </list>
/// </para>
///
/// Read semantics: missing or corrupt file returns null. Never throws.
/// Write semantics: atomic GUID-suffixed temp-file + move (same guarantee as
/// <see cref="EntitlementCache.TryWrite"/>). Never throws.
/// </summary>
public sealed class EntitlementCredentialStore
{
    private readonly string _credentialPath;
    private readonly string _refreshStatePath;

    /// <summary>
    /// Creates a store using the default machine-level paths under %APPDATA%/.scope/.
    /// </summary>
    public EntitlementCredentialStore()
        : this(DefaultCredentialPath(), DefaultRefreshStatePath()) { }

    /// <summary>
    /// Creates a store using explicit paths. Intended for test isolation.
    /// </summary>
    public EntitlementCredentialStore(string credentialPath, string refreshStatePath)
    {
        _credentialPath = credentialPath;
        _refreshStatePath = refreshStatePath;
    }

    /// <summary>Default path: %APPDATA%/.scope/entitlement-credential.json</summary>
    public static string DefaultCredentialPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".scope", "entitlement-credential.json");
    }

    /// <summary>Default path: %APPDATA%/.scope/entitlement-refresh.json</summary>
    public static string DefaultRefreshStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".scope", "entitlement-refresh.json");
    }

    // -------------------------------------------------------------------------
    // Credential (long-lived; survives token refreshes)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the stored credential string. Returns null when the file is missing or corrupt.
    /// Never throws.
    /// </summary>
    public string? TryReadCredential()
    {
        try
        {
            if (!File.Exists(_credentialPath)) return null;
            var json = File.ReadAllText(_credentialPath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var entry = JsonSerializer.Deserialize(json, EntitlementJsonContext.Default.EntitlementRefreshCredential);
            return entry?.Credential is { Length: > 0 } c ? c : null;
        }
        catch (Exception ex)
        {
            Log.Debug("EntitlementCredentialStore: read credential at {Path} failed: {Message}", _credentialPath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Atomically writes the credential. Returns true on success; false on any failure (never throws).
    /// </summary>
    public bool TryWriteCredential(string credential)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_credentialPath));
            if (dir is not null) Directory.CreateDirectory(dir);

            var entry = new EntitlementRefreshCredential { Credential = credential };
            var json = JsonSerializer.Serialize(entry, EntitlementJsonContext.Default.EntitlementRefreshCredential);
            var tmp = _credentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _credentialPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("EntitlementCredentialStore: write credential to {Path} failed: {Message}", _credentialPath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Removes the credential file. Best-effort; never throws. Called during deactivation
    /// so a subsequent refresher trigger makes no network call and cannot re-activate the seat.
    /// </summary>
    public void TryDeleteCredential()
    {
        try { if (File.Exists(_credentialPath)) File.Delete(_credentialPath); }
        catch { /* best effort */ }
    }

    // -------------------------------------------------------------------------
    // Refresh state (throttle / backoff persistence across restarts)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the persisted refresh state. Returns null when the file is missing or corrupt.
    /// Never throws.
    /// </summary>
    public EntitlementRefreshState? TryReadRefreshState()
    {
        try
        {
            if (!File.Exists(_refreshStatePath)) return null;
            var json = File.ReadAllText(_refreshStatePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize(json, EntitlementJsonContext.Default.EntitlementRefreshState);
        }
        catch (Exception ex)
        {
            Log.Debug("EntitlementCredentialStore: read refresh state at {Path} failed: {Message}", _refreshStatePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Atomically writes the refresh state. Best-effort; never throws.
    /// </summary>
    public void TryWriteRefreshState(EntitlementRefreshState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_refreshStatePath));
            if (dir is not null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(state, EntitlementJsonContext.Default.EntitlementRefreshState);
            var tmp = _refreshStatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _refreshStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Debug("EntitlementCredentialStore: write refresh state to {Path} failed: {Message}", _refreshStatePath, ex.Message);
        }
    }

    /// <summary>
    /// Removes the refresh state file. Best-effort; never throws. Called during deactivation
    /// to prevent backoff state from persisting after the seat is cleared.
    /// </summary>
    public void TryDeleteRefreshState()
    {
        try { if (File.Exists(_refreshStatePath)) File.Delete(_refreshStatePath); }
        catch { /* best effort */ }
    }
}

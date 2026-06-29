namespace Scope.Core.App;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Scope.Core.Entitlement;
using Scope.Core.Telemetry;
using Scope.Core.Utilities;
using Serilog;

public static partial class ScopeApp
{
    // =========================================================================
    // Token activation / deactivation / license-status surface
    //
    // CLI-ONLY. No HTTP activate endpoint (writing a token is privileged;
    // widening the loophole attack surface is not acceptable) and no MCP
    // activate tool (stdout is protocol-only, MCP never mutates host state).
    // The server picks up CLI activation with zero restart because
    // DefaultEntitlementProvider re-reads and re-verifies on every GetState()
    // call (no in-process cache).
    //
    // B3 (now closed): Activate writes the license key to EntitlementCredentialStore
    // so the refresher has a credential to authenticate with. Deactivate deletes BOTH
    // the token cache AND the credential + refresh-state files so a subsequent refresher
    // trigger makes no network call and cannot silently re-activate the seat.
    // =========================================================================

    /// <summary>
    /// Activates a signed entitlement token envelope on this machine.
    ///
    /// Validation gate (validate-then-persist invariant): the token is fully
    /// verified and resolved BEFORE any write. Nothing is written if the token
    /// fails verification or resolves to a non-entitling status.
    ///
    /// Injection parameters (<paramref name="verifier"/> and
    /// <paramref name="cachePath"/>) are for tests only; production callers
    /// omit them to get the pinned production verifier and the standard
    /// <c>%APPDATA%/.scope/entitlement.json</c> path.
    /// </summary>
    /// <param name="envelope">Signed token envelope: base64url(payload).base64url(sig).</param>
    /// <param name="jsonOutput">When true, emit the success result as a JSON <see cref="CommandEntitlementStatus"/>.</param>
    /// <param name="jsonErrors">When true, emit error details as JSON to stderr.</param>
    /// <param name="verifier">Injected verifier (tests). Null uses the pinned production key set.</param>
    /// <param name="cachePath">Injected cache path (tests). Null uses the default user-level path.</param>
    /// <param name="credentialStore">Injected credential store (tests). Null uses the default paths.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<ScopeCommandResult> ActivateAsync(
        string envelope,
        bool jsonOutput,
        bool jsonErrors,
        EntitlementTokenVerifier? verifier = null,
        string? cachePath = null,
        EntitlementCredentialStore? credentialStore = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var trimmed = envelope.Trim();
        if (trimmed.Length == 0)
            return Task.FromResult(Error(jsonErrors, ScopeExitCode.MissingParameter,
                "No token provided. Pass a token as an argument or use --from-file."));

        var tokenVerifier = verifier ?? new EntitlementTokenVerifier();
        var result = tokenVerifier.Verify(trimmed);

        if (!result.IsValid)
            return Task.FromResult(Error(jsonErrors, ScopeExitCode.EntitlementInvalid,
                "Token invalid: " + result.FailureReason,
                "failure_reason=" + result.FailureReason));

        // N9: reject signed tier=free tokens up-front; they resolve to Paid status but carry only
        // free limits, so activating them would be misleading with no practical benefit.
        if (string.Equals(result.Token!.Tier, EntitlementTiers.Free, StringComparison.Ordinal))
            return Task.FromResult(Error(jsonErrors, ScopeExitCode.EntitlementInvalid,
                "Token is a free-tier token and does not entitle paid features; not activated."));

        var now = DateTime.UtcNow;
        var state = EntitlementStateResolver.Resolve(result.Token, now);

        if (state.Status is not (EntitlementStatus.Paid or EntitlementStatus.Trial or EntitlementStatus.Grace))
        {
            // Include parsed expiry and current time so host-clock-skew cause is obvious.
            var detail = state.ExpiresAt is not null
                ? string.Create(CultureInfo.InvariantCulture, $"expires_at={state.ExpiresAt:o} utc_now={now:o}")
                : string.Create(CultureInfo.InvariantCulture, $"utc_now={now:o}");
            return Task.FromResult(Error(jsonErrors, ScopeExitCode.EntitlementInvalid,
                "Token is expired or not entitling; not activated.", detail));
        }

        var path = cachePath ?? EntitlementCache.CacheFilePath();
        var wrote = EntitlementCache.TryWrite(path, trimmed, now);
        if (!wrote)
            return Task.FromResult(Error(jsonErrors, ScopeExitCode.Unknown,
                "Activation failed: could not write entitlement cache. Check file permissions at: " + path));

        // B3: persist the license key as the refresh credential so the refresher can
        // authenticate with the control plane on subsequent TryRefreshAsync calls.
        // For the first-cut license-key flow, credential = the token envelope the user pasted.
        var store = credentialStore ?? new EntitlementCredentialStore();
        if (!store.TryWriteCredential(trimmed))
        {
            // Non-fatal: token cache is already written, activation succeeded.
            // The refresher will simply have no credential and behave like NoOp until
            // the user re-activates.
            Log.Warning("ScopeApp.ActivateAsync: could not write refresh credential; refresher will be a no-op.");
        }

        Log.Information("Entitlement token activated: tier={Tier} status={Status}", state.Tier, state.Status);
        return Task.FromResult(BuildActivateSuccess(state, jsonOutput));
    }

    /// <summary>
    /// Reports the current entitlement status. Reuses <c>BuildEntitlementStatus</c>
    /// (ScopeApp.Command.cs) to avoid duplicating the state-reading logic.
    ///
    /// Shows "Free (no token)" when no cache file is present, distinguishing the
    /// "never activated" case from "activated but resolved to Free."
    /// </summary>
    /// <param name="jsonOutput">When true, emit output as a JSON <see cref="CommandEntitlementStatus"/>.</param>
    /// <param name="jsonErrors">When true, emit error details as JSON to stderr.</param>
    /// <param name="entitlementProvider">Injected provider (tests). Null uses <see cref="DefaultEntitlementProvider"/>.</param>
    /// <param name="cachePath">Injected cache path (tests) used for the "has token" check. Null uses the default path.</param>
    /// <param name="refresher">Optional refresher (S6). When non-null, a short-bounded refresh is
    ///   awaited before reading status. Never blocks indefinitely; capped at 5 seconds. Throttle
    ///   gate inside the refresher makes this a no-op if recently refreshed.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ScopeCommandResult> LicenseStatusAsync(
        bool jsonOutput,
        bool jsonErrors,
        IEntitlementProvider? entitlementProvider = null,
        string? cachePath = null,
        IEntitlementRefresher? refresher = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // S6: explicit license status command warrants a short-bounded refresh attempt so the
            // user sees the most current server state. The throttle gate inside the refresher makes
            // this a no-op if a refresh happened recently (i.e. within the 4h throttle interval).
            // Bounded at 5 seconds so the command never hangs on a slow/down control plane.
            if (refresher is not null)
            {
                try
                {
                    using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    refreshCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await refresher.TryRefreshAsync(refreshCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Refresh failure is always non-fatal for status display.
                }
            }

            var cPath = cachePath ?? EntitlementCache.CacheFilePath();
            var hasToken = EntitlementCache.TryReadEnvelope(cPath) is not null;
            var provider = entitlementProvider ?? new DefaultEntitlementProvider();

            // BuildEntitlementStatus reads from the default cache path for last_refresh_at.
            // In production both use the same path. Test injections override state via the provider.
            var status = BuildEntitlementStatus(provider, scopeDir: null);

            if (jsonOutput)
            {
                var json = JsonSerializer.Serialize(status, ScopeJsonSerializerContext.Default.CommandEntitlementStatus);
                return ScopeCommandResult.Success(json);
            }

            if (!hasToken)
                return ScopeCommandResult.Success("License status: Free (no token)");

            return ScopeCommandResult.Success(FormatLicenseStatus(status));
        }
        catch (Exception ex)
        {
            Log.Debug("ScopeApp.LicenseStatusAsync failed: {Message}", ex.Message);
            return Error(jsonErrors, ScopeExitCode.Unknown, "Could not read license status.");
        }
    }

    /// <summary>
    /// Removes the entitlement token cache, the refresh credential, and the refresh-state
    /// file, reverting this machine to Free tier. Idempotent: succeeds even when no files exist.
    ///
    /// B3 (closed): all three files are cleared atomically (best-effort; each deletion is
    /// independent). After this method returns, a subsequent refresher trigger finds no
    /// credential, makes NO network call, and cannot silently re-activate the seat.
    /// </summary>
    /// <param name="jsonErrors">When true, emit error details as JSON to stderr.</param>
    /// <param name="cachePath">Injected cache path (tests). Null uses the default user-level path.</param>
    /// <param name="credentialStore">Injected credential store (tests). Null uses the default paths.</param>
    public static ScopeCommandResult Deactivate(
        bool jsonErrors,
        string? cachePath = null,
        EntitlementCredentialStore? credentialStore = null)
    {
        try
        {
            // 1. Remove the token cache.
            var path = cachePath ?? EntitlementCache.CacheFilePath();
            EntitlementCache.TryDelete(path);

            // 2. B3: remove the refresh credential so the refresher cannot re-activate.
            // 3. B3: remove the refresh-state file to avoid stale throttle / failure state.
            var store = credentialStore ?? new EntitlementCredentialStore();
            store.TryDeleteCredential();
            store.TryDeleteRefreshState();

            Log.Information("Entitlement token deactivated; machine reverted to Free tier.");
            return ScopeCommandResult.Success("Reverted to Free tier. Entitlement token removed.");
        }
        catch (Exception ex)
        {
            Log.Warning("ScopeApp.Deactivate failed unexpectedly: {Message}", ex.Message);
            return Error(jsonErrors, ScopeExitCode.Unknown, "Deactivation failed: " + ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="CommandEntitlementStatus"/> directly from an already-resolved
    /// <see cref="EntitlementState"/>. Used by <see cref="ActivateAsync"/> to avoid
    /// re-reading the just-written cache file.
    /// </summary>
    private static CommandEntitlementStatus BuildEntitlementStatusFromState(EntitlementState state)
    {
        var statusStr = state.Status switch
        {
            EntitlementStatus.Free => "free",
            EntitlementStatus.Trial => "trial",
            EntitlementStatus.Paid => "paid",
            EntitlementStatus.Grace => "grace",
            EntitlementStatus.Expired => "expired",
            _ => "unchecked",
        };
        return new CommandEntitlementStatus(
            Tier: state.Tier,
            Status: statusStr,
            LastRefreshAt: null,
            ExpiresAt: state.ExpiresAt?.ToString("o"),
            GraceRemainingSeconds: state.GraceRemaining.HasValue
                ? (long)state.GraceRemaining.Value.TotalSeconds
                : null);
    }

    private static ScopeCommandResult BuildActivateSuccess(EntitlementState state, bool jsonOutput)
    {
        var statusEntry = BuildEntitlementStatusFromState(state);
        if (jsonOutput)
        {
            var json = JsonSerializer.Serialize(statusEntry, ScopeJsonSerializerContext.Default.CommandEntitlementStatus);
            return ScopeCommandResult.Success(json);
        }

        var sb = new StringBuilder();
        sb.AppendLine("Activated successfully.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tier: {statusEntry.Tier}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Status: {statusEntry.Status}");
        if (statusEntry.ExpiresAt is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Expires: {statusEntry.ExpiresAt}");
        if (state.Status == EntitlementStatus.Grace && state.GraceRemaining is not null)
        {
            var r = state.GraceRemaining.Value;
            sb.AppendLine(CultureInfo.InvariantCulture, $"Grace remaining: {(int)r.TotalHours}h {r.Minutes}m");
        }
        return ScopeCommandResult.Success(sb.ToString().TrimEnd());
    }

    private static string FormatLicenseStatus(CommandEntitlementStatus status)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"License status: {status.Status}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tier: {status.Tier}");
        if (status.ExpiresAt is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Expires: {status.ExpiresAt}");
        if (status.LastRefreshAt is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Last refresh: {status.LastRefreshAt}");
        if (status.GraceRemainingSeconds is not null)
        {
            var r = TimeSpan.FromSeconds(status.GraceRemainingSeconds.Value);
            sb.AppendLine(CultureInfo.InvariantCulture, $"Grace remaining: {(int)r.TotalHours}h {r.Minutes}m");
        }
        return sb.ToString().TrimEnd();
    }
}

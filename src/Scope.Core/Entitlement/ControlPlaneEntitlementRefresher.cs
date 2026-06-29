namespace Scope.Core.Entitlement;

using Serilog;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------
// Contract DTOs (registered in EntitlementJsonContext)
// ---------------------------------------------------------------------------

/// <summary>
/// Content-free POST body for /v1/entitlement/validate.
/// Contains only identity metadata — never user telemetry content.
/// </summary>
public sealed record EntitlementValidateRequest
{
    /// <summary>Token schema version; always 1 for the current format.</summary>
    [JsonPropertyName("token_version")]
    public int TokenVersion { get; init; } = 1;

    /// <summary>Client version string for server-side analytics / compatibility gating.</summary>
    [JsonPropertyName("client_version")]
    public string ClientVersion { get; init; } = "";

    /// <summary>The long-lived license credential (license key). Never any user content.</summary>
    [JsonPropertyName("credential")]
    public string Credential { get; init; } = "";
}

/// <summary>
/// 200 OK response body from /v1/entitlement/validate.
/// The <see cref="Envelope"/> is a freshly signed token (either an active paid token or a
/// signed downgrade token when the seat is revoked). Both flow through the identical
/// client verify-then-write path.
/// </summary>
public sealed record EntitlementValidateResponse
{
    /// <summary>Freshly signed token envelope: base64url(payload).base64url(signature).</summary>
    [JsonPropertyName("envelope")]
    public string? Envelope { get; init; }

    /// <summary>Suggested client TTL in seconds; informational only.</summary>
    [JsonPropertyName("ttl_seconds")]
    public int? TtlSeconds { get; init; }
}

// ---------------------------------------------------------------------------
// Transport contract (injectable for tests — no live HttpClient in tests)
// ---------------------------------------------------------------------------

/// <summary>Status values for a /v1/entitlement/validate call outcome.</summary>
public enum EntitlementValidateStatus
{
    /// <summary>HTTP 200 with a parseable response body.</summary>
    Ok,

    /// <summary>HTTP 401: credential is unrecognised or expired at the server.</summary>
    BadCredential,

    /// <summary>Network failure, timeout, 5xx, or an unparseable body.</summary>
    NetworkFailure,
}

/// <summary>Outcome of one /v1/entitlement/validate POST.</summary>
public sealed record EntitlementValidateOutcome(
    EntitlementValidateStatus Status,
    string? Envelope = null,
    int? TtlSeconds = null);

/// <summary>
/// Abstraction over the HTTP transport layer for the control-plane validate endpoint.
/// Inject a fake in tests; use <see cref="HttpClientEntitlementTransport"/> in production.
///
/// Implementations MUST never throw; all failures MUST map to an
/// <see cref="EntitlementValidateStatus.NetworkFailure"/> outcome.
/// </summary>
public interface IEntitlementHttpTransport
{
    /// <summary>
    /// POSTs the content-free validate request and returns the outcome.
    /// Never throws; network / parse failures return NetworkFailure.
    /// </summary>
    Task<EntitlementValidateOutcome> PostValidateAsync(
        string baseUrl,
        EntitlementValidateRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production HTTP transport using <see cref="HttpClient"/>.
/// AOT-safe: uses source-generated JSON (EntitlementJsonContext) exclusively.
/// </summary>
public sealed class HttpClientEntitlementTransport(HttpClient httpClient) : IEntitlementHttpTransport
{
    /// <inheritdoc />
    public async Task<EntitlementValidateOutcome> PostValidateAsync(
        string baseUrl,
        EntitlementValidateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, EntitlementJsonContext.Default.EntitlementValidateRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = baseUrl.TrimEnd('/') + "/v1/entitlement/validate";

            using var response = await httpClient
                .PostAsync(url, content, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new EntitlementValidateOutcome(EntitlementValidateStatus.BadCredential);

            if (!response.IsSuccessStatusCode)
                return new EntitlementValidateOutcome(EntitlementValidateStatus.NetworkFailure);

            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            EntitlementValidateResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(body, EntitlementJsonContext.Default.EntitlementValidateResponse);
            }
            catch (JsonException)
            {
                Log.Warning("HttpClientEntitlementTransport: response body was not valid JSON");
                return new EntitlementValidateOutcome(EntitlementValidateStatus.NetworkFailure);
            }

            if (parsed?.Envelope is not { Length: > 0 } envelope)
            {
                Log.Warning("HttpClientEntitlementTransport: response body missing envelope field");
                return new EntitlementValidateOutcome(EntitlementValidateStatus.NetworkFailure);
            }

            return new EntitlementValidateOutcome(EntitlementValidateStatus.Ok, envelope, parsed.TtlSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException)
        {
            Log.Debug("HttpClientEntitlementTransport: network error: {Message}", ex.Message);
            return new EntitlementValidateOutcome(EntitlementValidateStatus.NetworkFailure);
        }
        catch (Exception ex)
        {
            Log.Warning("HttpClientEntitlementTransport: unexpected error: {Message}", ex.Message);
            return new EntitlementValidateOutcome(EntitlementValidateStatus.NetworkFailure);
        }
    }
}

// ---------------------------------------------------------------------------
// Real refresher
// ---------------------------------------------------------------------------

/// <summary>
/// Production <see cref="IEntitlementRefresher"/> that opportunistically fetches a fresh
/// short-TTL signed token from the control plane, verifies it, and persists it via
/// <see cref="EntitlementCache"/>. Designed for:
///
/// <list type="bullet">
///   <item><b>Throttle (no daemon):</b> at most one POST per throttle interval (default 4h).
///     Throttle is anchored on the attempt timestamp, not the success timestamp, so
///     consecutive failures back off without hammering the server.</item>
///   <item><b>Free-tier zero-network (A3):</b> when no credential is stored, returns false
///     immediately with zero network I/O. A poisoned transport will never be called.</item>
///   <item><b>Fail-closed (A2):</b> invalid/forged tokens are NEVER persisted; network failures
///     NEVER clear the cache; degradation to Free happens only after the token's grace window
///     (anchored on <c>expires_at_utc</c>, not on <c>FetchedAtUtc</c> — C-cite).</item>
///   <item><b>Within-process single-shot (S5):</b> a <see cref="SemaphoreSlim"/> serialises
///     concurrent in-process triggers so at most one POST occurs per interval.</item>
///   <item><b>Cross-process throttle (S5):</b> a best-effort persisted last-attempt timestamp
///     (entitlement-refresh.json) throttles separate OS processes. This is NOT an OS file lock:
///     a TOCTOU race lets two processes crossing the same interval boundary within a sub-second
///     window both POST. Harmless — the validate endpoint is idempotent and both paths
///     verify-then-write an equivalent signed token.</item>
/// </list>
///
/// Constructor parameters are injectable for testing; pass null to use production defaults.
/// </summary>
public sealed class ControlPlaneEntitlementRefresher : IEntitlementRefresher
{
    /// <summary>
    /// Recommended base URL for the control-plane validate endpoint.
    /// Wired at call sites; overridable only via explicit constructor argument (never at runtime
    /// by env var / config, to prevent an attacker substituting a rogue endpoint).
    /// </summary>
    public const string DefaultBaseUrl = "https://control.scope.invalid";

    /// <summary>Default refresh interval: 4 hours (5+ refresh chances per 24h server TTL).</summary>
    public static readonly TimeSpan DefaultThrottleInterval = TimeSpan.FromHours(4);

    private readonly IEntitlementHttpTransport _transport;
    private readonly string _baseUrl;
    private readonly EntitlementTokenVerifier _verifier;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _throttleInterval;
    private readonly EntitlementCredentialStore _credentialStore;
    private readonly string? _cachePath;

    // In-process state (guarded by _lock)
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private int _consecutiveFailures;

    private static readonly Random _jitter = new();

    // UTC parse styles (same as EntitlementStateResolver for consistency)
    private static readonly DateTimeStyles UtcParseStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>
    /// Creates a refresher with production defaults (pinned key set, default base URL, 4h interval,
    /// system clock, default credential paths, default cache path).
    /// </summary>
    public ControlPlaneEntitlementRefresher(IEntitlementHttpTransport transport)
        : this(transport, DefaultBaseUrl) { }

    /// <summary>
    /// Full constructor for test injection. Any null parameter falls back to the production default.
    /// </summary>
    public ControlPlaneEntitlementRefresher(
        IEntitlementHttpTransport transport,
        string baseUrl,
        EntitlementTokenVerifier? verifier = null,
        Func<DateTime>? utcNow = null,
        TimeSpan? throttleInterval = null,
        EntitlementCredentialStore? credentialStore = null,
        string? cachePath = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _transport = transport;
        _baseUrl = baseUrl;
        _verifier = verifier ?? new EntitlementTokenVerifier();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _throttleInterval = throttleInterval ?? DefaultThrottleInterval;
        _credentialStore = credentialStore ?? new EntitlementCredentialStore();
        _cachePath = cachePath; // null = default path inside TryWrite
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never throws. All failures (network, crypto, file I/O) are caught and logged;
    /// the method returns false and the existing cached state governs.
    ///
    /// IMPORTANT: this method never reads <c>FetchedAtUtc</c> for grace computation.
    /// Grace is anchored on <c>expires_at_utc</c> in the signed token (C-cite). The
    /// <see cref="EntitlementCache"/> FetchedAtUtc is used only as the success-throttle anchor.
    /// </remarks>
    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await TryRefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders: must-never-throw posture.
            Log.Warning("ControlPlaneEntitlementRefresher.TryRefreshAsync threw unexpectedly: {Message}", ex.Message);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Core logic
    // -------------------------------------------------------------------------

    private async Task<bool> TryRefreshCoreAsync(CancellationToken cancellationToken)
    {
        var now = _utcNow();

        // Fast in-process throttle check (no lock, no I/O): avoids waking the semaphore for
        // the common case where we're well within the cooldown window.
        if (IsWithinCooldown(now))
        {
            Log.Debug("ControlPlaneEntitlementRefresher: within throttle window; skipping.");
            return false;
        }

        // A3 (MUST): Free-tier zero-network guard. No credential -> IMMEDIATE no-op.
        // A poisoned transport MUST NOT be called when no credential is stored.
        var credential = _credentialStore.TryReadCredential();
        if (credential is null)
        {
            Log.Debug("ControlPlaneEntitlementRefresher: no credential stored; no-op (free tier or deactivated).");
            return false;
        }

        // Acquire within-process lock (S5 within-process single-shot).
        // Two concurrent in-process triggers compete here; the second will find
        // _lastAttemptUtc updated by the first and short-circuit.
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool proceedWithPost;
        try
        {
            now = _utcNow();

            // Double-checked locking: the first concurrent call may have updated
            // _lastAttemptUtc while we were waiting for the semaphore.
            if (IsWithinCooldown(now))
            {
                Log.Debug("ControlPlaneEntitlementRefresher: within throttle window (post-lock); skipping.");
                return false;
            }

            // Cross-process throttle (S5): best-effort, NOT an OS lock. Check the persisted
            // last-attempt time; if another process attempted within the interval, skip. A
            // TOCTOU race can still let two processes both POST (harmless: idempotent endpoint).
            var state = _credentialStore.TryReadRefreshState();
            if (state?.LastAttemptUtc is { } fileAttemptStr
                && DateTime.TryParse(fileAttemptStr, null, UtcParseStyles, out var fileAttempt)
                && (now - fileAttempt) < _throttleInterval)
            {
                // Another process made an attempt recently; sync our in-memory anchor so
                // the next fast-path check returns quickly, then skip.
                _lastAttemptUtc = fileAttempt;
                Log.Debug("ControlPlaneEntitlementRefresher: within cross-process throttle window; skipping.");
                return false;
            }

            // Record the attempt timestamp BEFORE posting (storm protection): even if
            // the POST fails, we will not retry within the throttle interval.
            _lastAttemptUtc = now;
            _credentialStore.TryWriteRefreshState(new EntitlementRefreshState
            {
                LastAttemptUtc = now.ToString("o"),
                ConsecutiveFailures = _consecutiveFailures,
            });

            proceedWithPost = true;
        }
        finally
        {
            _lock.Release();
        }

        if (!proceedWithPost)
            return false;

        // Outside the lock: make the network call.
        var request = new EntitlementValidateRequest
        {
            TokenVersion = 1,
            ClientVersion = GetClientVersion(),
            Credential = credential,
        };

        EntitlementValidateOutcome outcome;
        try
        {
            outcome = await _transport
                .PostValidateAsync(_baseUrl, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning("ControlPlaneEntitlementRefresher: transport threw unexpectedly: {Message}", ex.Message);
            outcome = new EntitlementValidateOutcome(EntitlementValidateStatus.NetworkFailure);
        }

        return HandleOutcome(outcome);
    }

    // -------------------------------------------------------------------------
    // Response handling
    // -------------------------------------------------------------------------

    private bool HandleOutcome(EntitlementValidateOutcome outcome)
    {
        var now = _utcNow();

        switch (outcome.Status)
        {
            case EntitlementValidateStatus.Ok:
            {
                // envelope must be present (transport already validated this, but belt-and-suspenders)
                if (outcome.Envelope is not { Length: > 0 } envelope)
                {
                    Log.Warning("ControlPlaneEntitlementRefresher: 200 OK but envelope is empty; ignoring.");
                    RecordFailure(now);
                    return false;
                }

                // Verify the envelope before persisting (A2: forged tokens must NEVER be written).
                var result = _verifier.Verify(envelope);
                if (!result.IsValid)
                {
                    Log.Warning(
                        "ControlPlaneEntitlementRefresher: 200 OK but envelope failed verification ({Reason}); " +
                        "last-known-good token retained.",
                        result.FailureReason);
                    RecordFailure(now);
                    return false;
                }

                // A2 compliant: only write after successful signature verification.
                // The resolver does NOT read FetchedAtUtc for grace (C-cite); grace is always
                // anchored on expires_at_utc from the signed token. A revoked-seat token carries
                // tier=free + limits=FreeTierDefaults; the resolver resolves to Free on next
                // GetState() call without any special handling here.
                bool wrote = _cachePath is not null
                    ? EntitlementCache.TryWrite(_cachePath, envelope, now)
                    : EntitlementCache.TryWrite(envelope, now);

                if (!wrote)
                {
                    Log.Warning("ControlPlaneEntitlementRefresher: cache write failed; last-known-good retained.");
                    RecordFailure(now);
                    return false;
                }

                // Success: reset consecutive-failure counter.
                _consecutiveFailures = 0;
                _credentialStore.TryWriteRefreshState(new EntitlementRefreshState
                {
                    LastAttemptUtc = now.ToString("o"),
                    ConsecutiveFailures = 0,
                });
                Log.Information(
                    "ControlPlaneEntitlementRefresher: token refreshed successfully (tier={Tier}).",
                    result.Token!.Tier);
                return true;
            }

            case EntitlementValidateStatus.BadCredential:
                // 401: the credential is rejected by the server. Keep the existing token governing
                // (it will degrade to Free on its own after expiry+grace). Do NOT overwrite the cache.
                // Surface a re-activate hint.
                Log.Warning(
                    "ControlPlaneEntitlementRefresher: 401 bad/expired credential — " +
                    "existing token governs until grace elapses. Run 'scope activate <token>' to re-activate.");
                RecordFailure(now);
                return false;

            case EntitlementValidateStatus.NetworkFailure:
            default:
                // Network failure / 5xx / timeout: do NOT clear or overwrite the cache (A2).
                // Apply jittered backoff on consecutive failures.
                RecordFailure(now);
                Log.Debug(
                    "ControlPlaneEntitlementRefresher: network failure (consecutive={Count}); " +
                    "existing token governs.",
                    _consecutiveFailures);
                return false;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private bool IsWithinCooldown(DateTime now)
    {
        var effectiveCooldown = _consecutiveFailures > 0
            ? ComputeBackoffCooldown(_consecutiveFailures)
            : _throttleInterval;
        // Use <= so that when two concurrent in-process tasks race at the same clock tick
        // (elapsed = 0, throttleInterval = 0 in tests) the second task correctly sees
        // "already attempted this instant" and skips. In production (4h interval) the difference
        // between < and <= is negligible; neither fires at exactly the interval boundary.
        return (now - _lastAttemptUtc) <= effectiveCooldown;
    }

    private TimeSpan ComputeBackoffCooldown(int failures)
    {
        // Exponential backoff: interval * 2^failures, capped at 24 hours.
        // ±20% jitter to spread retry storms across a fleet.
        var multiplier = Math.Pow(2.0, Math.Min(failures, 8)); // cap at 2^8 = 256x
        var raw = TimeSpan.FromTicks((long)(_throttleInterval.Ticks * multiplier));
        var capped = raw > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : raw;

        // Jitter: uniform in [0.8, 1.2) of the backoff value.
        double jitterFactor;
        lock (_jitter) { jitterFactor = 0.8 + _jitter.NextDouble() * 0.4; }
        return TimeSpan.FromTicks((long)(capped.Ticks * jitterFactor));
    }

    private void RecordFailure(DateTime now)
    {
        _consecutiveFailures++;
        _credentialStore.TryWriteRefreshState(new EntitlementRefreshState
        {
            LastAttemptUtc = now.ToString("o"),
            ConsecutiveFailures = _consecutiveFailures,
        });
    }

    private static string GetClientVersion()
    {
        // Use the assembly informational version when available; fall back to "unknown".
        var asm = typeof(ControlPlaneEntitlementRefresher).Assembly;
        return asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is System.Reflection.AssemblyInformationalVersionAttribute[] { Length: > 0 } attrs
            ? attrs[0].InformationalVersion
            : "unknown";
    }
}

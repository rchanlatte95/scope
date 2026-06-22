namespace Scope.Core.Telemetry;

using System;
using Scope.Core.Utilities;

/// <summary>
/// Honest classification of how a session key was obtained. Surfaced for A5 transparency
/// (no fabricated parents/sessions): a key is either Scope-owned (tier 1), supplied
/// best-effort by the host (tier 2), or genuinely absent and bucketed per UTC day (tier 3).
/// </summary>
public enum SessionOrigin
{
    /// <summary>Tier 1: SCOPE_SESSION_KEY, set by a launcher Scope controls. Correctness rides only on this.</summary>
    ScopeLaunched,

    /// <summary>Tier 2: a host-provided candidate (hook session_id / CLAUDE_CODE_SESSION_ID). Best-effort, never relied on for correctness.</summary>
    HostProvided,

    /// <summary>Tier 3: no real session; coalesced into a per-day "unattributed-{yyyy-MM-dd}" bucket.</summary>
    Unattributed,
}

/// <summary>
/// Result of resolving a session key: the key itself plus its honest <see cref="SessionOrigin"/>.
/// </summary>
public readonly record struct SessionKeyResolution(string Key, SessionOrigin Origin);

/// <summary>
/// The single shared, AOT-safe, three-tier session-key resolver applied by every capture channel
/// (the PostToolUse hook via <c>ScopeApp.RecordToolCall</c> and the MCP self-recording path via
/// <c>McpObservability.GetSessionKey</c>). It NEVER manufactures a per-process (<c>{prefix}-{pid}</c>)
/// session key. The ladder is, in order:
/// <list type="number">
///   <item>SCOPE_SESSION_KEY env var, if inhabited -> (<see cref="SessionOrigin.ScopeLaunched"/>).</item>
///   <item>else the caller-supplied host candidate, if inhabited -> (<see cref="SessionOrigin.HostProvided"/>).</item>
///   <item>else "unattributed-{yyyy-MM-dd}" (UTC, per-day bucket) -> (<see cref="SessionOrigin.Unattributed"/>).</item>
/// </list>
/// Pure: the environment getter and UTC clock are injected so the logic is unit-testable without
/// touching the real environment or <c>DateTime.UtcNow</c>.
/// </summary>
public static class SessionKeyResolver
{
    /// <summary>The env var that carries a Scope-owned (tier 1) session key.</summary>
    public const string ScopeSessionKeyEnv = "SCOPE_SESSION_KEY";

    /// <summary>Stable prefix for the per-day tier 3 unattributed bucket.</summary>
    public const string UnattributedPrefix = "unattributed-";

    /// <summary>
    /// RESERVED delimiter that separates a base session key from its generation ordinal in an effective
    /// key (<c>cli-{uuid7}#g{n}</c>, generation 0 SUFFIXLESS). Centralized here so the binding allocator
    /// (<see cref="ObservabilityWriter.ResolveEffectiveSessionKey"/>) and a guard test that no key minter
    /// emits this token both reference one source of truth. No minter may emit this token in a BASE key.
    /// </summary>
    public const string GenerationDelimiter = "#g";

    /// <summary>
    /// Resolves a session key and its origin using an injected environment getter and UTC clock.
    /// This is the pure core; production callers use the <see cref="Resolve(string?)"/> overload.
    /// </summary>
    /// <param name="hostCandidate">The best-effort host session id the caller has, or null.</param>
    /// <param name="getEnv">Reads an environment variable by name; returns null when unset.</param>
    /// <param name="utcNow">The current UTC time, used only for the per-day unattributed bucket.</param>
    public static SessionKeyResolution Resolve(
        string? hostCandidate,
        Func<string, string?> getEnv,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(getEnv);

        var scopeKey = getEnv(ScopeSessionKeyEnv);
        if (scopeKey.IsInhabited)
            return new SessionKeyResolution(scopeKey, SessionOrigin.ScopeLaunched);

        if (hostCandidate.IsInhabited)
            return new SessionKeyResolution(hostCandidate, SessionOrigin.HostProvided);

        var bucket = UnattributedPrefix + utcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return new SessionKeyResolution(bucket, SessionOrigin.Unattributed);
    }

    /// <summary>
    /// Production resolution against the real process environment and UTC clock.
    /// </summary>
    /// <param name="hostCandidate">The best-effort host session id the caller has, or null.</param>
    public static SessionKeyResolution Resolve(string? hostCandidate)
        => Resolve(hostCandidate, Environment.GetEnvironmentVariable, DateTime.UtcNow);

    /// <summary>Maps a <see cref="SessionOrigin"/> to its stable, persisted label.</summary>
    public static string ToLabel(this SessionOrigin origin) => origin switch
    {
        SessionOrigin.ScopeLaunched => "scope-launched",
        SessionOrigin.HostProvided => "host-provided",
        SessionOrigin.Unattributed => "unattributed",
        _ => "unattributed",
    };

    /// <summary>
    /// Analytically splits an EFFECTIVE session key into its durable BASE and GENERATION ordinal using
    /// the reserved <see cref="GenerationDelimiter"/>. A suffixless key (no delimiter, or an unparsable
    /// suffix) is generation 0 with the whole key as its base, so every legacy/cron/MCP/host-provided key
    /// round-trips unchanged. This is the inverse of the allocator's <c>base + "#g" + n</c> derivation.
    /// </summary>
    public static (string Base, int Generation) SplitEffectiveKey(string effectiveKey)
    {
        ArgumentNullException.ThrowIfNull(effectiveKey);
        var idx = effectiveKey.LastIndexOf(GenerationDelimiter, StringComparison.Ordinal);
        if (idx < 0)
            return (effectiveKey, 0);

        var suffix = effectiveKey.AsSpan(idx + GenerationDelimiter.Length);
        if (int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var gen) && gen > 0)
            return (effectiveKey[..idx], gen);

        // Not a generation suffix this allocator emitted: treat the whole key as a base (g0).
        return (effectiveKey, 0);
    }
}

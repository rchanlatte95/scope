namespace Scope.Core.Telemetry.Trace;

/// <summary>
/// Three-value cost BILLING-CLASS discriminator (T-2026-06-19-01 Phase 3b). Orthogonal to
/// <see cref="Scope.Core.Cron.Run.CostProvenance"/>: provenance is data-source QUALITY
/// (wire_exact / joined_exact / provider_estimated / scope_estimated / unmetered / unavailable);
/// billing-class is whether the dollars are an ACTUAL per-token charge. The discriminator is the
/// session AUTH MODE (known at launch), NOT the provenance. A scope_estimated cost on a cron
/// API-key run is <see cref="Billed"/> (real charge, just estimated); the same provenance on a
/// subscription interactive session is <see cref="ShadowWouldBe"/> (a would-be figure, not an
/// invoice).
/// <para>
/// Carried as a snake_case string on the wire (no enum, to avoid source-gen registration churn,
/// matching the <see cref="Scope.Core.Cron.Run.CostProvenance"/> convention). Existing/legacy rows
/// default to <see cref="Unclassified"/> so they show NO new badge and stay backward-compatible.
/// </para>
/// <para>
/// Derivation (conservative; when the signal is ambiguous, <see cref="Unclassified"/> applies; never
/// mislabel real spend as shadow or vice versa):
/// </para>
/// <list type="bullet">
///   <item>Cron session (session key starts with <c>cron-</c>) -> <see cref="Billed"/> (cron runs
///         with an API key; a real per-token charge).</item>
///   <item>Interactive session (<c>cli-</c>) whose cost includes WIRE-sourced spend (provenance
///         <c>wire_exact</c>/<c>joined_exact</c>, i.e. it went through the proxy with an API key) ->
///         <see cref="Billed"/>.</item>
///   <item>Interactive session (<c>cli-</c>) with cost from hook/estimate ONLY (no wire rows,
///         provenance <c>provider_estimated</c>/<c>scope_estimated</c>) -> <see cref="ShadowWouldBe"/>
///         (subscription; a would-be figure, not actually billed 1:1).</item>
///   <item>Anything that cannot be confidently classified — unknown session-key prefix, no cost
///         (<c>unmetered</c>/<c>unavailable</c>), or legacy data -> <see cref="Unclassified"/>.</item>
/// </list>
/// </summary>
public static class CostBillingClass
{
    /// <summary>An actual per-token charge to an API/Console account (cron API-key run, or an
    /// interactive session that went through the proxy with an API key -> wire-sourced spend).</summary>
    public const string Billed = "billed";

    /// <summary>A would-be cost: what the tokens WOULD cost at API rates for a subscription
    /// interactive session, surfaced as reference, not an invoice (no API key -> no wire metering).</summary>
    public const string ShadowWouldBe = "shadow_would_be";

    /// <summary>The billing class cannot be confidently determined (unknown session-key prefix, no
    /// cost, or legacy data). The conservative default; never mislabels real spend.</summary>
    public const string Unclassified = "unclassified";

    /// <summary>Stable prefix of a cron session key (<c>cron-{uuid7}</c>).</summary>
    private const string CronSessionPrefix = "cron-";

    /// <summary>Stable prefix of an interactive session key (<c>cli-{uuid7}</c>).</summary>
    private const string InteractiveSessionPrefix = "cli-";

    /// <summary>
    /// Derives the billing class for a session from its session key (the auth-mode signal: cron keys
    /// run with an API key; <c>cli-</c> keys are interactive), whether the session's tokens are
    /// WIRE-sourced (<paramref name="tokenSource"/> == <c>"wire"</c> means it went through the metering
    /// proxy with an API key), and the resolved session-level
    /// <see cref="Scope.Core.Cron.Run.CostProvenance"/> (used to tell genuine cost from no-cost).
    /// <para>
    /// The wire-sourced signal — NOT the provenance label — is what distinguishes a metered (billed)
    /// interactive session from a subscription (shadow) one. Provenance reflects PRICING quality
    /// (a wire-metered session priced from the seed table is <c>provider_estimated</c>, not
    /// <c>wire_exact</c>), so keying billing-class on provenance alone would mislabel a genuinely
    /// metered interactive session as shadow. The presence of wire tokens is the auth-mode signal.
    /// </para>
    /// <para>
    /// Conservative by construction: any session key that is neither a cron nor an interactive key,
    /// and any interactive session with no cost (<c>unmetered</c>/<c>unavailable</c>), resolves to
    /// <see cref="Unclassified"/> rather than guessing. This keeps the default backward-compatible
    /// (no new badge on existing/legacy data) and never mislabels real spend as shadow or vice versa.
    /// </para>
    /// </summary>
    /// <param name="sessionKey">The session key (e.g. <c>cron-{uuid7}</c> or <c>cli-{uuid7}</c>).</param>
    /// <param name="costProvenance">The session-level six-value cost provenance string.</param>
    /// <param name="tokenSource">The aggregate token source (<c>wire</c>|<c>exact</c>|<c>unknown</c>);
    /// <c>wire</c> means the session was metered through the proxy with an API key.</param>
    /// <returns>One of <see cref="Billed"/>, <see cref="ShadowWouldBe"/>, or <see cref="Unclassified"/>.</returns>
    public static string Derive(string? sessionKey, string? costProvenance, string? tokenSource = null)
    {
        if (string.IsNullOrEmpty(sessionKey))
            return Unclassified;

        // Cron sessions run with an API key -> a real per-token charge, regardless of whether the
        // cost is wire-exact or estimated.
        if (sessionKey.StartsWith(CronSessionPrefix, StringComparison.Ordinal))
            return Billed;

        // Interactive sessions: the metered (proxy + API key) path produces WIRE-sourced tokens, which
        // IS a real charge (whether priced wire-exact or provider-estimated from the seed table). The
        // un-metered (subscription) path has no wire tokens — only hook/estimate cost — which is a
        // would-be figure. No cost at all -> we cannot classify it.
        if (sessionKey.StartsWith(InteractiveSessionPrefix, StringComparison.Ordinal))
        {
            // Auth-mode signal: wire tokens mean the session went through the proxy with a key.
            if (string.Equals(tokenSource, "wire", StringComparison.Ordinal))
                return Billed;

            // A wire/joined-exact provenance also implies wire metering even if the token-source label
            // was not threaded through (defensive; same conclusion).
            if (string.Equals(costProvenance, Scope.Core.Cron.Run.CostProvenance.WireExact, StringComparison.Ordinal)
                || string.Equals(costProvenance, Scope.Core.Cron.Run.CostProvenance.JoinedExact, StringComparison.Ordinal))
                return Billed;

            return costProvenance switch
            {
                // Estimate-only cost on a subscription interactive session -> would-be, not billed 1:1.
                Scope.Core.Cron.Run.CostProvenance.ProviderEstimated => ShadowWouldBe,
                Scope.Core.Cron.Run.CostProvenance.ScopeEstimated => ShadowWouldBe,
                // unmetered / unavailable / unknown: no dollars to classify -> conservative default.
                _ => Unclassified,
            };
        }

        // Unknown session-key prefix (legacy unattributed-{day} buckets, host-provided ids, etc.):
        // we cannot confidently determine the auth mode, so stay Unclassified.
        return Unclassified;
    }
}

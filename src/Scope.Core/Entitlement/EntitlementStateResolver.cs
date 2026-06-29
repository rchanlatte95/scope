namespace Scope.Core.Entitlement;

using Serilog;

/// <summary>
/// Pure, deterministic resolver from (verified token, now) to <see cref="EntitlementState"/>.
/// Encodes the grace-window and state-machine logic defined in the approved design.
///
/// State machine (all UTC comparisons):
///   no token                          -> Free  (free-tier limits, no network)
///   now &lt; expiry &amp;&amp; trial_ends_at set and now &lt; trial_ends_at -> Trial (paid limits)
///   now &lt; expiry                      -> Paid  (paid limits)
///   expiry &lt;= now &lt; expiry + 72h     -> Grace (paid limits, grace_remaining surfaced)
///   now >= expiry + 72h               -> Expired (free-tier limits, honest message)
///
/// Any exception inside Resolve returns Free (must-never-crash posture).
/// </summary>
public static class EntitlementStateResolver
{
    /// <summary>
    /// Grace window: 72 hours beyond token expiry. Paid features remain accessible during
    /// this window so a user on a plane does not lose access due to a missed refresh.
    /// </summary>
    public static readonly TimeSpan GraceWindow = TimeSpan.FromHours(72);

    /// <summary>
    /// UTC-strict timestamp parse styles. Token timestamps are UTC by contract; this assumes UTC
    /// for a naive (no Z/offset) value and adjusts an offset-bearing value to UTC, so the result is
    /// always a true UTC instant. Without AssumeUniversal a naive timestamp would be read as LOCAL
    /// and shifted by the host's tz offset, mis-dating expiry. The parsed result Kind is Utc.
    /// </summary>
    private const System.Globalization.DateTimeStyles UtcParseStyles =
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;

    /// <summary>
    /// Resolves entitlement state from a verified token and the current instant.
    /// No token (null) always resolves to Free.
    ///
    /// "now" is injected so tests can exercise every branch without wall-clock dependency.
    /// </summary>
    public static EntitlementState Resolve(EntitlementToken? token, DateTime now)
    {
        try
        {
            if (token is null)
                return EntitlementState.FreeState;

            if (!DateTime.TryParse(token.ExpiresAtUtc, null, UtcParseStyles, out var expiresAt))
            {
                Log.Debug("EntitlementStateResolver: could not parse expires_at_utc '{Value}'; resolving to Free", token.ExpiresAtUtc);
                return EntitlementState.FreeState;
            }

            // expiresAt is now a true UTC instant (see UtcParseStyles). All comparisons are UTC-vs-UTC.
            // graceEnd is computed LAZILY below (post-expiry branch only) so a bad GraceHours value
            // in the token can never affect the Paid / Trial path.

            // Deny-to-free guard for an absent/renamed `limits` claim. System.Text.Json source-gen
            // does NOT apply the record's init default when the JSON property is missing or null, so
            // a renamed/absent claim leaves token.Limits == null (see EntitlementTokenContractTests).
            // Any ambiguity resolves to LESS entitlement: a null Limits is treated as free-tier, never
            // as null-means-unlimited (which would silently GRANT paid caps) and never dereferenced raw
            // (which would NRE). Used wherever a paid sub-state would otherwise surface token.Limits.
            var effectivePaidLimits = token.Limits ?? EntitlementLimits.FreeTierDefaults;

            // Token is still within its validity window.
            if (now < expiresAt)
            {
                // Check for trial sub-state.
                if (token.TrialEndsAtUtc is { Length: > 0 } trialStr
                    && DateTime.TryParse(trialStr, null, UtcParseStyles, out var trialEnds))
                {
                    if (now < trialEnds)
                    {
                        return new EntitlementState
                        {
                            Tier = token.Tier,
                            Status = EntitlementStatus.Trial,
                            ExpiresAt = expiresAt,
                            TrialEndsAt = trialEnds,
                            GraceRemaining = null,
                            EffectiveLimits = effectivePaidLimits,
                        };
                    }
                }

                // Paid subscription (or trial period ended but token still valid).
                return new EntitlementState
                {
                    Tier = token.Tier,
                    Status = EntitlementStatus.Paid,
                    ExpiresAt = expiresAt,
                    TrialEndsAt = null,
                    GraceRemaining = null,
                    EffectiveLimits = effectivePaidLimits,
                };
            }

            // --- Post-expiry path: compute graceEnd lazily here so no bad GraceHours value ---
            // can ever reach the valid-window (Paid/Trial) return paths above.
            //
            // Per-token grace (B2 hardening): use GraceHours claim when present and positive;
            // clamp to [0, SaneMaxGraceHours]. Absent/negative/zero -> GraceWindow (72h default).
            const int SaneMaxGraceHours = 720; // 30 days max
            TimeSpan effectiveGrace;
            if (token.GraceHours is { } rawGrace && rawGrace > 0)
                effectiveGrace = TimeSpan.FromHours(Math.Min(rawGrace, SaneMaxGraceHours));
            else
                effectiveGrace = GraceWindow;
            var graceEnd = expiresAt + effectiveGrace;

            // Within grace window: expiry <= now < expiry+effectiveGrace.
            if (now < graceEnd)
            {
                return new EntitlementState
                {
                    Tier = token.Tier,
                    Status = EntitlementStatus.Grace,
                    ExpiresAt = expiresAt,
                    TrialEndsAt = null,
                    GraceRemaining = graceEnd - now,
                    EffectiveLimits = effectivePaidLimits, // paid limits still apply during grace
                };
            }

            // Grace window also elapsed: now >= expiry+effectiveGrace -> Expired -> free-tier limits.
            return new EntitlementState
            {
                Tier = token.Tier,
                Status = EntitlementStatus.Expired,
                ExpiresAt = expiresAt,
                TrialEndsAt = null,
                GraceRemaining = null,
                EffectiveLimits = EntitlementLimits.FreeTierDefaults,
            };
        }
        catch (Exception ex)
        {
            // Must-never-crash: log the problem, degrade to Free.
            Log.Warning("EntitlementStateResolver.Resolve threw unexpectedly ({Message}); resolving to Free", ex.Message);
            return EntitlementState.FreeState;
        }
    }
}

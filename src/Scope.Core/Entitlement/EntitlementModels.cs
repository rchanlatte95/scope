namespace Scope.Core.Entitlement;

using System.Text.Json.Serialization;

/// <summary>
/// Tier identifiers as stable string constants. These appear verbatim in the signed
/// entitlement token payload, so they must not be renamed without a token_version bump.
/// </summary>
public static class EntitlementTiers
{
    public const string Free = "free";
    public const string Team = "team";
    public const string Enterprise = "enterprise";
}

/// <summary>
/// Feature limits embedded in the entitlement token. A null int means "unlimited" for that
/// dimension. Free-tier defaults are the hard-coded fallback when no valid token is present.
/// </summary>
public sealed record EntitlementLimits
{
    /// <summary>
    /// Maximum number of simultaneously enabled cron agents. Null means unlimited (paid).
    /// Free default: 3.
    /// </summary>
    [JsonPropertyName("max_enabled_crons")]
    public int? MaxEnabledCrons { get; init; }

    /// <summary>
    /// Observability data retention in days. Null means unlimited (paid).
    /// Free default: 7.
    /// </summary>
    [JsonPropertyName("observability_retention_days")]
    public int? ObservabilityRetentionDays { get; init; }

    /// <summary>
    /// Maximum number of recent sessions surfaced in the dashboard. Null means unlimited (paid).
    /// Free default: 10.
    /// </summary>
    [JsonPropertyName("max_dashboard_sessions")]
    public int? MaxDashboardSessions { get; init; }

    /// <summary>
    /// Whether the user may export audit logs (scope audit export, --html).
    /// Free default: false.
    /// </summary>
    [JsonPropertyName("audit_export_allowed")]
    public bool AuditExportAllowed { get; init; }

    /// <summary>Hard-coded free-tier limit defaults applied when no valid token governs.</summary>
    public static readonly EntitlementLimits FreeTierDefaults = new()
    {
        MaxEnabledCrons = 3,
        ObservabilityRetentionDays = 7,
        MaxDashboardSessions = 10,
        AuditExportAllowed = false,
    };
}

/// <summary>
/// The deserialized entitlement token payload. This record is the signed payload JSON
/// embedded in the token envelope; every field maps 1:1 to control-plane output.
/// </summary>
public sealed record EntitlementToken
{
    /// <summary>Schema version for forward-compat; current value is 1.</summary>
    [JsonPropertyName("token_version")]
    public int TokenVersion { get; init; }

    /// <summary>Entitlement tier: "free", "team", or "enterprise".</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = EntitlementTiers.Free;

    /// <summary>Organisation identifier; null for free-tier tokens.</summary>
    [JsonPropertyName("org_id")]
    public string? OrgId { get; init; }

    /// <summary>Seat identifier; null for free-tier tokens.</summary>
    [JsonPropertyName("seat_id")]
    public string? SeatId { get; init; }

    /// <summary>Feature limits encoded in the token.</summary>
    [JsonPropertyName("limits")]
    public EntitlementLimits Limits { get; init; } = EntitlementLimits.FreeTierDefaults;

    /// <summary>ISO-8601 UTC timestamp when the token was issued.</summary>
    [JsonPropertyName("issued_at_utc")]
    public string IssuedAtUtc { get; init; } = "";

    /// <summary>ISO-8601 UTC timestamp when the token expires.</summary>
    [JsonPropertyName("expires_at_utc")]
    public string ExpiresAtUtc { get; init; } = "";

    /// <summary>
    /// ISO-8601 UTC timestamp when the trial period ends. Null when the token does not
    /// represent a trial (fully paid or free tier).
    /// </summary>
    [JsonPropertyName("trial_ends_at_utc")]
    public string? TrialEndsAtUtc { get; init; }
}

/// <summary>
/// Status values for the resolved entitlement state. The values are ordered from most
/// to least permissive for readability; ordering is not meaningful for business logic.
/// </summary>
public enum EntitlementStatus
{
    /// <summary>No valid token; free-tier limits apply.</summary>
    Free,

    /// <summary>Trial period active; paid limits apply until trial_ends_at_utc.</summary>
    Trial,

    /// <summary>Paid subscription active; token limits apply.</summary>
    Paid,

    /// <summary>Token expired; within 72-hour grace window; paid limits still apply.</summary>
    Grace,

    /// <summary>Token expired and grace window elapsed; free-tier limits apply.</summary>
    Expired,
}

/// <summary>
/// Resolved entitlement state: the output of <see cref="EntitlementStateResolver.Resolve"/>.
/// Carries the effective limits callers should gate against.
/// </summary>
public sealed record EntitlementState
{
    /// <summary>The token tier string, or "free" when no token governs.</summary>
    public string Tier { get; init; } = EntitlementTiers.Free;

    /// <summary>Current entitlement status.</summary>
    public EntitlementStatus Status { get; init; } = EntitlementStatus.Free;

    /// <summary>Token expiry; null when there is no token.</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Trial end date; null when not a trial or no token.</summary>
    public DateTime? TrialEndsAt { get; init; }

    /// <summary>
    /// Remaining time in the grace window. Non-null only when Status is Grace.
    /// Callers may display this to prompt re-activation.
    /// </summary>
    public TimeSpan? GraceRemaining { get; init; }

    /// <summary>
    /// Effective feature limits to gate against. Resolves to token limits when
    /// Status is Trial/Paid/Grace; resolves to free-tier defaults otherwise.
    /// </summary>
    public EntitlementLimits EffectiveLimits { get; init; } = EntitlementLimits.FreeTierDefaults;

    /// <summary>Convenience: returns the well-known Free state.</summary>
    public static EntitlementState FreeState { get; } = new();
}

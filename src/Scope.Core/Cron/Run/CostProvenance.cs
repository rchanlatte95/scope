namespace Scope.Core.Cron.Run;

/// <summary>
/// Six-value cost provenance vocabulary for cron runs and trace session/run-level cost summaries.
/// Used as a snake_case string field on the wire; no enum is defined to avoid source-gen
/// registration churn. Values are constant strings and documented here as the canonical reference.
///
/// Vocabulary (matches the FE-B cost-ui contract):
/// <list type="bullet">
///   <item><term>wire_exact</term> <description>Cost came directly from provider wire metering with no join step.</description></item>
///   <item><term>joined_exact</term> <description>A wire-exact cost was joined to this run through an attribution key (tool_use_id for MVP).</description></item>
///   <item><term>provider_estimated</term> <description>Provider-supplied estimate (e.g. a reported cost field, not a local computation).</description></item>
///   <item><term>scope_estimated</term> <description>Local Scope estimate computed from available token counts and pricing tables.</description></item>
///   <item><term>unmetered</term> <description>No wire calls existed to meter (legitimately nothing to attribute).</description></item>
///   <item><term>unavailable</term> <description>Telemetry or cost data is absent; provenance cannot be determined.</description></item>
/// </list>
/// </summary>
public static class CostProvenance
{
    /// <summary>Cost came directly from provider wire metering with no join step.</summary>
    public const string WireExact = "wire_exact";

    /// <summary>Wire-exact cost joined to the run via tool_use_id attribution.</summary>
    public const string JoinedExact = "joined_exact";

    /// <summary>Provider-supplied cost estimate (e.g. a provider cost field, not a local computation).</summary>
    public const string ProviderEstimated = "provider_estimated";

    /// <summary>Local Scope estimate computed from token counts and pricing tables.</summary>
    public const string ScopeEstimated = "scope_estimated";

    /// <summary>No wire calls existed to meter (legitimately nothing to attribute).</summary>
    public const string Unmetered = "unmetered";

    /// <summary>Telemetry or cost data is absent; provenance cannot be determined.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// Maps a trace-level Money.source string (4-value: exact|wire|estimate|unknown) to the
    /// session-level CostProvenance (6-value). Used when projecting per-primitive source up to
    /// a run/session summary. Money.source is not replaced; this mapping is additive only.
    /// </summary>
    public static string FromMoneySource(string? moneySource) => moneySource switch
    {
        "wire" => WireExact,
        "exact" => JoinedExact,
        "estimate" => ScopeEstimated,
        _ => Unavailable,
    };
}

namespace Scope.Cli.Proxy;

using System.Text.Json.Serialization;

/// <summary>
/// CLI-side view of the proxy <c>/healthz</c> payload. Mirrors the proxy's
/// <c>HealthzResponse</c> field names; the launcher reads only what it needs.
/// </summary>
internal sealed record HealthzReport(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("drift_count")] long DriftCount,
    [property: JsonPropertyName("recorded_count")] long RecordedCount);

/// <summary>Source-generated JSON context for proxy CLI DTOs (no reflection).</summary>
[JsonSerializable(typeof(HealthzReport))]
internal partial class ProxyCliJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

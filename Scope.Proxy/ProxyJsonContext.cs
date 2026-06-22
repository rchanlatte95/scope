namespace Scope.Proxy;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Healthz payload. Carries no secrets (never the token).</summary>
public record HealthzResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("drift_count")] long DriftCount,
    [property: JsonPropertyName("recorded_count")] long RecordedCount)
{
    /// <summary>Serializes via the source-generated context.</summary>
    public string ToJsonString() =>
        JsonSerializer.Serialize(this, ProxyJsonContext.Default.HealthzResponse);
}

/// <summary>Source-generated JSON context for proxy-local DTOs (no reflection).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthzResponse))]
[JsonSerializable(typeof(List<string>))]
public partial class ProxyJsonContext : JsonSerializerContext;

namespace Scope.Core.Telemetry.Trace;

using System.Text.Json;
using static Scope.Core.Telemetry.Trace.TraceProjection;

/// <summary>
/// Projects wire_calls rows (wire-metering ST-01, merged) into the scope-trace-v1 wire body.
/// The shape was re-validated field-for-field against the merged WireCallSchemaManager DDL at
/// ST-12 time (see ST-12-trace-api.xml wireCheck). The whole object is content-free: endpoint is
/// the provider route template (proxy token stripped at storage, NEVER 8), model is a provider id,
/// system_hash/first_user_hash are hex strings, tool_use_ids are provider-issued join keys.
/// When no rows exist the body reports available=false with an empty list (honest, never faked).
/// </summary>
public static class TraceWireProjection
{
    /// <summary>Builds the wire body from wire_calls rows. <paramref name="tableExists"/> drives the available flag.</summary>
    public static WireBody Build(bool tableExists, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var calls = new List<WireCallDto>(rows.Count);
        foreach (var r in rows)
        {
            var id = Str(r, "id") ?? "";
            calls.Add(new WireCallDto(
                Id: id,
                DisplayId: TraceIdCodec.DisplayId(id),
                StartedAt: Str(r, "started_at") ?? "",
                TtftMs: Long(r, "ttft_ms"),
                TotalMs: Long(r, "total_ms"),
                Endpoint: Str(r, "endpoint") ?? "",
                Status: Int(r, "status") ?? 0,
                Stream: (Long(r, "stream") ?? 0) != 0,
                Model: Str(r, "model"),
                MessageId: Str(r, "message_id"),
                StopReason: Str(r, "stop_reason"),
                Tokens: new TokenBlock(
                    Input: Long(r, "input_tokens"),
                    Output: Long(r, "output_tokens"),
                    CacheCreation: Long(r, "cache_creation_tokens"),
                    CacheRead: Long(r, "cache_read_tokens"),
                    Total: Sum(Long(r, "input_tokens"), Long(r, "output_tokens")),
                    Source: "wire"),
                ToolUseIds: ParseToolUseIds(Str(r, "tool_use_ids")),
                SystemHash: Str(r, "system_hash"),
                FirstUserHash: Str(r, "first_user_hash"),
                MessageCount: Int(r, "message_count"),
                RequestBytes: Long(r, "request_bytes"),
                ResponseBytes: Long(r, "response_bytes")));
        }

        // available reflects whether the wire-metering subsystem has produced data: the table must
        // exist AND carry rows. An existing-but-empty table is still "no wire data yet".
        return new WireBody(tableExists && calls.Count > 0, calls);
    }

    private static long? Sum(long? a, long? b)
    {
        if (a is null && b is null) return null;
        return (a ?? 0) + (b ?? 0);
    }

    private static List<string> ParseToolUseIds(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) result.Add(el.GetString()!);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

namespace Scope.Core.Telemetry.Trace;

using System.Text.Json;
using System.Text.Json.Serialization;

// =============================================================================
// System.Text.Json source-generation context for the scope-trace-v1 contract.
// No reflection serialization: every concrete envelope type the server emits is
// registered here. AOT-compatible (Scope.Core is IsAotCompatible).
// =============================================================================

// No DefaultIgnoreCondition: the contract declares nullable fields as required-and-present
// (e.g. machine_id/workspace_id/org_id, TokenBlock.total). Every field serializes, null included,
// so consumers see a stable, complete shape and coherence sums never trip over a dropped field.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
// local-full envelopes
[JsonSerializable(typeof(TraceEnvelope<SessionsListBody>))]
[JsonSerializable(typeof(TraceEnvelope<SessionsListBodyWithLimits>))]
[JsonSerializable(typeof(TraceEnvelope<SessionGraphBody>))]
[JsonSerializable(typeof(TraceEnvelope<TraceBody>))]
[JsonSerializable(typeof(TraceEnvelope<RollupBody>))]
[JsonSerializable(typeof(TraceEnvelope<TopActionsBody>))]
[JsonSerializable(typeof(TraceEnvelope<WireBody>))]
// ST-08: conversation + CLI-session rollup envelopes
[JsonSerializable(typeof(TraceEnvelope<ConversationRollupBody>))]
[JsonSerializable(typeof(TraceEnvelope<CliSessionRollupBody>))]
// packet envelopes
[JsonSerializable(typeof(TraceEnvelope<PacketSessionsListBody>))]
[JsonSerializable(typeof(TraceEnvelope<PacketSessionGraphBody>))]
[JsonSerializable(typeof(TraceEnvelope<PacketTraceBody>))]
// soft-delete / restore result (T-2026-06-15-16/ST-01). Registered here (not in
// ScopeJsonSerializerContext) so deleted_at:null is EMITTED, not dropped: this context has no
// DefaultIgnoreCondition, so the restore response keeps {session_key, deleted_at:null} per contract.
[JsonSerializable(typeof(TraceSessionDeleteResult))]
public sealed partial class TraceJsonContext : JsonSerializerContext;

/// <summary>Helpers to serialize trace envelopes through the source-gen context (no reflection).</summary>
public static class TraceJson
{
    /// <summary>Serializes a local-full sessions_list envelope.</summary>
    public static string Serialize(TraceEnvelope<SessionsListBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeSessionsListBody);

    /// <summary>Serializes a sessions_list envelope carrying the entitlement limits signal (T-33).</summary>
    public static string Serialize(TraceEnvelope<SessionsListBodyWithLimits> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeSessionsListBodyWithLimits);

    /// <summary>Serializes a packet sessions_list envelope.</summary>
    public static string Serialize(TraceEnvelope<PacketSessionsListBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopePacketSessionsListBody);

    /// <summary>Serializes a local-full session_graph envelope.</summary>
    public static string Serialize(TraceEnvelope<SessionGraphBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeSessionGraphBody);

    /// <summary>Serializes a packet session_graph envelope.</summary>
    public static string Serialize(TraceEnvelope<PacketSessionGraphBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopePacketSessionGraphBody);

    /// <summary>Serializes a local-full trace envelope.</summary>
    public static string Serialize(TraceEnvelope<TraceBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeTraceBody);

    /// <summary>Serializes a packet trace envelope.</summary>
    public static string Serialize(TraceEnvelope<PacketTraceBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopePacketTraceBody);

    /// <summary>Serializes a rollup envelope (content-free body shared by both tiers).</summary>
    public static string Serialize(TraceEnvelope<RollupBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeRollupBody);

    /// <summary>Serializes a top-actions envelope (content-free body shared by both tiers).</summary>
    public static string Serialize(TraceEnvelope<TopActionsBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeTopActionsBody);

    /// <summary>Serializes a wire envelope (content-free body shared by both tiers).</summary>
    public static string Serialize(TraceEnvelope<WireBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeWireBody);

    /// <summary>
    /// Serializes a soft-delete / restore result, keeping deleted_at present even when null
    /// (T-2026-06-15-16/ST-01) so the restore response is {session_key, deleted_at:null}.
    /// </summary>
    public static string Serialize(TraceSessionDeleteResult r)
        => JsonSerializer.Serialize(r, TraceJsonContext.Default.TraceSessionDeleteResult);

    /// <summary>Serializes a conversation rollup envelope (ST-08).</summary>
    public static string Serialize(TraceEnvelope<ConversationRollupBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeConversationRollupBody);

    /// <summary>Serializes a CLI-session rollup envelope (ST-08).</summary>
    public static string Serialize(TraceEnvelope<CliSessionRollupBody> e)
        => JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEnvelopeCliSessionRollupBody);
}

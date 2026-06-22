namespace Scope.Core.Telemetry.Ingestion;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;
using Serilog;

/// <summary>
/// Ingests Claude Code session-transcript turns (<see cref="TranscriptTurn"/>, produced by
/// <see cref="ClaudeCodeTranscriptReader"/>) into <c>wire_calls</c> rows tagged
/// <c>source='transcript'</c>, so the top-level interactive session's OWN turns (parse-prompt +
/// reply + each tool call) become an exact, ambient token source. A transcript assistant turn is
/// structurally a wire call (model + input/output/cache tokens + <c>message.id</c> + the
/// <c>tool_use</c> ids it emitted), so writing it as a <c>wire_calls</c> row reuses the ENTIRE
/// existing attribution/rollup pipeline (header pass routes by <c>cc_session_id</c> to the session
/// root span; the <c>tool_use_id</c> pass splits the turn onto its tool rows). No new cost math.
///
/// <para><b>Wire precedence + dedup (verified design, T-2026-06-22-01).</b> The dedup key is
/// <c>message.id</c>. A turn is inserted ONLY when no existing <c>wire_calls</c> row already carries
/// that <c>message_id</c>. This gives two guarantees with one check: (1) a REAL proxy row for the
/// same message always wins (the proxy carries exact wire bytes/latency; the transcript is the
/// fallback), and (2) re-reading the same transcript is idempotent (a prior transcript row blocks
/// re-insert). Belt-and-suspenders: the row <c>id</c> is the deterministic
/// <c>"transcript:" + message.id</c>, so even a concurrent double-ingest collides on the PRIMARY KEY
/// rather than double-inserting.</para>
///
/// <para><b>tool_use_ids parity.</b> The turn's tool_use ids are serialized in the EXACT same JSON
/// shape the wire proxy writes (a plain JSON array of strings, e.g. <c>["toolu_a","toolu_b"]</c>, or
/// <c>null</c> when the turn emitted no tools) so the existing <c>tool_use_id</c> attribution join
/// splits the turn onto its tool rows unchanged; an empty/text-only turn (null) routes the whole
/// turn to the session root span via the header pass.</para>
///
/// <para><b>Never throws.</b> This runs under the PostToolUse hook contract (capture must never crash
/// callers). Every failure path logs a Serilog warning and continues; a metering failure must never
/// become a hook failure. Uses source-generated JSON (no reflection) for AOT compatibility.</para>
/// </summary>
public static class TranscriptCostIngester
{
    /// <summary>The benign endpoint stamped on transcript rows (we have no real request line).</summary>
    private const string TranscriptEndpoint = "/v1/messages";

    /// <summary>The provenance tag distinguishing these rows from proxy-metered rows.</summary>
    private const string TranscriptSource = "transcript";

    /// <summary>
    /// Inserts the given transcript <paramref name="turns"/> as <c>source='transcript'</c>
    /// <c>wire_calls</c> rows in the telemetry db at <paramref name="telemetryDbPath"/>, honoring
    /// wire precedence + <c>message.id</c> dedup. Returns the count of rows actually inserted (turns
    /// skipped for a blank message id, an existing proxy/transcript row, or a dropped insert do not
    /// count). Never throws.
    /// </summary>
    /// <param name="turns">The transcript turns to ingest (typically one transcript file's turns).</param>
    /// <param name="telemetryDbPath">Absolute path to <c>.scope/telemetry.db</c>.</param>
    /// <returns>The number of <c>wire_calls</c> rows newly inserted.</returns>
    public static int IngestTurns(IReadOnlyList<TranscriptTurn> turns, string telemetryDbPath)
    {
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count == 0 || string.IsNullOrWhiteSpace(telemetryDbPath))
            return 0;

        try
        {
            // One short-lived connection drives the existence checks; TryInsert opens its own
            // connection per write (matching the proxy's contention model). Ensuring the wire schema
            // here means the existence-check SELECT cannot fail on a fresh db with no wire_calls table.
            using var connection = SQLite.OpenDb(telemetryDbPath);
            new WireCallSchemaManager(connection).EnsureSchema();

            var db = new WireCallDb(telemetryDbPath);
            var inserted = 0;

            foreach (var turn in turns)
            {
                if (turn is null || string.IsNullOrWhiteSpace(turn.MessageId))
                    continue; // no dedup/grouping key -> cannot attribute or dedup the turn

                // Wire precedence + idempotency: skip if ANY wire row already carries this message_id
                // (a real proxy row wins; a prior transcript row blocks re-insert).
                if (MessageIdExists(connection, turn.MessageId))
                    continue;

                if (db.TryInsert(ToRecord(turn)))
                    inserted++;
            }

            return inserted;
        }
        catch (Exception ex)
        {
            // Capture must never crash the hook: log and report zero ingested.
            Log.Warning("Transcript cost ingest failed for {Path}: {Message}", telemetryDbPath, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Maps a <see cref="TranscriptTurn"/> to a <see cref="WireCallRecord"/>: deterministic id
    /// (<c>"transcript:" + MessageId</c>), <c>source='transcript'</c>, token/model/cc_session_id and
    /// proxy-shaped tool_use_ids carried through; null/zero benign values for the wire-only fields we
    /// do not have (latency, bytes, hashes, machine/workspace/org, cc agent ids).
    /// </summary>
    private static WireCallRecord ToRecord(TranscriptTurn turn) =>
        new(
            Id: "transcript:" + turn.MessageId,
            StartedAt: string.IsNullOrEmpty(turn.StartedAt)
                ? DateTime.UtcNow.ToString("o")
                : turn.StartedAt,
            TtftMs: null,
            TotalMs: 0,
            Endpoint: TranscriptEndpoint,
            Status: 200,
            Stream: false,
            Model: turn.Model,
            MessageId: turn.MessageId,
            StopReason: null,
            InputTokens: turn.InputTokens,
            OutputTokens: turn.OutputTokens,
            CacheCreationTokens: turn.CacheCreationTokens,
            CacheReadTokens: turn.CacheReadTokens,
            ToolUseIdsJson: SerializeToolUseIds(turn.ToolUseIds),
            SystemHash: null,
            FirstUserHash: null,
            MessageCount: null,
            RequestBytes: 0,
            ResponseBytes: 0,
            MachineId: null,
            WorkspaceId: null,
            OrgId: null,
            CcSessionId: turn.CcSessionId,
            // null cc_agent_id routes the turn to the session ROOT span (the parent's own turns),
            // which is exactly correct: these are the interactive session's turns, not a subagent's.
            CcAgentId: null,
            CcParentAgentId: null,
            Source: TranscriptSource);

    /// <summary>
    /// Serializes the turn's tool_use ids in the EXACT JSON shape the wire proxy writes: a COMPACT
    /// (non-indented) plain JSON array of strings (<c>["toolu_a","toolu_b"]</c>) when present, or
    /// <c>null</c> when the turn emitted no tools (matching the proxy's "no tool_use" representation,
    /// which routes the whole turn to the root span via the header pass).
    /// <para>
    /// The proxy writes <c>JsonSerializer.Serialize(List&lt;string&gt;, ProxyJsonContext.Default.ListString)</c>
    /// whose context is NON-indented, so the on-disk value is compact. Scope.Core's shared
    /// <c>ScopeJsonSerializerContext</c> sets <c>WriteIndented = true</c> and would pretty-print, which
    /// would NOT match. We therefore write the array with a default (compact) <see cref="Utf8JsonWriter"/>,
    /// which produces the identical byte shape with correct JSON string escaping and no reflection
    /// (AOT-safe), so the existing tool_use_id attribution join parses it unchanged.
    /// </para>
    /// </summary>
    private static string? SerializeToolUseIds(IReadOnlyList<string> toolUseIds)
    {
        if (toolUseIds is null || toolUseIds.Count == 0)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var id in toolUseIds)
                writer.WriteStringValue(id);
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// True when ANY <c>wire_calls</c> row already carries <paramref name="messageId"/>. The wire
    /// schema is ensured by the caller before this runs, so the SELECT cannot fail on a missing table.
    /// </summary>
    private static bool MessageIdExists(SqliteConnection connection, string messageId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM wire_calls WHERE message_id = $messageId);";
        cmd.Parameters.AddWithValue("$messageId", messageId);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
}

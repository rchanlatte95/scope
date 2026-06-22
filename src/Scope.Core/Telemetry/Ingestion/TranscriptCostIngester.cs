namespace Scope.Core.Telemetry.Ingestion;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scope.Core.Telemetry;
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
/// <para><b>Single connection, transactional (T-2026-06-22-02 Piece B1).</b> One ingest run uses ONE
/// <see cref="SqliteConnection"/> for EVERYTHING: the one-time v3 schema migration, the
/// existence/dedup pre-load, and all INSERTs. This removes the prior same-process multi-connection
/// pattern (a reader connection plus a fresh <see cref="WireCallDb"/> connection per write) that
/// churned locks and could lose the v3 DDL under concurrent load. Inserts run inside
/// <c>BEGIN IMMEDIATE</c> transactions chunked to <see cref="ChunkSize"/> rows, so the write lock is
/// acquired UP FRONT (contention surfaces at BEGIN, not mid-transaction) and held only briefly per
/// chunk. The migration and each <c>BEGIN IMMEDIATE</c> are wrapped in <see cref="SqliteRetry"/> so a
/// transient BUSY/LOCKED retries with jittered backoff instead of being silently dropped. WAL
/// (applied in <see cref="SQLite.OpenDb(string)"/>) keeps concurrent readers unaffected; the chunking
/// is a good-citizen bound on write-lock hold time for other writers.</para>
///
/// <para><b>Wire precedence + dedup (verified design, T-2026-06-22-01).</b> The dedup key is
/// <c>message.id</c>. A turn is inserted ONLY when no existing <c>wire_calls</c> row already carries
/// that <c>message_id</c>. This gives two guarantees with one check: (1) a REAL proxy row for the
/// same message always wins (the proxy carries exact wire bytes/latency; the transcript is the
/// fallback), and (2) re-reading the same transcript is idempotent (a prior transcript row blocks
/// re-insert). The dedup set is pre-loaded ON THE SAME CONNECTION in one query before the inserts.
/// Belt-and-suspenders: the row <c>id</c> is the deterministic <c>"transcript:" + message.id</c>, so
/// even a concurrent double-ingest collides on the PRIMARY KEY rather than double-inserting; we use
/// <c>INSERT OR IGNORE</c> so such a PK collision is a benign no-op, not a transaction abort.</para>
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
    /// Rows per <c>BEGIN IMMEDIATE</c> transaction. Chunking bounds how long the write lock is held on
    /// a very large transcript: WAL keeps readers unaffected, but other WRITERS still serialize behind
    /// the immediate lock, so we release and re-acquire every <c>ChunkSize</c> rows to stay a good
    /// citizen. Each chunk commits independently, so a failure mid-run keeps already-committed chunks.
    /// </summary>
    private const int ChunkSize = 100;

    /// <summary>
    /// The shared INSERT column list (identical to <see cref="WireCallDb"/>'s, kept in lockstep). We
    /// inline the INSERT on the single ingest connection rather than calling
    /// <see cref="WireCallDb.TryInsert"/> (which opens its OWN connection per write -- the pattern this
    /// piece removes for the ingest path). <c>INSERT OR IGNORE</c> makes a deterministic-id PK
    /// collision (concurrent double-ingest) a benign no-op rather than a transaction abort.
    /// </summary>
    private const string InsertSql = """
        INSERT OR IGNORE INTO wire_calls (
            id, started_at, ttft_ms, total_ms, endpoint, status, stream,
            model, message_id, stop_reason,
            input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens,
            tool_use_ids, system_hash, first_user_hash, message_count,
            request_bytes, response_bytes, machine_id, workspace_id, org_id,
            cc_session_id, cc_agent_id, cc_parent_agent_id, source)
        VALUES (
            $id, $startedAt, $ttftMs, $totalMs, $endpoint, $status, $stream,
            $model, $messageId, $stopReason,
            $inputTokens, $outputTokens, $cacheCreationTokens, $cacheReadTokens,
            $toolUseIds, $systemHash, $firstUserHash, $messageCount,
            $requestBytes, $responseBytes, $machineId, $workspaceId, $orgId,
            $ccSessionId, $ccAgentId, $ccParentAgentId, $source);
        """;

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
            // ONE connection drives the migration, the dedup pre-load, and all INSERTs. WAL +
            // busy_timeout are applied by OpenDb; SqliteRetry below absorbs residual writer-vs-writer
            // serialization on the migration DDL and the BEGIN IMMEDIATE lock acquisition.
            using var connection = SQLite.OpenDb(telemetryDbPath);

            // One-time v3 migration. Retry the DDL so a transient BUSY/LOCKED during the ALTER retries
            // (with jittered backoff) instead of being lost; once the db is at v3 this is a single
            // cheap MAX(version) probe with no DDL.
            SqliteRetry.Execute(() => new WireCallSchemaManager(connection).EnsureSchema());

            // Wire precedence + idempotency pre-load: every message_id already in wire_calls (proxy OR
            // a prior transcript row). Done on the SAME connection in ONE query so there is no second
            // connection contending with our own writer.
            var present = LoadExistingMessageIds(connection);

            // Filter to the turns we will actually write, deduping WITHIN this batch too (two turns in
            // one file carrying the same message_id collapse to one insert).
            List<WireCallRecord>? records = null;
            foreach (var turn in turns)
            {
                if (turn is null || string.IsNullOrWhiteSpace(turn.MessageId))
                    continue; // no dedup/grouping key -> cannot attribute or dedup the turn
                if (!present.Add(turn.MessageId))
                    continue; // already in the db, or a duplicate earlier in this same batch
                (records ??= []).Add(ToRecord(turn));
            }

            if (records is null || records.Count == 0)
                return 0;

            return InsertInChunks(connection, records);
        }
        catch (Exception ex)
        {
            // Capture must never crash the hook: log and report zero ingested.
            Log.Warning("Transcript cost ingest failed for {Path}: {Message}", telemetryDbPath, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Writes <paramref name="records"/> in <see cref="ChunkSize"/>-row <c>BEGIN IMMEDIATE</c>
    /// transactions on the shared <paramref name="connection"/>, returning the total inserted. The
    /// immediate lock is acquired up front per chunk (contention surfaces at BEGIN, retried by
    /// <see cref="SqliteRetry"/>; no rows written yet so retrying BEGIN is safe), then every INSERT in
    /// the chunk runs under the held lock and cannot hit BUSY from another writer. A row that
    /// <c>INSERT OR IGNORE</c>s away (PK collision from a concurrent double-ingest) does not count.
    /// </summary>
    private static int InsertInChunks(SqliteConnection connection, List<WireCallRecord> records)
    {
        var inserted = 0;
        for (var offset = 0; offset < records.Count; offset += ChunkSize)
        {
            var end = Math.Min(offset + ChunkSize, records.Count);

            // Acquire the write lock UP FRONT. Retrying BEGIN IMMEDIATE is safe: no rows are written
            // until the lock is held, so a transient BUSY here just re-attempts the acquisition.
            SqliteRetry.Execute(() => ExecuteNonQuery(connection, "BEGIN IMMEDIATE;"));
            try
            {
                for (var i = offset; i < end; i++)
                    inserted += InsertOne(connection, records[i]);

                ExecuteNonQuery(connection, "COMMIT;");
            }
            catch
            {
                // Roll back this chunk only; already-committed chunks survive. Best-effort rollback:
                // if it fails the connection is being torn down anyway (outer catch logs + returns 0
                // for the run is not possible here since we rethrow, so swallow rollback failure).
                TryRollback(connection);
                throw;
            }
        }

        return inserted;
    }

    /// <summary>Inserts one record on the open connection; returns 1 when a row was written, 0 when ignored.</summary>
    private static int InsertOne(SqliteConnection connection, WireCallRecord record)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = InsertSql;
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$startedAt", record.StartedAt);
        cmd.Parameters.AddWithValue("$ttftMs", (object?)record.TtftMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$totalMs", record.TotalMs);
        cmd.Parameters.AddWithValue("$endpoint", record.Endpoint);
        cmd.Parameters.AddWithValue("$status", record.Status);
        cmd.Parameters.AddWithValue("$stream", record.Stream ? 1 : 0);
        cmd.Parameters.AddWithValue("$model", (object?)record.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$messageId", (object?)record.MessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stopReason", (object?)record.StopReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inputTokens", (object?)record.InputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outputTokens", (object?)record.OutputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cacheCreationTokens", (object?)record.CacheCreationTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cacheReadTokens", (object?)record.CacheReadTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$toolUseIds", (object?)record.ToolUseIdsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$systemHash", (object?)record.SystemHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$firstUserHash", (object?)record.FirstUserHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$messageCount", (object?)record.MessageCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$requestBytes", record.RequestBytes);
        cmd.Parameters.AddWithValue("$responseBytes", record.ResponseBytes);
        cmd.Parameters.AddWithValue("$machineId", (object?)record.MachineId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$workspaceId", (object?)record.WorkspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$orgId", (object?)record.OrgId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ccSessionId", (object?)record.CcSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ccAgentId", (object?)record.CcAgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ccParentAgentId", (object?)record.CcParentAgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", record.Source);
        // ExecuteNonQuery returns the affected row count: 1 on insert, 0 when OR IGNORE skipped a PK clash.
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads every non-null <c>message_id</c> currently in <c>wire_calls</c> into an ordinal set, the
    /// dedup/wire-precedence backstop. Done on the SAME connection as the writes (no second connection)
    /// in one query. The wire schema is ensured by the caller before this runs, so the SELECT cannot
    /// fail on a missing table.
    /// </summary>
    private static HashSet<string> LoadExistingMessageIds(SqliteConnection connection)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT message_id FROM wire_calls WHERE message_id IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                present.Add(reader.GetString(0));
        }

        return present;
    }

    private static int ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            ExecuteNonQuery(connection, "ROLLBACK;");
        }
        catch
        {
            // No active transaction (or the connection is dying): nothing to undo.
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
}

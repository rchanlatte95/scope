namespace Scope.Core.Telemetry;

using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;
using System.Globalization;

/// <summary>
/// Creates the wire_calls table in .scope/telemetry.db for the wire-metering proxy.
/// Owns its own additive schema chain (wire_schema_versions), separate from the
/// obs_* chain managed by <see cref="ObservabilitySchemaManager"/>. The migration is
/// version-gated: once a db is at <see cref="LatestVersion"/>, EnsureSchema does a single
/// MAX(version) probe and returns, so it stays near-free and exception-free on the read
/// hot path (it is invoked on every wire read via ObservabilityAuditDb.HasWireCallsTable).
/// Column presence is checked with PRAGMA table_info rather than catching a thrown
/// "duplicate column" SqliteException, so no exception is used as steady-state control flow.
/// </summary>
public class WireCallSchemaManager(SqliteConnection connection)
{
    /// <summary>The newest wire schema version this manager knows how to produce.</summary>
    private const int LatestVersion = 4;

    /// <summary>
    /// Ensures the wire metering tables and indexes exist and the schema is at <see cref="LatestVersion"/>.
    /// Safe and cheap to call repeatedly: once migrated, it does one MAX(version) read and returns
    /// without running any DDL or throwing.
    /// </summary>
    public void EnsureSchema()
    {
        EnsureVersionTable();

        // Version-gate the migration. This runs on every wire READ (HasWireCallsTable funnels the
        // read paths through EnsureSchema to close the lazy-migration reader gap), so the steady
        // state must be a single cheap probe with no DDL and no thrown/caught exceptions.
        if (CurrentVersion() >= LatestVersion)
            return;

        CreateBaseSchema();
        InsertSchemaVersion(1, "Wire metering: wire_calls L0 usage/latency rows");

        MigrateToV2();
        MigrateToV3();
        MigrateToV4();
    }

    private void EnsureVersionTable()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wire_schema_versions (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT NOT NULL,
                description TEXT NOT NULL
            ) STRICT;
            """;
        cmd.ExecuteNonQueryRetrying();
    }

    private int CurrentVersion()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM wire_schema_versions;";
        return Convert.ToInt32(cmd.ExecuteScalarRetrying(), CultureInfo.InvariantCulture);
    }

    private void CreateBaseSchema()
    {
        // Fresh dbs get the cc_* columns directly from this CREATE; existing v1 dbs (created before
        // cc_* was added here) are upgraded by MigrateToV2's PRAGMA-guarded ALTER. CREATE ... IF NOT
        // EXISTS leaves an existing wire_calls untouched, so this is safe on a live v1 db.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wire_calls (
                id                    TEXT PRIMARY KEY,
                started_at            TEXT NOT NULL,
                ttft_ms               INTEGER,
                total_ms              INTEGER NOT NULL,
                endpoint              TEXT NOT NULL,
                status                INTEGER NOT NULL,
                stream                INTEGER NOT NULL DEFAULT 0,
                model                 TEXT,
                message_id            TEXT,
                stop_reason           TEXT,
                input_tokens          INTEGER,
                output_tokens         INTEGER,
                cache_creation_tokens INTEGER,
                cache_read_tokens     INTEGER,
                tool_use_ids          TEXT,
                system_hash           TEXT,
                first_user_hash       TEXT,
                message_count         INTEGER,
                request_bytes         INTEGER NOT NULL DEFAULT 0,
                response_bytes        INTEGER NOT NULL DEFAULT 0,
                machine_id            TEXT,
                workspace_id          TEXT,
                org_id                TEXT,
                cc_session_id         TEXT,
                cc_agent_id           TEXT,
                cc_parent_agent_id    TEXT,
                source                TEXT NOT NULL DEFAULT 'proxy'
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_wire_started  ON wire_calls(started_at);
            CREATE INDEX IF NOT EXISTS idx_wire_model    ON wire_calls(model);
            CREATE INDEX IF NOT EXISTS idx_wire_message  ON wire_calls(message_id);
            """;
        cmd.ExecuteNonQueryRetrying();
    }

    private void MigrateToV2()
    {
        // Per-session / per-subagent attribution columns (T-2026-06-19-01, Phase 1). Claude Code
        // sends three ephemeral, content-free per-spawn identifier headers on every /v1/messages
        // request; the proxy already forwards them upstream byte-identically and now also RECORDS
        // them so wire rows can later be attributed to an interactive session or a spawned subagent:
        //   - cc_session_id        from X-Claude-Code-Session-Id
        //   - cc_agent_id          from X-Claude-Code-Agent-Id
        //   - cc_parent_agent_id   from X-Claude-Code-Parent-Agent-Id
        // Forward-only, nullable, no backfill: legacy rows and non-CC callers stay NULL. This phase
        // only records; it changes NO rollup math. ALTER TABLE lacks IF NOT EXISTS before SQLite
        // 3.35, so each column is gated on a PRAGMA table_info check (not a thrown duplicate-column
        // exception). Per-column checks also tolerate a partially-migrated db.
        AddColumnIfMissing("cc_session_id");
        AddColumnIfMissing("cc_agent_id");
        AddColumnIfMissing("cc_parent_agent_id");

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_wire_ccsession ON wire_calls(cc_session_id);
            CREATE INDEX IF NOT EXISTS idx_wire_ccagent   ON wire_calls(cc_agent_id);
            """;
        idxCmd.ExecuteNonQueryRetrying();

        InsertSchemaVersion(2, "CC attribution: cc_session_id, cc_agent_id, cc_parent_agent_id on wire_calls");
    }

    private void MigrateToV3()
    {
        // Row provenance (T-2026-06-22-01, Piece 2). 'source' distinguishes proxy-metered rows from
        // transcript-derived rows: the wire proxy writes 'proxy' (the DEFAULT), a later piece writes
        // 'transcript' for turns reconstructed from the Claude Code session transcript. Honest
        // provenance/auditing only -- it changes NO rollup math. NOT NULL DEFAULT 'proxy' means
        // existing v2 rows and the proxy producer (which does not set Source) both read 'proxy'.
        // ALTER TABLE ... ADD COLUMN with a constant DEFAULT backfills existing rows to that constant,
        // so legacy rows become 'proxy' with no separate UPDATE. Guarded by a PRAGMA table_info check
        // (not a thrown duplicate-column exception) so it is safe to re-run on a partially-migrated db.
        AddColumnIfMissing("source");

        InsertSchemaVersion(3, "Row provenance: source ('proxy' | 'transcript') on wire_calls");
    }

    /// <summary>
    /// STRICT conversion (T-2026-07-12-01, hard rule owner 2026-07-12): recreates <c>wire_calls</c> and
    /// <c>wire_schema_versions</c> as STRICT. <c>wire_calls</c> carries immutable financial rows (exact
    /// wire cost, tokens, provenance), so a pre-existing NULL-PK row (legal only in the old non-STRICT
    /// shape, since a bare TEXT PRIMARY KEY does not imply NOT NULL) is a fail-loud condition: never
    /// silently dropped, see <see cref="NullPkPolicy.Throw"/>. <c>wire_schema_versions</c> has no
    /// NULL-PK risk (INTEGER PRIMARY KEY rowid alias) and is copied straight.
    /// <para>
    /// Retry posture: <see cref="StrictTableMigrator.Recreate"/> runs bare (non-retrying) statements
    /// inside one BEGIN/COMMIT transaction; the WHOLE transaction is wrapped as a single
    /// <see cref="SqliteRetry"/> unit below (not the individual statements) because a BUSY/LOCKED
    /// failure rolls the transaction back first, so retrying the entire sequence from scratch cannot
    /// double-apply. <see cref="StrictTableMigrator.IsStrict"/> makes this a single cheap probe on
    /// every call once migrated (this method still runs unconditionally as part of the version-gated
    /// EnsureSchema chain, but does no DDL once both tables are STRICT).
    /// </para>
    /// </summary>
    private void MigrateToV4()
    {
        using (var offCmd = connection.CreateCommand())
        {
            offCmd.CommandText = "PRAGMA foreign_keys = OFF;";
            offCmd.ExecuteNonQueryRetrying();
        }

        try
        {
            SqliteRetry.Execute(() =>
            {
                using var beginCmd = connection.CreateCommand();
                beginCmd.CommandText = "BEGIN TRANSACTION;";
                beginCmd.ExecuteNonQuery();

                try
                {
                    StrictTableMigrator.Recreate(
                        connection,
                        "wire_schema_versions",
                        """
                        CREATE TABLE wire_schema_versions_new (
                            version     INTEGER PRIMARY KEY,
                            applied_at  TEXT NOT NULL,
                            description TEXT NOT NULL
                        ) STRICT;
                        """,
                        "version, applied_at, description",
                        pkColumn: null,
                        NullPkPolicy.Throw);

                    StrictTableMigrator.Recreate(
                        connection,
                        "wire_calls",
                        """
                        CREATE TABLE wire_calls_new (
                            id                    TEXT PRIMARY KEY,
                            started_at            TEXT NOT NULL,
                            ttft_ms               INTEGER,
                            total_ms              INTEGER NOT NULL,
                            endpoint              TEXT NOT NULL,
                            status                INTEGER NOT NULL,
                            stream                INTEGER NOT NULL DEFAULT 0,
                            model                 TEXT,
                            message_id            TEXT,
                            stop_reason           TEXT,
                            input_tokens          INTEGER,
                            output_tokens         INTEGER,
                            cache_creation_tokens INTEGER,
                            cache_read_tokens     INTEGER,
                            tool_use_ids          TEXT,
                            system_hash           TEXT,
                            first_user_hash       TEXT,
                            message_count         INTEGER,
                            request_bytes         INTEGER NOT NULL DEFAULT 0,
                            response_bytes        INTEGER NOT NULL DEFAULT 0,
                            machine_id            TEXT,
                            workspace_id          TEXT,
                            org_id                TEXT,
                            cc_session_id         TEXT,
                            cc_agent_id           TEXT,
                            cc_parent_agent_id    TEXT,
                            source                TEXT NOT NULL DEFAULT 'proxy'
                        ) STRICT;
                        """,
                        """
                        id, started_at, ttft_ms, total_ms, endpoint, status, stream, model, message_id,
                        stop_reason, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens,
                        tool_use_ids, system_hash, first_user_hash, message_count, request_bytes,
                        response_bytes, machine_id, workspace_id, org_id, cc_session_id, cc_agent_id,
                        cc_parent_agent_id, source
                        """,
                        pkColumn: "id",
                        NullPkPolicy.Throw);

                    using var commitCmd = connection.CreateCommand();
                    commitCmd.CommandText = "COMMIT;";
                    commitCmd.ExecuteNonQuery();
                }
                catch
                {
                    using var rollbackCmd = connection.CreateCommand();
                    rollbackCmd.CommandText = "ROLLBACK;";
                    rollbackCmd.ExecuteNonQuery();
                    throw;
                }
            });
        }
        finally
        {
            using var onCmd = connection.CreateCommand();
            onCmd.CommandText = "PRAGMA foreign_keys = ON;";
            onCmd.ExecuteNonQueryRetrying();
        }

        InsertSchemaVersion(4, "STRICT conversion: wire_calls + wire_schema_versions recreated STRICT (T-2026-07-12-01)");
    }

    private void AddColumnIfMissing(string column)
    {
        if (ColumnExists(column))
            return;

        // Column name comes only from this class's own string literals (never user input), so the
        // fixed per-column DDL below carries no injection surface.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = column switch
        {
            "cc_session_id" => "ALTER TABLE wire_calls ADD COLUMN cc_session_id TEXT;",
            "cc_agent_id" => "ALTER TABLE wire_calls ADD COLUMN cc_agent_id TEXT;",
            "cc_parent_agent_id" => "ALTER TABLE wire_calls ADD COLUMN cc_parent_agent_id TEXT;",
            "source" => "ALTER TABLE wire_calls ADD COLUMN source TEXT NOT NULL DEFAULT 'proxy';",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown wire_calls column."),
        };
        cmd.ExecuteNonQueryRetrying();
    }

    private bool ColumnExists(string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('wire_calls') WHERE name = $col;";
        cmd.Parameters.AddWithValue("$col", column);
        return Convert.ToInt32(cmd.ExecuteScalarRetrying(), CultureInfo.InvariantCulture) > 0;
    }

    private void InsertSchemaVersion(int version, string description)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO wire_schema_versions (version, applied_at, description)
            VALUES ($version, $appliedAt, $description);
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$description", description);
        cmd.ExecuteNonQueryRetrying();
    }
}

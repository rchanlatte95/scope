using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;

namespace Scope.Core.Cron;

/// <summary>
/// Creates the cron-agent definition and run tables in a workspace database.
/// </summary>
public sealed class CronSchemaManager(SqliteConnection connection)
{
    /// <summary>
    /// Ensures cron schema tables and indexes exist. Safe to call repeatedly.
    /// Handles upgrades safely: before adding the v2 partial unique index, resolves any
    /// pre-existing duplicate running rows (keeping the newest, marking older ones 'failed')
    /// so a schema upgrade on a dirty live DB cannot crash.
    ///
    /// v3 (FE-B, 2026-06-13): adds <c>cost_provenance TEXT</c> column to <c>cron_runs</c>.
    /// ALTER TABLE ADD COLUMN is safe for nullable columns: existing rows get NULL, which the DTO
    /// maps to 'unavailable' so historical rows are honest. This bumps the CRON DB schema version
    /// (cron_schema_versions table), NOT the main index SchemaVersion.Current (scope.db); no
    /// re-indexing is triggered.
    ///
    /// v4 (R7-backend, 2026-06-14): adds <c>block_concurrent_runs INTEGER NOT NULL DEFAULT 1</c>
    /// column to <c>cron_agents</c>. Existing rows receive the DEFAULT 1 value (guard ON),
    /// which is the safe default: prior behavior was a best-effort skip row (202); the guard
    /// promotes concurrent run-now rejection to 409 Conflict. Agents that want the old skip
    /// behavior can explicitly set this to 0.
    ///
    /// v5 (T-2026-06-16-17, 2026-06-16): soft-delete for cron agents. Adds a nullable
    /// <c>deleted_at TEXT</c> column to <c>cron_agents</c> (NULL = active, non-null = ISO-8601
    /// deletion timestamp) and changes the <c>cron_runs</c> FK on <c>cron_agents(id)</c> from
    /// <c>ON DELETE CASCADE</c> to <c>ON DELETE RESTRICT</c>. cron_runs carry immutable financial
    /// records (total_cost_usd, cost_provenance, session_key, timestamps) that must survive agent
    /// deletion in perpetuity; RESTRICT makes any stray direct DELETE on cron_agents fail loudly at
    /// the DB level instead of silently cascading. SQLite cannot ALTER a FK constraint, so this
    /// recreates cron_runs inside a transaction. Both steps are idempotent: the column is added only
    /// when absent, and the table is recreated only when the FK is not already RESTRICT.
    ///
    /// v6 (T-2026-06-17-06, 2026-06-17): runaway guard. Adds a nullable
    /// <c>max_runtime_seconds INTEGER</c> column to <c>cron_agents</c> (NULL = fall back to the global
    /// supervisor default) and widens the <c>cron_runs.status</c> CHECK constraint to admit the new
    /// terminal value <c>'budget_exhausted'</c> (a run stopped by the token-budget or wall-clock
    /// guard). SQLite cannot ALTER a CHECK constraint, so this recreates <c>cron_runs</c> inside a
    /// transaction (same DROP/RENAME dance as v5), preserving every immutable financial field verbatim.
    /// Both sub-steps are idempotent: the column is added only when absent, and the table is recreated
    /// only when the existing CHECK does not already admit <c>'budget_exhausted'</c>. Lazy-read gotcha:
    /// any live read endpoint that projects <c>max_runtime_seconds</c> or filters on the new status must
    /// reach this migration first; every cron read goes through <see cref="CronRepository.EnsureSchema"/>.
    ///
    /// v7 (F6, 2026-07-01): metering-misconfiguration flag. Adds a nullable
    /// <c>metering_misconfigured INTEGER</c> column to <c>cron_runs</c> via ALTER TABLE ADD COLUMN,
    /// guarded by a PRAGMA table_info column-existence check. NULL reads back as <c>false</c> (honest
    /// for historical rows that predate the column). The flag is set by the cron runner when metering
    /// was expected (proxy base-url and api-key were both set) but the final resolved provenance is
    /// still <c>unmetered</c> after the F2 self-reported fallback. It is NEVER set for
    /// <c>joined_exact</c>, <c>provider_estimated</c>, <c>unavailable</c>, or honest no-proxy
    /// <c>unmetered</c>. This is the CRON DB <c>cron_schema_versions</c>, NOT
    /// <c>SchemaVersion.Current</c> (scope.db); no re-indexing is triggered. v7 runs last and only
    /// ADDs a column; the v5/v6 table-recreate blocks are not modified.
    /// </summary>
    public void EnsureSchema()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS cron_schema_versions (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT NOT NULL,
                description TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS cron_agents (
                id           TEXT PRIMARY KEY,
                label        TEXT NOT NULL COLLATE NOCASE UNIQUE,
                schedule     TEXT NOT NULL,
                prompt       TEXT NOT NULL,
                repo_path    TEXT NOT NULL,
                harness      TEXT NOT NULL,
                model        TEXT,
                token_budget INTEGER,
                enabled      INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0, 1)),
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                CHECK(token_budget IS NULL OR token_budget > 0),
                CHECK(harness IN ('claude-code', 'codex'))
            ) STRICT;

            CREATE TABLE IF NOT EXISTS cron_runs (
                id               TEXT PRIMARY KEY,
                cron_agent_id    TEXT NOT NULL REFERENCES cron_agents(id) ON DELETE CASCADE,
                dispatch_id      TEXT,
                session_key      TEXT,
                started_at       TEXT NOT NULL,
                completed_at     TEXT,
                status           TEXT NOT NULL CHECK(status IN ('running', 'completed', 'failed', 'skipped')),
                exit_code        INTEGER,
                total_cost_usd   REAL,
                skip_reason      TEXT,
                error_message    TEXT
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_cron_agents_enabled ON cron_agents(enabled);
            CREATE INDEX IF NOT EXISTS idx_cron_runs_agent_started ON cron_runs(cron_agent_id, started_at);
            CREATE INDEX IF NOT EXISTS idx_cron_runs_dispatch ON cron_runs(dispatch_id);
            """;
        cmd.ExecuteNonQuery();

        InsertSchemaVersion(1, "Cron agents definitions and run records");

        // v2: partial unique index guaranteeing at most one 'running' row per agent.
        // Resolve any pre-existing duplicate running rows before creating the index so this
        // is safe on a dirty live DB (duplicate rows would make CREATE UNIQUE INDEX fail).
        ResolveStaleRunningRows();
        ApplyV2UniqueRunningIndex();
        InsertSchemaVersion(2, "Partial unique index: at most one running row per agent");

        // v3: cost_provenance TEXT column on cron_runs (FE-B, 2026-06-13).
        // ALTER TABLE ADD COLUMN is safe for nullable columns: the column is added when absent,
        // existing rows get NULL (read back as 'unavailable' provenance by the DTO mapper).
        // This is the CRON DB cron_schema_versions, NOT the main index SchemaVersion.Current.
        ApplyV3CostProvenance();
        InsertSchemaVersion(3, "cost_provenance TEXT column on cron_runs (FE-B)");

        // v4: block_concurrent_runs INTEGER NOT NULL DEFAULT 1 on cron_agents (R7-backend, 2026-06-14).
        // Existing rows receive DEFAULT 1 (guard ON) via the DEFAULT clause on the ALTER TABLE.
        // SQLite ALTER TABLE ADD COLUMN with a NOT NULL DEFAULT is safe: rows that pre-date the column
        // report the default value. This is forward-only; no data migration is needed.
        ApplyV4BlockConcurrentRuns();
        InsertSchemaVersion(4, "block_concurrent_runs column on cron_agents (R7-backend)");

        // v5: soft-delete cron agents (T-2026-06-16-17, 2026-06-16).
        // Adds nullable deleted_at to cron_agents (NULL = active) and changes the cron_runs FK to
        // ON DELETE RESTRICT so cron_runs financial records (total_cost_usd, cost_provenance,
        // session_key, timestamps) can never be silently cascaded away when an agent row is deleted.
        // Both sub-steps are idempotent and the FK recreate runs in a transaction.
        ApplyV5SoftDelete();
        InsertSchemaVersion(5, "Soft-delete cron_agents: deleted_at column + FK RESTRICT on cron_runs");

        // v6: runaway guard (T-2026-06-17-06, 2026-06-17).
        // Adds nullable max_runtime_seconds to cron_agents (NULL = global default) and widens the
        // cron_runs.status CHECK to admit 'budget_exhausted'. The CHECK widening requires a table
        // recreate (SQLite cannot ALTER a CHECK); it preserves the v5 FK RESTRICT and every row.
        // Both sub-steps are idempotent.
        ApplyV6RunawayGuard();
        InsertSchemaVersion(6, "Runaway guard: max_runtime_seconds column + 'budget_exhausted' run status");

        // v7: metering_misconfigured INTEGER column on cron_runs (F6, 2026-07-01).
        // ALTER TABLE ADD COLUMN is safe for nullable columns: the column is added when absent,
        // existing rows get NULL (read back as false by the DTO mapper). v7 runs LAST and only
        // ADDs a column; the v5/v6 table-recreate blocks are not modified. This is the CRON DB
        // cron_schema_versions, NOT the main index SchemaVersion.Current.
        ApplyV7MeteringMisconfigured();
        InsertSchemaVersion(7, "metering_misconfigured column on cron_runs (F6)");

        // v8: STRICT conversion (T-2026-07-12-01, hard rule owner 2026-07-12). NOT version-gated, like
        // every other step in this method: it runs on every EnsureSchema call with its own idempotency
        // guard (see ApplyV8Strict). cron_schema_versions' v1 CREATE already carries STRICT and is
        // never recreated afterward, so this step is a genuine no-op there once migrated once; cron_runs
        // and cron_agents ARE recreated by the byte-stable v5/v6 templates above on every fresh
        // bootstrap (those templates are deliberately left non-STRICT so the shipped migrations stay
        // byte-stable), so this step is the one place that guarantees their FINAL shape is STRICT.
        ApplyV8Strict();
        InsertSchemaVersion(8, "STRICT conversion: cron_schema_versions, cron_agents, cron_runs recreated STRICT (T-2026-07-12-01)");
    }

    /// <summary>
    /// Resolves pre-existing duplicate 'running' rows for any agent: keeps the most recently
    /// started one, marks all older ones 'failed' with an honest reap message. This is called
    /// during EnsureSchema before the v2 partial unique index is created, so the upgrade never
    /// crashes on a dirty database.
    /// </summary>
    private void ResolveStaleRunningRows()
    {
        // Identify the IDs of running rows that are NOT the newest running row per agent.
        using var findCmd = connection.CreateCommand();
        findCmd.CommandText = """
            SELECT r.id
            FROM cron_runs r
            WHERE r.status = 'running'
              AND r.id NOT IN (
                  SELECT id FROM cron_runs cr
                  WHERE cr.cron_agent_id = r.cron_agent_id AND cr.status = 'running'
                  ORDER BY cr.started_at DESC
                  LIMIT 1
              );
            """;

        var staleIds = new List<string>();
        using (var reader = findCmd.ExecuteReader())
        {
            while (reader.Read())
                staleIds.Add(reader.GetString(0));
        }

        if (staleIds.Count == 0)
            return;

        var now = DateTime.UtcNow.ToString("o");
        foreach (var id in staleIds)
        {
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE cron_runs
                SET status = 'failed',
                    completed_at = $completedAt,
                    error_message = 'reaped: duplicate running row resolved during schema upgrade'
                WHERE id = $id;
                """;
            updateCmd.Parameters.AddWithValue("$id", id);
            updateCmd.Parameters.AddWithValue("$completedAt", now);
            updateCmd.ExecuteNonQuery();
        }
    }

    private void ApplyV2UniqueRunningIndex()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_cron_runs_one_running
            ON cron_runs(cron_agent_id) WHERE status = 'running';
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds <c>cost_provenance TEXT</c> to <c>cron_runs</c> when the column does not already
    /// exist. SQLite ALTER TABLE ADD COLUMN is idempotent-safe via the column-existence check;
    /// if the column already exists the command is silently skipped.
    /// </summary>
    private void ApplyV3CostProvenance()
    {
        // Check whether the column already exists (column-info query is the safe way in SQLite
        // -- ADD COLUMN without IF NOT EXISTS would throw on existing columns).
        var hasColumn = false;
        using (var infoCmd = connection.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(cron_runs);";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "cost_provenance", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE cron_runs ADD COLUMN cost_provenance TEXT;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds <c>metering_misconfigured INTEGER</c> to <c>cron_runs</c> when the column does not already
    /// exist. SQLite ALTER TABLE ADD COLUMN is idempotent-safe via the column-existence check;
    /// if the column already exists the command is silently skipped. NULL reads back as <c>false</c>
    /// (honest for historical rows that predate the column). Exact structural mirror of
    /// <see cref="ApplyV3CostProvenance"/>.
    /// </summary>
    private void ApplyV7MeteringMisconfigured()
    {
        var hasColumn = false;
        using (var infoCmd = connection.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(cron_runs);";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "metering_misconfigured", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE cron_runs ADD COLUMN metering_misconfigured INTEGER;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds <c>block_concurrent_runs INTEGER NOT NULL DEFAULT 1</c> to <c>cron_agents</c> when the
    /// column does not already exist. SQLite allows ADD COLUMN with a NOT NULL DEFAULT on an existing
    /// table as long as the default value is a constant (not an expression). Existing rows report 1,
    /// which preserves the safe default (guard ON).
    /// </summary>
    private void ApplyV4BlockConcurrentRuns()
    {
        var hasColumn = false;
        using (var infoCmd = connection.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(cron_agents);";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "block_concurrent_runs", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE cron_agents ADD COLUMN block_concurrent_runs INTEGER NOT NULL DEFAULT 1;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// STRICT conversion (T-2026-07-12-01, hard rule owner 2026-07-12): recreates
    /// <c>cron_schema_versions</c>, <c>cron_agents</c>, and <c>cron_runs</c> as STRICT in one
    /// transaction. Both <c>cron_agents.id</c> and <c>cron_runs.id</c> are treated as immutable/critical
    /// (<see cref="NullPkPolicy.Throw"/> on a NULL primary key, never silently dropped): a lost
    /// <c>cron_runs</c> row would erase an immutable financial record, and a lost <c>cron_agents</c> row
    /// would silently delete a user's cron agent definition -- the audit that scoped this task groups
    /// <c>cron_agents.id</c> in the same NULL-PK caveat as <c>cron_runs.id</c> and <c>wire_calls.id</c>,
    /// and the safe reading of that grouping is the same fail-loud policy for all three.
    /// <c>cron_schema_versions.version</c> is an INTEGER PRIMARY KEY rowid alias (never stored NULL) and
    /// is copied straight. <see cref="StrictTableMigrator.Recreate"/> preserves the v5 FK
    /// <c>ON DELETE RESTRICT</c> and the v6-widened status CHECK exactly (this method's CREATE
    /// statements mirror the byte-stable v5/v6/v7 shape, just with STRICT appended and every column
    /// through v7 included).
    /// <para>
    /// Retry posture matches <see cref="Telemetry.WireCallSchemaManager"/>'s v4 STRICT step: the WHOLE
    /// transaction is wrapped as one <see cref="SqliteRetry"/> unit (not the individual bare statements
    /// inside it) because a BUSY/LOCKED failure rolls the transaction back first, so retrying the entire
    /// sequence from scratch cannot double-apply.
    /// </para>
    /// <para>
    /// Idempotency: unlike the version-gated <c>WireCallSchemaManager</c>, this whole method (like every
    /// other step in <c>EnsureSchema</c>) runs unconditionally on every call, so the pre-check below
    /// probes all THREE tables' own existence+STRICT state before opening the transaction/PRAGMA
    /// machinery at all, and skips entirely once none need it. A single-table anchor check would be
    /// unsound here: cron_runs is recreated (non-STRICT) by the byte-stable v5/v6 templates on every
    /// fresh bootstrap even though cron_agents and cron_schema_versions are STRICT from their v1 CREATE,
    /// so an anchor on either of those two would wrongly report "already migrated" while cron_runs still
    /// needs converting.
    /// </para>
    /// </summary>
    private void ApplyV8Strict()
    {
        if (!V8NeedsRecreate())
            return;

        using (var offCmd = connection.CreateCommand())
        {
            offCmd.CommandText = "PRAGMA foreign_keys = OFF;";
            offCmd.ExecuteNonQuery();
        }

        try
        {
            SqliteRetry.Execute(() =>
            {
                ExecuteNonQuery("BEGIN TRANSACTION;");
                try
                {
                    StrictTableMigrator.Recreate(
                        connection,
                        "cron_schema_versions",
                        """
                        CREATE TABLE cron_schema_versions_new (
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
                        "cron_agents",
                        """
                        CREATE TABLE cron_agents_new (
                            id                    TEXT PRIMARY KEY,
                            label                 TEXT NOT NULL COLLATE NOCASE UNIQUE,
                            schedule              TEXT NOT NULL,
                            prompt                TEXT NOT NULL,
                            repo_path             TEXT NOT NULL,
                            harness               TEXT NOT NULL,
                            model                 TEXT,
                            token_budget          INTEGER,
                            enabled               INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0, 1)),
                            created_at            TEXT NOT NULL,
                            updated_at            TEXT NOT NULL,
                            block_concurrent_runs INTEGER NOT NULL DEFAULT 1,
                            deleted_at            TEXT,
                            max_runtime_seconds   INTEGER,
                            CHECK(token_budget IS NULL OR token_budget > 0),
                            CHECK(harness IN ('claude-code', 'codex'))
                        ) STRICT;
                        """,
                        """
                        id, label, schedule, prompt, repo_path, harness, model, token_budget, enabled,
                        created_at, updated_at, block_concurrent_runs, deleted_at, max_runtime_seconds
                        """,
                        pkColumn: "id",
                        NullPkPolicy.Throw);

                    StrictTableMigrator.Recreate(
                        connection,
                        "cron_runs",
                        """
                        CREATE TABLE cron_runs_new (
                            id                     TEXT PRIMARY KEY,
                            cron_agent_id          TEXT NOT NULL REFERENCES cron_agents(id) ON DELETE RESTRICT,
                            dispatch_id            TEXT,
                            session_key            TEXT,
                            started_at             TEXT NOT NULL,
                            completed_at           TEXT,
                            status                 TEXT NOT NULL CHECK(status IN ('running', 'completed', 'failed', 'skipped', 'budget_exhausted')),
                            exit_code              INTEGER,
                            total_cost_usd         REAL,
                            skip_reason            TEXT,
                            error_message          TEXT,
                            cost_provenance        TEXT,
                            metering_misconfigured INTEGER
                        ) STRICT;
                        """,
                        """
                        id, cron_agent_id, dispatch_id, session_key, started_at, completed_at, status,
                        exit_code, total_cost_usd, skip_reason, error_message, cost_provenance,
                        metering_misconfigured
                        """,
                        pkColumn: "id",
                        NullPkPolicy.Throw);

                    ExecuteNonQuery("COMMIT;");
                }
                catch
                {
                    ExecuteNonQuery("ROLLBACK;");
                    throw;
                }
            });
        }
        finally
        {
            using var onCmd = connection.CreateCommand();
            onCmd.CommandText = "PRAGMA foreign_keys = ON;";
            onCmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns true when at least one of cron_schema_versions, cron_agents, or cron_runs exists but is
    /// not yet STRICT. See the idempotency remarks on <see cref="ApplyV8Strict"/> for why every table is
    /// checked individually instead of a single anchor.
    /// </summary>
    private bool V8NeedsRecreate()
    {
        foreach (var table in (ReadOnlySpan<string>)["cron_schema_versions", "cron_agents", "cron_runs"])
        {
            if (StrictTableMigrator.TableExists(connection, table) && !StrictTableMigrator.IsStrict(connection, table))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Applies the v5 soft-delete migration: adds the nullable <c>deleted_at</c> column to
    /// <c>cron_agents</c> and rewrites the <c>cron_runs</c> FK from <c>ON DELETE CASCADE</c> to
    /// <c>ON DELETE RESTRICT</c>. Both sub-steps are individually idempotent:
    /// <list type="bullet">
    /// <item>the column is added only when the column-info query shows it absent;</item>
    /// <item>the cron_runs table is recreated only when its FK is not already RESTRICT
    /// (detected via PRAGMA foreign_key_list).</item>
    /// </list>
    /// The table-recreate runs inside an explicit transaction (with foreign_keys temporarily off
    /// so DROP/RENAME does not trip the constraint) so a partial failure rolls back cleanly and the
    /// existing rows (and their immutable cost fields) are preserved verbatim by INSERT ... SELECT.
    /// </summary>
    private void ApplyV5SoftDelete()
    {
        AddDeletedAtColumnIfMissing();
        ChangeCronRunsFkToRestrictIfNeeded();
    }

    private void AddDeletedAtColumnIfMissing()
    {
        var hasColumn = false;
        using (var infoCmd = connection.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(cron_agents);";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "deleted_at", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE cron_agents ADD COLUMN deleted_at TEXT;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Recreates <c>cron_runs</c> with an <c>ON DELETE RESTRICT</c> FK on <c>cron_agents(id)</c>
    /// when the existing FK is not already RESTRICT. Idempotent: a no-op once the FK is RESTRICT.
    /// </summary>
    private void ChangeCronRunsFkToRestrictIfNeeded()
    {
        if (IsCronRunsFkAlreadyRestrict())
            return;

        // SQLite forbids dropping a table referenced by a FK while foreign_keys is ON, and the
        // DROP/RENAME dance below would otherwise trip the constraint. Disable enforcement for the
        // duration of the recreate, then restore it. PRAGMA foreign_keys is a no-op inside an active
        // transaction, so it must be toggled OUTSIDE the BEGIN/COMMIT block.
        ExecuteNonQuery("PRAGMA foreign_keys = OFF;");
        try
        {
            ExecuteNonQuery("BEGIN TRANSACTION;");
            try
            {
                ExecuteNonQuery("""
                    CREATE TABLE cron_runs_new (
                        id               TEXT PRIMARY KEY,
                        cron_agent_id    TEXT NOT NULL REFERENCES cron_agents(id) ON DELETE RESTRICT,
                        dispatch_id      TEXT,
                        session_key      TEXT,
                        started_at       TEXT NOT NULL,
                        completed_at     TEXT,
                        status           TEXT NOT NULL CHECK(status IN ('running', 'completed', 'failed', 'skipped')),
                        exit_code        INTEGER,
                        total_cost_usd   REAL,
                        skip_reason      TEXT,
                        error_message    TEXT,
                        cost_provenance  TEXT
                    );
                    """);

                // Copy every existing row verbatim. Column list is explicit (not SELECT *) so the
                // copy is robust to any future column-order drift and preserves all financial fields.
                ExecuteNonQuery("""
                    INSERT INTO cron_runs_new (
                        id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                        status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance)
                    SELECT
                        id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                        status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance
                    FROM cron_runs;
                    """);

                ExecuteNonQuery("DROP TABLE cron_runs;");
                ExecuteNonQuery("ALTER TABLE cron_runs_new RENAME TO cron_runs;");

                // Recreate the indexes that lived on the old table.
                ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_cron_runs_agent_started ON cron_runs(cron_agent_id, started_at);");
                ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_cron_runs_dispatch ON cron_runs(dispatch_id);");
                ExecuteNonQuery("CREATE UNIQUE INDEX IF NOT EXISTS ux_cron_runs_one_running ON cron_runs(cron_agent_id) WHERE status = 'running';");

                ExecuteNonQuery("COMMIT;");
            }
            catch
            {
                ExecuteNonQuery("ROLLBACK;");
                throw;
            }
        }
        finally
        {
            ExecuteNonQuery("PRAGMA foreign_keys = ON;");
        }
    }

    /// <summary>
    /// Returns true when the <c>cron_runs</c> FK referencing <c>cron_agents</c> is already
    /// <c>ON DELETE RESTRICT</c> (or otherwise non-CASCADE). Uses PRAGMA foreign_key_list, whose
    /// <c>on_delete</c> column reports the deletion action as a string.
    /// </summary>
    private bool IsCronRunsFkAlreadyRestrict()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_list(cron_runs);";
        using var reader = cmd.ExecuteReader();
        // PRAGMA foreign_key_list columns: id, seq, table, from, to, on_update, on_delete, match.
        var onDeleteOrdinal = reader.GetOrdinal("on_delete");
        var tableOrdinal = reader.GetOrdinal("table");
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(tableOrdinal), "cron_agents", StringComparison.OrdinalIgnoreCase))
                continue;

            var onDelete = reader.GetString(onDeleteOrdinal);
            // "CASCADE" means the old (unsafe) schema is still present; anything else (RESTRICT,
            // NO ACTION) is acceptable and means the migration has already run.
            return !string.Equals(onDelete, "CASCADE", StringComparison.OrdinalIgnoreCase);
        }

        // No FK to cron_agents found (unexpected). Treat as "needs no recreate" to avoid corrupting
        // an unrecognized schema; the recreate path assumes the canonical v1 shape.
        return true;
    }

    /// <summary>
    /// Applies the v6 runaway-guard migration: adds the nullable <c>max_runtime_seconds</c> column to
    /// <c>cron_agents</c> and widens the <c>cron_runs.status</c> CHECK to admit <c>'budget_exhausted'</c>.
    /// Both sub-steps are individually idempotent:
    /// <list type="bullet">
    /// <item>the column is added only when the column-info query shows it absent;</item>
    /// <item>the cron_runs table is recreated only when its existing CHECK does not already mention
    /// <c>budget_exhausted</c> (detected from the stored DDL in <c>sqlite_master</c>).</item>
    /// </list>
    /// The recreate preserves the v5 <c>ON DELETE RESTRICT</c> FK and copies every existing row verbatim.
    /// </summary>
    private void ApplyV6RunawayGuard()
    {
        AddMaxRuntimeColumnIfMissing();
        WidenCronRunsStatusCheckIfNeeded();
    }

    private void AddMaxRuntimeColumnIfMissing()
    {
        var hasColumn = false;
        using (var infoCmd = connection.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(cron_agents);";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "max_runtime_seconds", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE cron_agents ADD COLUMN max_runtime_seconds INTEGER;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Recreates <c>cron_runs</c> with a status CHECK that admits <c>'budget_exhausted'</c> when the
    /// current CHECK does not already. Idempotent: a no-op once the DDL mentions the new value.
    /// Keeps the v5 <c>ON DELETE RESTRICT</c> FK so the financial-record protection is not regressed.
    /// </summary>
    private void WidenCronRunsStatusCheckIfNeeded()
    {
        if (CronRunsStatusCheckAdmitsBudgetExhausted())
            return;

        // Same DROP/RENAME dance as v5: foreign_keys OFF outside the transaction (PRAGMA is a no-op
        // inside an active transaction), recreate with the widened CHECK + RESTRICT FK, copy rows.
        ExecuteNonQuery("PRAGMA foreign_keys = OFF;");
        try
        {
            ExecuteNonQuery("BEGIN TRANSACTION;");
            try
            {
                ExecuteNonQuery("""
                    CREATE TABLE cron_runs_new (
                        id               TEXT PRIMARY KEY,
                        cron_agent_id    TEXT NOT NULL REFERENCES cron_agents(id) ON DELETE RESTRICT,
                        dispatch_id      TEXT,
                        session_key      TEXT,
                        started_at       TEXT NOT NULL,
                        completed_at     TEXT,
                        status           TEXT NOT NULL CHECK(status IN ('running', 'completed', 'failed', 'skipped', 'budget_exhausted')),
                        exit_code        INTEGER,
                        total_cost_usd   REAL,
                        skip_reason      TEXT,
                        error_message    TEXT,
                        cost_provenance  TEXT
                    );
                    """);

                ExecuteNonQuery("""
                    INSERT INTO cron_runs_new (
                        id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                        status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance)
                    SELECT
                        id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                        status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance
                    FROM cron_runs;
                    """);

                ExecuteNonQuery("DROP TABLE cron_runs;");
                ExecuteNonQuery("ALTER TABLE cron_runs_new RENAME TO cron_runs;");

                ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_cron_runs_agent_started ON cron_runs(cron_agent_id, started_at);");
                ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_cron_runs_dispatch ON cron_runs(dispatch_id);");
                ExecuteNonQuery("CREATE UNIQUE INDEX IF NOT EXISTS ux_cron_runs_one_running ON cron_runs(cron_agent_id) WHERE status = 'running';");

                ExecuteNonQuery("COMMIT;");
            }
            catch
            {
                ExecuteNonQuery("ROLLBACK;");
                throw;
            }
        }
        finally
        {
            ExecuteNonQuery("PRAGMA foreign_keys = ON;");
        }
    }

    /// <summary>
    /// Returns true when the stored <c>cron_runs</c> DDL already admits the <c>budget_exhausted</c>
    /// status value (so the v6 CHECK widening is a no-op). Reads the canonical CREATE statement from
    /// <c>sqlite_master</c>.
    /// </summary>
    private bool CronRunsStatusCheckAdmitsBudgetExhausted()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='cron_runs';";
        var ddl = cmd.ExecuteScalar() as string;
        // A missing table is unexpected here (v1 created it); treat as already-admitting to avoid a
        // spurious recreate against an unrecognized schema.
        return ddl is null || ddl.Contains("budget_exhausted", StringComparison.OrdinalIgnoreCase);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void InsertSchemaVersion(int version, string description)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO cron_schema_versions (version, applied_at, description)
            VALUES ($version, $appliedAt, $description);
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$description", description);
        cmd.ExecuteNonQuery();
    }
}

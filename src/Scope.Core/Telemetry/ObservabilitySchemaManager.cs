namespace Scope.Core.Telemetry;

using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;

/// <summary>
/// Creates the obs_* observability tables in .scope/telemetry.db.
/// Coexists with the legacy retrieval_requests/retrieval_results tables
/// managed by <see cref="TelemetrySchemaManager"/>.
/// </summary>
public class ObservabilitySchemaManager(SqliteConnection connection)
{
    /// <summary>
    /// Ensures all observability tables and indexes exist.
    /// Safe to call multiple times (CREATE TABLE IF NOT EXISTS).
    /// </summary>
    public void EnsureSchema()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_schema_versions (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT NOT NULL,
                description TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_requests (
                id                TEXT PRIMARY KEY,
                parent_request_id TEXT,
                caller_surface    TEXT NOT NULL,
                operation         TEXT NOT NULL,
                agent_session_key TEXT,
                query_text        TEXT,
                parameters_json   TEXT,
                status            TEXT NOT NULL DEFAULT 'started',
                exit_code         INTEGER,
                error_message     TEXT,
                total_elapsed_ms  INTEGER,
                created_at        TEXT NOT NULL,
                completed_at      TEXT
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_request_stages (
                id            TEXT PRIMARY KEY,
                request_id    TEXT NOT NULL REFERENCES obs_requests(id),
                stage_name    TEXT NOT NULL,
                stage_order   INTEGER NOT NULL,
                elapsed_ms    INTEGER NOT NULL,
                metadata_json TEXT,
                created_at    TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_retrieval_results (
                id              TEXT PRIMARY KEY,
                request_id      TEXT NOT NULL REFERENCES obs_requests(id),
                document_key    TEXT NOT NULL,
                rank            INTEGER NOT NULL,
                score           REAL NOT NULL,
                provider_source TEXT NOT NULL,
                is_exact_match  INTEGER NOT NULL DEFAULT 0,
                boosts_applied  TEXT NOT NULL DEFAULT '',
                created_at      TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_packing_metrics (
                id                   TEXT PRIMARY KEY,
                request_id           TEXT NOT NULL REFERENCES obs_requests(id),
                target_fqn           TEXT NOT NULL,
                budget_requested     INTEGER NOT NULL,
                budget_used          INTEGER NOT NULL,
                budget_remaining     INTEGER NOT NULL,
                symbols_considered   INTEGER NOT NULL,
                symbols_included     INTEGER NOT NULL,
                symbols_rejected     INTEGER NOT NULL,
                compression_ct       INTEGER NOT NULL,
                full_body_ct         INTEGER NOT NULL,
                signature_only_ct    INTEGER NOT NULL,
                call_site_ct         INTEGER NOT NULL,
                dedup_removed_ct     INTEGER NOT NULL,
                free_symbol_ct       INTEGER NOT NULL,
                test_callers_capped  INTEGER NOT NULL DEFAULT 0,
                target_token_count   INTEGER NOT NULL,
                context_token_count  INTEGER NOT NULL,
                created_at           TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_cost_estimates (
                id                      TEXT PRIMARY KEY,
                request_id              TEXT NOT NULL REFERENCES obs_requests(id),
                model_id                TEXT NOT NULL,
                provider                TEXT NOT NULL,
                input_tokens            INTEGER NOT NULL,
                estimated_output_tokens INTEGER NOT NULL,
                input_cost_usd          REAL NOT NULL,
                output_cost_usd         REAL NOT NULL,
                total_cost_usd          REAL NOT NULL,
                created_at              TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_agent_sessions (
                session_key   TEXT PRIMARY KEY,
                agent_type    TEXT NOT NULL,
                agent_subtype TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at  TEXT NOT NULL,
                request_count INTEGER NOT NULL DEFAULT 0
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_agent_events (
                id              TEXT PRIMARY KEY,
                session_key     TEXT NOT NULL REFERENCES obs_agent_sessions(session_key),
                request_id      TEXT REFERENCES obs_requests(id),
                event_type      TEXT NOT NULL,
                event_data_json TEXT,
                created_at      TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS obs_payloads (
                id           TEXT PRIMARY KEY,
                request_id   TEXT NOT NULL REFERENCES obs_requests(id),
                direction    TEXT NOT NULL CHECK(direction IN ('inbound','outbound')),
                content_type TEXT NOT NULL,
                payload_text TEXT NOT NULL,
                token_count  INTEGER,
                created_at   TEXT NOT NULL
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_obsreq_surface   ON obs_requests(caller_surface);
            CREATE INDEX IF NOT EXISTS idx_obsreq_operation  ON obs_requests(operation);
            CREATE INDEX IF NOT EXISTS idx_obsreq_agent      ON obs_requests(agent_session_key);
            CREATE INDEX IF NOT EXISTS idx_obsreq_created    ON obs_requests(created_at);
            CREATE INDEX IF NOT EXISTS idx_obsreq_parent     ON obs_requests(parent_request_id);
            CREATE INDEX IF NOT EXISTS idx_obsstage_request  ON obs_request_stages(request_id);
            CREATE INDEX IF NOT EXISTS idx_obsret_request    ON obs_retrieval_results(request_id);
            CREATE INDEX IF NOT EXISTS idx_obsret_dockey     ON obs_retrieval_results(document_key);
            CREATE INDEX IF NOT EXISTS idx_obspack_request   ON obs_packing_metrics(request_id);
            CREATE INDEX IF NOT EXISTS idx_obscost_request   ON obs_cost_estimates(request_id);
            CREATE INDEX IF NOT EXISTS idx_obscost_created   ON obs_cost_estimates(created_at);
            CREATE INDEX IF NOT EXISTS idx_obsagent_session  ON obs_agent_events(session_key);
            CREATE INDEX IF NOT EXISTS idx_obsagent_request  ON obs_agent_events(request_id);
            CREATE INDEX IF NOT EXISTS idx_obsagent_type     ON obs_agent_events(event_type);
            CREATE INDEX IF NOT EXISTS idx_obspay_request    ON obs_payloads(request_id);
            CREATE INDEX IF NOT EXISTS idx_obspay_direction  ON obs_payloads(direction);
            """;
        cmd.ExecuteNonQueryRetrying();

        InsertSchemaVersion(1, "Initial observability schema with obs_* tables");

        MigrateToV2();
        MigrateToV3();
    }

    private void MigrateToV2()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_dispatches (
                id           TEXT PRIMARY KEY,
                session_key  TEXT,
                label        TEXT,
                created_at   TEXT NOT NULL,
                completed_at TEXT
            ) STRICT;
            CREATE INDEX IF NOT EXISTS idx_obsdisp_session ON obs_dispatches(session_key);
            """;
        cmd.ExecuteNonQueryRetrying();

        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_requests ADD COLUMN dispatch_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsreq_dispatch ON obs_requests(dispatch_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(2, "Dispatch correlation");
    }

    private void MigrateToV3()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_agent_spans (
                id              TEXT PRIMARY KEY,
                session_key     TEXT NOT NULL,
                dispatch_id     TEXT,
                parent_span_id  TEXT,
                agent_type      TEXT NOT NULL,
                agent_name      TEXT NOT NULL,
                started_at      TEXT NOT NULL,
                completed_at    TEXT,
                input_summary   TEXT,
                output_summary  TEXT,
                status          TEXT NOT NULL DEFAULT 'active'
            ) STRICT;
            CREATE INDEX IF NOT EXISTS idx_obsspan_session ON obs_agent_spans(session_key);
            CREATE INDEX IF NOT EXISTS idx_obsspan_dispatch ON obs_agent_spans(dispatch_id);
            CREATE INDEX IF NOT EXISTS idx_obsspan_parent ON obs_agent_spans(parent_span_id);
            """;
        cmd.ExecuteNonQueryRetrying();

        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_events ADD COLUMN span_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsagent_span ON obs_agent_events(span_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(3, "Agent span hierarchy");
        MigrateToV4();
    }

    private void MigrateToV4()
    {
        // Add parent_session_key to obs_agent_sessions for session hierarchy
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN parent_session_key TEXT;";
            cmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_parent ON obs_agent_sessions(parent_session_key);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(4, "Agent session continuity");
        MigrateToV5();
    }

    private void MigrateToV5()
    {
        // Additive columns on obs_agent_spans: agent_ref stores the host-assigned agent id
        // of the spawned agent (from the Agent tool result) so subsequent payloads carrying
        // that id as agent_id can be linked back to this span; the token columns store exact
        // usage from synchronous Agent completions for span-level attribution.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap each in try/catch.
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_spans ADD COLUMN agent_ref TEXT;",
            "ALTER TABLE obs_agent_spans ADD COLUMN total_tokens INTEGER;",
            "ALTER TABLE obs_agent_spans ADD COLUMN input_tokens INTEGER;",
            "ALTER TABLE obs_agent_spans ADD COLUMN output_tokens INTEGER;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsspan_agentref ON obs_agent_spans(session_key, agent_ref);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(5, "Span agent linkage (agent_ref) and exact token usage");
        MigrateToV6();
    }

    private void MigrateToV6()
    {
        // Add pinned-cost columns to obs_agent_spans for price-immutability.
        // The span is the cron rollup carrier: cron sessions have NO obs_requests row
        // (tool_call events carry request_id=NULL), so pinning on obs_cost_estimates
        // (which requires a NOT NULL request_id FK) cannot serve the cron rollup.
        // Pinning here lets TraceRollup.ResolveTaskCost read the stored snapshot and
        // emit pinned=true instead of re-deriving from the live pricing table.
        // pinned_breakdown_json stores the serialized List<CostBreakdownComponent> so the
        // breakdown can be reconstructed exactly without re-reading rates.
        // Forward-only: nullable, no backfill. Legacy spans stay re-derived (pinned=false).
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_cost_usd REAL;",
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_model_id TEXT;",
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_prices_as_of TEXT;",
            "ALTER TABLE obs_agent_spans ADD COLUMN pinned_breakdown_json TEXT;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }
        InsertSchemaVersion(6, "Cost pinning: price snapshot at capture in obs_agent_spans");
        MigrateToV7();
    }

    private void MigrateToV7()
    {
        // A5 transparency (T-2026-06-15-17): persist how the session key was obtained so the
        // origin is shown, never implied. Values: 'scope-launched' (tier 1 SCOPE_SESSION_KEY),
        // 'host-provided' (tier 2 best-effort host session id), 'unattributed' (tier 3 per-day
        // bucket). Forward-only, nullable; legacy rows stay NULL.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN origin TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(7, "Session origin label (scope-launched/host-provided/unattributed)");
        MigrateToV8();
    }

    private void MigrateToV8()
    {
        // Soft-delete support (T-2026-06-15-16/ST-01): persist when a session was soft-deleted so it
        // can be hidden by default, surfaced in a trash view, and restored. ISO-8601 UTC string when
        // soft-deleted, NULL otherwise. Reversible by design: only this flag is touched, never the
        // session's events/spans/requests (NO cascade). Forward-only, nullable; legacy rows stay NULL.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN deleted_at TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(8, "Session soft-delete flag (deleted_at) on obs_agent_sessions");
        MigrateToV9();
    }

    private void MigrateToV9()
    {
        // Tier-2 recorded-model attribution (T-2026-06-15-04): persist the model that was REQUESTED
        // for a span's work so the trace rollup can price the un-metered case from the recorded model
        // instead of the assumed default. Two capture sources:
        //   - cron: the launched run's request.Model (Scope knows it; no parsing).
        //   - interactive hook: tool_input.model on an Agent dispatch, recorded onto the dispatched
        //     child agent's span (host-shape knowledge stays in ClaudeCodeHookAdapter).
        // It is a REQUESTED/recorded model (precedence Tier 2): wire truth (Tier 1) still wins, and
        // the assumed default (Tier 3) remains the last resort. Forward-only, nullable, no backfill.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_spans ADD COLUMN model TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(9, "Recorded/requested model on obs_agent_spans (Tier-2 cost attribution)");
        MigrateToV10();
    }

    private void MigrateToV10()
    {
        // Privileged 'user_submit' first-class event class (T-2026-06-16-19/ST-01). Every human
        // intervention (initiating prompt, follow-up, interrupt, tool approval/denial, redirection,
        // cron prompt) is recorded as an obs_agent_events row with event_type='user_submit'. Three
        // additive, nullable columns on obs_agent_events:
        //   - event_subtype: the ratified taxonomy member (see UserSubmitSubtype).
        //   - content_hash:  stable SHA-256 over the normalized content; the primary dedup key
        //                    together with session_key so the same submit captured by multiple
        //                    redundant paths (payload/hook/cron) records once.
        //   - turn_index:    0-based ordinal of the submit within the session (initiating = 0),
        //                    a secondary identity for path-specific dedup.
        // Forward-only, nullable, no backfill; legacy event rows stay NULL. A partial UNIQUE index
        // enforces the (session_key, content_hash) dedup invariant at the storage layer, scoped to
        // user_submit rows so other event types (content_hash NULL) are never constrained.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap each in try/catch.
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_events ADD COLUMN event_subtype TEXT;",
            "ALTER TABLE obs_agent_events ADD COLUMN content_hash TEXT;",
            "ALTER TABLE obs_agent_events ADD COLUMN turn_index INTEGER;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_obsagent_usersubmit_dedup
                ON obs_agent_events(session_key, content_hash)
                WHERE event_type = 'user_submit' AND content_hash IS NOT NULL;
                """;
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsagent_subtype ON obs_agent_events(event_subtype);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(10, "Privileged user_submit event class: event_subtype, content_hash, turn_index on obs_agent_events");
        MigrateToV11();
    }

    private void MigrateToV11()
    {
        // D3 session bridge (T-2026-06-19-01 Phase 3b): bind an interactive session's WIRE side to its
        // Scope side. Interactive wire rows carry Claude Code's OWN X-Claude-Code-Session-Id
        // (wire_calls.cc_session_id), NOT Scope's cli-{uuid7} SCOPE_SESSION_KEY. The hook already sees
        // BOTH ids in the same child (the CC session_id in its payload, the Scope key from
        // SCOPE_SESSION_KEY), so it records the CC session id here next to the Scope session key. The
        // header-based wire attribution pass then maps wire_calls.cc_session_id -> this column ->
        // session_key to attribute interactive spend to the right Scope session.
        // Additive, nullable, set-once (COALESCE in UpsertAgentSession), no backfill; content-free (an
        // ephemeral per-spawn id, not content). Legacy/cron rows stay NULL.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN cc_session_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_ccsession ON obs_agent_sessions(cc_session_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(11, "D3 session bridge: cc_session_id on obs_agent_sessions (CC session id -> Scope session key)");
        MigrateToV12();
    }

    private void MigrateToV12()
    {
        // Session generations (T-2026-06-19-03 Wave 1): a `/clear` in the `scope claude` wrapper keeps
        // the OS process, so the launcher-minted cli-{uuid7} SCOPE_SESSION_KEY (the BASE) stays frozen
        // and multiple `/clear`-separated conversations collapse into ONE Scope session. Claude Code DOES
        // mint a new session_id on `/clear`, so a previously-unseen cc_session_id under a base is the
        // deterministic boundary signal. This table binds (base, cc) -> a per-base GENERATION ordinal and
        // its derived effective_key (g0 SUFFIXLESS == base; g1+ == base + "#g" + n), allocated once and
        // idempotent under interleave (PK on (base, cc) + IMMEDIATE txn in ResolveEffectiveSessionKey).
        // The unique (base, generation) index guarantees a generation maps to exactly one effective_key.
        // Forward-only: existing single-generation sessions stay byte-identical (no row, g0, no suffix).
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_session_generations (
                base_session_key TEXT NOT NULL,
                cc_session_id    TEXT NOT NULL,
                generation       INTEGER NOT NULL,
                effective_key    TEXT NOT NULL,
                opened_at        TEXT NOT NULL,
                open_source      TEXT,
                PRIMARY KEY (base_session_key, cc_session_id)
            ) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS idx_obssessgen_effective
                ON obs_session_generations(base_session_key, generation);
            """;
        cmd.ExecuteNonQueryRetrying();

        // Stamp the resolved generation provenance onto the session row so a reader can attribute a
        // session to its base/generation without re-deriving. Nullable, forward-only: a NULL generation
        // is read as 0 and a NULL base_session_key as the session_key itself (legacy rows).
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap each in try/catch.
        string[] columnDdl =
        [
            "ALTER TABLE obs_agent_sessions ADD COLUMN base_session_key TEXT;",
            "ALTER TABLE obs_agent_sessions ADD COLUMN generation INTEGER;",
        ];
        foreach (var ddl in columnDdl)
        {
            try
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = ddl;
                alterCmd.ExecuteNonQueryRetrying();
            }
            catch (SqliteException) { /* column already exists */ }
        }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_base ON obs_agent_sessions(base_session_key);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(12, "Session generations: obs_session_generations + base_session_key/generation on obs_agent_sessions (/clear splits a base into generations)");

        MigrateToV13();
    }

    private void MigrateToV13()
    {
        // SessionStart source precedence (T-2026-06-19-03 Wave 2). The obs_session_generation_hints scratch
        // table that originally backed a CROSS-PROCESS pending no-roll hint was REMOVED in T-2026-06-19-04:
        // that mechanism proved DEAD (the no-roll signal is honored INLINE on the SessionStart's own binding
        // via the openSource argument to ResolveEffectiveSessionKey, so no cross-process scratch is needed).
        // Forward-only: the schema VERSION stays 13 and the rest of this migration body is unchanged, so a
        // live v13 db is never re-migrated and an already-created orphan hints table is harmless (purged
        // separately). A fresh db simply never creates the table; nothing references it.

        // Relax the Wave 1 (base, generation) UNIQUE index to NON-unique. Under compact/resume no-roll a
        // genuinely-new cc id legitimately binds to the CURRENT generation, so two (base, cc) rows now
        // share one generation/effective_key. The 1:1 "a generation maps to exactly one effective_key"
        // property is PRESERVED by construction (effective_key is derived as base + "#g" + generation),
        // but it is now a functional dependency, not a row-uniqueness constraint. Keep the index for the
        // MAX(generation) / current-generation lookups; DROP + recreate non-unique. Both are idempotent.
        try
        {
            using var dropIdx = connection.CreateCommand();
            dropIdx.CommandText = "DROP INDEX IF EXISTS idx_obssessgen_effective;";
            dropIdx.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        try
        {
            using var idx = connection.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssessgen_effective ON obs_session_generations(base_session_key, generation);";
            idx.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(13, "Session generation no-roll: (base, generation) index relaxed to non-unique for compact/resume multi-cc (the obs_session_generation_hints table this version originally created was removed as dead in T-2026-06-19-04; dropped from live dbs in v15)");

        MigrateToV14();
    }

    private void MigrateToV14()
    {
        // Tool-call status (T-2026-06-20-02, OBS-12): persist a machine-readable status for tool_call
        // events so the trace timeline can show success/failed instead of dropping the only signal the
        // host gives. Values written by the capture path (ClaudeCodeHookAdapter): 'success' (a
        // tool_response was present with no error shape), 'failed' (the tool_response carried an error
        // shape -- is_error / error / errorMessage / interrupted, or a Bash nonzero-exit shape), or NULL
        // when undeterminable (never invented). This is an acknowledged HEURISTIC: PostToolUse exposes no
        // explicit status field, so it is derived from the tool_response shape, and host-shape knowledge
        // stays in ClaudeCodeHookAdapter (platform-independence rule).
        //
        // NOTE on 'denied': a tool DENIAL is a human-intervention signal that belongs to the
        // user_submit / Notification path, NOT a tool status. The Notification hook exposes a pending
        // permission decision only as English prose (no structured approved/denied field), so 'denied'
        // is host-blocked today and is deliberately NOT a value this column ever takes. See
        // ClaudeCodeHookAdapter.ParseNotification for the documented gap.
        //
        // Additive, forward-only, no backfill: legacy event rows (and all non-tool_call event types)
        // stay NULL. ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_events ADD COLUMN status TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        InsertSchemaVersion(14, "Tool-call status (success/failed/NULL) on obs_agent_events (OBS-12; denied is a human-intervention signal, host-blocked, never a tool status)");

        MigrateToV15();
    }

    private void MigrateToV15()
    {
        // Drop the orphaned obs_session_generation_hints table (T-2026-06-20-20b). The table originally
        // backed a CROSS-PROCESS pending no-roll hint (v13) but that mechanism proved DEAD and its CREATE
        // was removed in T-2026-06-19-04 (the no-roll signal is honored INLINE on the SessionStart's own
        // binding via ResolveEffectiveSessionKey's openSource argument; no cross-process scratch is needed).
        // No INSERT and no SELECT reference it anywhere. Removing the CREATE stopped fresh dbs from making
        // it, but live dbs created at or before v13 still carry the orphan physically. This forward-only
        // migration purges it on next schema ensure. DROP TABLE IF EXISTS is idempotent: a fresh db that
        // never created the table is a no-op.
        try
        {
            using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = "DROP TABLE IF EXISTS obs_session_generation_hints;";
            dropCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* table already absent */ }

        InsertSchemaVersion(15, "Drop orphaned obs_session_generation_hints table (dead since T-2026-06-19-04; no reader/writer)");
        MigrateToV16();
    }

    private void MigrateToV16()
    {
        // Resume-stable + cross-machine conversation identity (T-2026-06-22-03):
        //
        // conversation_id: the Claude Code transcript filename stem. The transcript file persists across a
        // /resume -- the host session_id changes on resume, the transcript file does NOT -- so keying on
        // the stem makes the conversation identity stable across resumes. Falls back to the host session_id
        // when no transcript_path is available (Codex / non-CC hosts). Additive, nullable, set-once
        // (COALESCE in UpsertAgentSession), no backfill; legacy/cron/MCP rows stay NULL.
        //
        // machine_id: the locally-minted UUIDv7 from %APPDATA%/.scope/machine-id. Required for correct
        // cross-machine merge: two machines may independently use the same cc session_id stem (different
        // workspaces, forked repos, CI etc.). Bucketing on (machine_id, conversation_id) keeps them
        // separate. Additive, nullable, set-once, no backfill.
        //
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap each in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN conversation_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN machine_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_conversation ON obs_agent_sessions(conversation_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_machine ON obs_agent_sessions(machine_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        InsertSchemaVersion(16, "conversation_id + machine_id on obs_agent_sessions (resume-stable + cross-machine-safe conversation grouping)");
        MigrateToV17();
    }

    private void MigrateToV17()
    {
        // tool_use_id first-class column on obs_agent_events (T-2026-06-23-01 ST-01):
        // The tool_use_id is already embedded in event_data_json for tool_call events but
        // cannot be queried or indexed efficiently from there. Promoting it to a first-class
        // column enables:
        //   - Direct SQL join: obs_agent_events.tool_use_id -> wire_calls.tool_use_ids[]
        //   - O(1) index lookup instead of per-row JSON parse
        //   - BuildToolUseOwnerMap (ST-03) reads this column to build the attributor's input
        //
        // The column is populated on INSERT from the value the writer already has parsed.
        // Backfill: existing rows are updated from event_data_json using JSON_EXTRACT so
        // historical tool_call rows gain the column without re-indexing.
        //
        // Forward-only, nullable; rows with no tool_use_id in JSON stay NULL.
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_events ADD COLUMN tool_use_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obsagent_tooluse ON obs_agent_events(tool_use_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        // Backfill: for existing rows where tool_use_id IS NULL, extract it from event_data_json.
        // JSON_EXTRACT is idempotent and safe: rows where the JSON has no "tool_use_id" key stay NULL.
        // Only rows with event_data_json IS NOT NULL are touched (NULL json -> no parse attempt).
        try
        {
            using var backfillCmd = connection.CreateCommand();
            backfillCmd.CommandText = """
                UPDATE obs_agent_events
                SET tool_use_id = JSON_EXTRACT(event_data_json, '$.tool_use_id')
                WHERE tool_use_id IS NULL
                  AND event_data_json IS NOT NULL
                  AND JSON_EXTRACT(event_data_json, '$.tool_use_id') IS NOT NULL;
                """;
            backfillCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* best-effort backfill; forward writes always populate */ }

        InsertSchemaVersion(17, "tool_use_id first-class column + index on obs_agent_events (T-2026-06-23-01 ST-01; backfilled from event_data_json)");
        MigrateToV18();
    }

    private void MigrateToV18()
    {
        // Durable conversation identity (T-2026-06-23-01 ST-06): Scope-minted conversation id
        // that is stable across /resume (unlike the host cc session id which changes) and
        // cross-machine-safe (bucketed by machine_id so two machines never share a
        // scope_conversation_id even if their stems collide).
        //
        // obs_agent_sessions.scope_conversation_id: points each session row at the logical
        // conversation it belongs to. Nullable, additive, set-once (populated by ST-07 resolver).
        // idx_obssess_scopeconv: index for fast per-conversation session lookups.
        //
        // obs_conversations: one row per scope_conversation_id; records the machine it originated
        // on and when it was first created (minted by the ST-07 resolver on first sight of a stem).
        //
        // obs_conversation_bindings: the stem -> scope_conversation_id mapping. PRIMARY KEY on
        // (machine_id, stem) enforces first-insert-wins: only ONE scope_conversation_id is ever
        // bound to a (machine, stem) pair; a duplicate INSERT must fail (PK collision). The ST-07
        // resolver catches that collision and re-SELECTs the winner (copy BindGeneration pattern).
        //
        // ALTER TABLE lacks IF NOT EXISTS before SQLite 3.35, wrap in try/catch.
        try
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE obs_agent_sessions ADD COLUMN scope_conversation_id TEXT;";
            alterCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_obssess_scopeconv ON obs_agent_sessions(scope_conversation_id);";
            idxCmd.ExecuteNonQueryRetrying();
        }
        catch (SqliteException) { }

        using var tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS obs_conversations (
                scope_conversation_id TEXT PRIMARY KEY,
                machine_id            TEXT NOT NULL,
                created_at            TEXT NOT NULL) STRICT;

            CREATE TABLE IF NOT EXISTS obs_conversation_bindings (
                machine_id            TEXT NOT NULL,
                stem                  TEXT NOT NULL,
                scope_conversation_id TEXT NOT NULL,
                bound_at              TEXT NOT NULL,
                PRIMARY KEY (machine_id, stem)) STRICT;
            """;
        tablesCmd.ExecuteNonQueryRetrying();

        InsertSchemaVersion(18, "scope_conversation_id column + index on obs_agent_sessions; obs_conversations + obs_conversation_bindings tables (T-2026-06-23-01 ST-06)");
        MigrateToV19();
    }

    /// <summary>
    /// STRICT conversion (T-2026-07-12-01, hard rule owner 2026-07-12): recreates every obs_* table
    /// (obs_schema_versions, obs_requests, obs_request_stages, obs_retrieval_results,
    /// obs_packing_metrics, obs_cost_estimates, obs_agent_sessions, obs_agent_events, obs_payloads,
    /// obs_dispatches, obs_agent_spans, obs_session_generations, obs_conversations,
    /// obs_conversation_bindings) as STRICT, plus the legacy retrieval_requests/retrieval_results
    /// tables owned by <see cref="TelemetrySchemaManager"/> (which has no version ledger of its own, so
    /// this is the only place a pre-existing non-STRICT copy of those two tables is ever converted;
    /// <see cref="StrictTableMigrator.Recreate"/> no-ops when a table does not exist on this
    /// connection, since a pure-observability writer may never have run <see cref="TelemetrySchemaManager"/>).
    /// <para>
    /// Idempotency guard: this method is called UNCONDITIONALLY from the tail of every
    /// <see cref="MigrateToV18"/> call (the V2..V18 chain runs on every <see cref="EnsureSchema"/>
    /// invocation, i.e. on every telemetry write). <see cref="V19NeedsRecreate"/> probes all 16 table
    /// names with a cheap <c>PRAGMA table_list</c> read each (no DDL) and only opens the
    /// transaction/PRAGMA foreign_keys machinery when at least one of them still needs converting.
    /// A single-anchor check (e.g. "obs_requests alone") is UNSOUND here and was deliberately rejected:
    /// a partial/synthetic old-shape db (as several schema-migration unit tests build to simulate a
    /// pre-vN live db) can be missing a table this method treats as core -- that missing table gets
    /// created STRICT for the first time by the unconditional `CREATE TABLE IF NOT EXISTS ... STRICT`
    /// earlier in this same call, which would make a single-table anchor probe report "already
    /// migrated" while sibling tables that DID pre-exist (non-STRICT) are silently skipped. Checking
    /// every table's own existence+strict state removes that failure mode. This guard is still
    /// LOAD-BEARING for the expensive part: without it, every write would re-run the full multi-table
    /// recreate+copy (the per-table probes here are metadata-only and cheap by comparison, and
    /// <see cref="StrictTableMigrator.Recreate"/> repeats the same cheap per-table check internally, so
    /// this outer check is purely an optimization to skip opening the transaction at all).
    /// </para>
    /// <para>
    /// Retry posture: <see cref="StrictTableMigrator.Recreate"/> runs bare (non-retrying) statements;
    /// the WHOLE transaction is wrapped as a single <see cref="SqliteRetry"/> unit (not the individual
    /// statements) because a BUSY/LOCKED failure rolls the transaction back first, so retrying the
    /// entire sequence from scratch cannot double-apply. This is the same posture as
    /// <see cref="WireCallSchemaManager"/>'s v4 STRICT step and <see cref="CronSchemaManager"/>'s v8
    /// STRICT step.
    /// </para>
    /// </summary>
    private void MigrateToV19()
    {
        if (V19NeedsRecreate())
        {
            using (var offCmd = connection.CreateCommand())
            {
                offCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                offCmd.ExecuteNonQueryRetrying();
            }

            try
            {
                SqliteRetry.Execute(RunV19RecreateTransaction);
            }
            finally
            {
                using var onCmd = connection.CreateCommand();
                onCmd.CommandText = "PRAGMA foreign_keys = ON;";
                onCmd.ExecuteNonQueryRetrying();
            }
        }

        InsertSchemaVersion(19, "STRICT conversion: all obs_* tables plus retrieval_requests/retrieval_results recreated STRICT (T-2026-07-12-01)");
    }

    /// <summary>Every table touched by the v19 STRICT recreate (see <see cref="RunV19RecreateTransaction"/>).</summary>
    private static readonly string[] V19Tables =
    [
        "obs_schema_versions", "obs_requests", "obs_request_stages", "obs_retrieval_results",
        "obs_packing_metrics", "obs_cost_estimates", "obs_agent_sessions", "obs_agent_events",
        "obs_payloads", "obs_dispatches", "obs_agent_spans", "obs_session_generations",
        "obs_conversations", "obs_conversation_bindings", "retrieval_requests", "retrieval_results",
    ];

    /// <summary>
    /// Returns true when at least one of <see cref="V19Tables"/> exists but is not yet STRICT. See the
    /// idempotency-guard remarks on <see cref="MigrateToV19"/> for why this checks every table instead
    /// of a single anchor.
    /// </summary>
    private bool V19NeedsRecreate()
    {
        foreach (var table in V19Tables)
        {
            if (StrictTableMigrator.TableExists(connection, table) && !StrictTableMigrator.IsStrict(connection, table))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Runs the v19 multi-table recreate inside one BEGIN/COMMIT transaction. Every table is
    /// individually guarded by <see cref="StrictTableMigrator.Recreate"/> (no-op if already STRICT or
    /// absent), so calling this on a partially-migrated db only touches what still needs converting.
    /// All 16 NULL-PK-checked tables here are best-effort observability/retrieval telemetry (never
    /// cron_runs/wire_calls), so every one uses <see cref="NullPkPolicy.SkipAndWarn"/>: a NULL-PK row
    /// is already unreadable by the app, and this runs on the write path, so it must never hard-throw.
    /// </summary>
    private void RunV19RecreateTransaction()
    {
        using (var beginCmd = connection.CreateCommand())
        {
            beginCmd.CommandText = "BEGIN TRANSACTION;";
            beginCmd.ExecuteNonQuery();
        }

        try
        {
            StrictTableMigrator.Recreate(connection, "obs_schema_versions",
                """
                CREATE TABLE obs_schema_versions_new (
                    version     INTEGER PRIMARY KEY,
                    applied_at  TEXT NOT NULL,
                    description TEXT NOT NULL
                ) STRICT;
                """,
                "version, applied_at, description", pkColumn: null, NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_requests",
                """
                CREATE TABLE obs_requests_new (
                    id                TEXT PRIMARY KEY,
                    parent_request_id TEXT,
                    caller_surface    TEXT NOT NULL,
                    operation         TEXT NOT NULL,
                    agent_session_key TEXT,
                    query_text        TEXT,
                    parameters_json   TEXT,
                    status            TEXT NOT NULL DEFAULT 'started',
                    exit_code         INTEGER,
                    error_message     TEXT,
                    total_elapsed_ms  INTEGER,
                    created_at        TEXT NOT NULL,
                    completed_at      TEXT,
                    dispatch_id       TEXT
                ) STRICT;
                """,
                """
                id, parent_request_id, caller_surface, operation, agent_session_key, query_text,
                parameters_json, status, exit_code, error_message, total_elapsed_ms, created_at,
                completed_at, dispatch_id
                """,
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_request_stages",
                """
                CREATE TABLE obs_request_stages_new (
                    id            TEXT PRIMARY KEY,
                    request_id    TEXT NOT NULL REFERENCES obs_requests(id),
                    stage_name    TEXT NOT NULL,
                    stage_order   INTEGER NOT NULL,
                    elapsed_ms    INTEGER NOT NULL,
                    metadata_json TEXT,
                    created_at    TEXT NOT NULL
                ) STRICT;
                """,
                "id, request_id, stage_name, stage_order, elapsed_ms, metadata_json, created_at",
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_retrieval_results",
                """
                CREATE TABLE obs_retrieval_results_new (
                    id              TEXT PRIMARY KEY,
                    request_id      TEXT NOT NULL REFERENCES obs_requests(id),
                    document_key    TEXT NOT NULL,
                    rank            INTEGER NOT NULL,
                    score           REAL NOT NULL,
                    provider_source TEXT NOT NULL,
                    is_exact_match  INTEGER NOT NULL DEFAULT 0,
                    boosts_applied  TEXT NOT NULL DEFAULT '',
                    created_at      TEXT NOT NULL
                ) STRICT;
                """,
                """
                id, request_id, document_key, rank, score, provider_source, is_exact_match,
                boosts_applied, created_at
                """,
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_packing_metrics",
                """
                CREATE TABLE obs_packing_metrics_new (
                    id                   TEXT PRIMARY KEY,
                    request_id           TEXT NOT NULL REFERENCES obs_requests(id),
                    target_fqn           TEXT NOT NULL,
                    budget_requested     INTEGER NOT NULL,
                    budget_used          INTEGER NOT NULL,
                    budget_remaining     INTEGER NOT NULL,
                    symbols_considered   INTEGER NOT NULL,
                    symbols_included     INTEGER NOT NULL,
                    symbols_rejected     INTEGER NOT NULL,
                    compression_ct       INTEGER NOT NULL,
                    full_body_ct         INTEGER NOT NULL,
                    signature_only_ct    INTEGER NOT NULL,
                    call_site_ct         INTEGER NOT NULL,
                    dedup_removed_ct     INTEGER NOT NULL,
                    free_symbol_ct       INTEGER NOT NULL,
                    test_callers_capped  INTEGER NOT NULL DEFAULT 0,
                    target_token_count   INTEGER NOT NULL,
                    context_token_count  INTEGER NOT NULL,
                    created_at           TEXT NOT NULL
                ) STRICT;
                """,
                """
                id, request_id, target_fqn, budget_requested, budget_used, budget_remaining,
                symbols_considered, symbols_included, symbols_rejected, compression_ct, full_body_ct,
                signature_only_ct, call_site_ct, dedup_removed_ct, free_symbol_ct, test_callers_capped,
                target_token_count, context_token_count, created_at
                """,
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_cost_estimates",
                """
                CREATE TABLE obs_cost_estimates_new (
                    id                      TEXT PRIMARY KEY,
                    request_id              TEXT NOT NULL REFERENCES obs_requests(id),
                    model_id                TEXT NOT NULL,
                    provider                TEXT NOT NULL,
                    input_tokens            INTEGER NOT NULL,
                    estimated_output_tokens INTEGER NOT NULL,
                    input_cost_usd          REAL NOT NULL,
                    output_cost_usd         REAL NOT NULL,
                    total_cost_usd          REAL NOT NULL,
                    created_at              TEXT NOT NULL
                ) STRICT;
                """,
                """
                id, request_id, model_id, provider, input_tokens, estimated_output_tokens,
                input_cost_usd, output_cost_usd, total_cost_usd, created_at
                """,
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_agent_sessions",
                """
                CREATE TABLE obs_agent_sessions_new (
                    session_key           TEXT PRIMARY KEY,
                    agent_type            TEXT NOT NULL,
                    agent_subtype         TEXT NOT NULL,
                    first_seen_at         TEXT NOT NULL,
                    last_seen_at          TEXT NOT NULL,
                    request_count         INTEGER NOT NULL DEFAULT 0,
                    parent_session_key    TEXT,
                    origin                TEXT,
                    deleted_at            TEXT,
                    base_session_key      TEXT,
                    generation            INTEGER,
                    conversation_id       TEXT,
                    machine_id            TEXT,
                    cc_session_id         TEXT,
                    scope_conversation_id TEXT
                ) STRICT;
                """,
                """
                session_key, agent_type, agent_subtype, first_seen_at, last_seen_at, request_count,
                parent_session_key, origin, deleted_at, base_session_key, generation, conversation_id,
                machine_id, cc_session_id, scope_conversation_id
                """,
                pkColumn: "session_key", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_agent_events",
                """
                CREATE TABLE obs_agent_events_new (
                    id              TEXT PRIMARY KEY,
                    session_key     TEXT NOT NULL REFERENCES obs_agent_sessions(session_key),
                    request_id      TEXT REFERENCES obs_requests(id),
                    event_type      TEXT NOT NULL,
                    event_data_json TEXT,
                    created_at      TEXT NOT NULL,
                    span_id         TEXT,
                    event_subtype   TEXT,
                    content_hash    TEXT,
                    turn_index      INTEGER,
                    status          TEXT,
                    tool_use_id     TEXT
                ) STRICT;
                """,
                """
                id, session_key, request_id, event_type, event_data_json, created_at, span_id,
                event_subtype, content_hash, turn_index, status, tool_use_id
                """,
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_payloads",
                """
                CREATE TABLE obs_payloads_new (
                    id           TEXT PRIMARY KEY,
                    request_id   TEXT NOT NULL REFERENCES obs_requests(id),
                    direction    TEXT NOT NULL CHECK(direction IN ('inbound','outbound')),
                    content_type TEXT NOT NULL,
                    payload_text TEXT NOT NULL,
                    token_count  INTEGER,
                    created_at   TEXT NOT NULL
                ) STRICT;
                """,
                "id, request_id, direction, content_type, payload_text, token_count, created_at",
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_dispatches",
                """
                CREATE TABLE obs_dispatches_new (
                    id           TEXT PRIMARY KEY,
                    session_key  TEXT,
                    label        TEXT,
                    created_at   TEXT NOT NULL,
                    completed_at TEXT
                ) STRICT;
                """,
                "id, session_key, label, created_at, completed_at",
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_agent_spans",
                """
                CREATE TABLE obs_agent_spans_new (
                    id                    TEXT PRIMARY KEY,
                    session_key           TEXT NOT NULL,
                    dispatch_id           TEXT,
                    parent_span_id        TEXT,
                    agent_type            TEXT NOT NULL,
                    agent_name            TEXT NOT NULL,
                    started_at            TEXT NOT NULL,
                    completed_at          TEXT,
                    input_summary         TEXT,
                    output_summary        TEXT,
                    status                TEXT NOT NULL DEFAULT 'active',
                    agent_ref             TEXT,
                    total_tokens          INTEGER,
                    input_tokens          INTEGER,
                    output_tokens         INTEGER,
                    pinned_cost_usd       REAL,
                    pinned_model_id       TEXT,
                    pinned_prices_as_of   TEXT,
                    pinned_breakdown_json TEXT,
                    model                 TEXT
                ) STRICT;
                """,
                """
                id, session_key, dispatch_id, parent_span_id, agent_type, agent_name, started_at,
                completed_at, input_summary, output_summary, status, agent_ref, total_tokens,
                input_tokens, output_tokens, pinned_cost_usd, pinned_model_id, pinned_prices_as_of,
                pinned_breakdown_json, model
                """,
                pkColumn: "id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_session_generations",
                """
                CREATE TABLE obs_session_generations_new (
                    base_session_key TEXT NOT NULL,
                    cc_session_id    TEXT NOT NULL,
                    generation       INTEGER NOT NULL,
                    effective_key    TEXT NOT NULL,
                    opened_at        TEXT NOT NULL,
                    open_source      TEXT,
                    PRIMARY KEY (base_session_key, cc_session_id)
                ) STRICT;
                """,
                "base_session_key, cc_session_id, generation, effective_key, opened_at, open_source",
                pkColumn: null, NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_conversations",
                """
                CREATE TABLE obs_conversations_new (
                    scope_conversation_id TEXT PRIMARY KEY,
                    machine_id            TEXT NOT NULL,
                    created_at            TEXT NOT NULL
                ) STRICT;
                """,
                "scope_conversation_id, machine_id, created_at",
                pkColumn: "scope_conversation_id", NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "obs_conversation_bindings",
                """
                CREATE TABLE obs_conversation_bindings_new (
                    machine_id            TEXT NOT NULL,
                    stem                  TEXT NOT NULL,
                    scope_conversation_id TEXT NOT NULL,
                    bound_at              TEXT NOT NULL,
                    PRIMARY KEY (machine_id, stem)
                ) STRICT;
                """,
                "machine_id, stem, scope_conversation_id, bound_at",
                pkColumn: null, NullPkPolicy.SkipAndWarn);

            // Legacy tables owned by TelemetrySchemaManager, which has no version ledger of its own.
            // Recreate.no-ops when the table does not exist on this connection (a pure-observability
            // writer may never have run TelemetrySchemaManager here).
            StrictTableMigrator.Recreate(connection, "retrieval_requests",
                """
                CREATE TABLE retrieval_requests_new (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    query_text       TEXT NOT NULL,
                    mode             TEXT NOT NULL CHECK(mode IN ('Lexical','Semantic','Hybrid')),
                    providers_used   TEXT NOT NULL,
                    result_count     INTEGER NOT NULL,
                    total_candidates INTEGER NOT NULL,
                    latency_ms       INTEGER NOT NULL,
                    caller_surface   TEXT NOT NULL CHECK(caller_surface IN ('Cli','Mcp','Server','Test','Benchmark')),
                    created_at       TEXT NOT NULL
                ) STRICT;
                """,
                """
                id, query_text, mode, providers_used, result_count, total_candidates, latency_ms,
                caller_surface, created_at
                """,
                pkColumn: null, NullPkPolicy.SkipAndWarn);

            StrictTableMigrator.Recreate(connection, "retrieval_results",
                """
                CREATE TABLE retrieval_results_new (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    request_id      INTEGER NOT NULL REFERENCES retrieval_requests(id) ON DELETE CASCADE,
                    document_key    TEXT NOT NULL,
                    rank            INTEGER NOT NULL,
                    score           REAL NOT NULL,
                    provider_source TEXT NOT NULL CHECK(provider_source IN ('Lexical','Semantic','Hybrid')),
                    is_exact_match  INTEGER NOT NULL DEFAULT 0,
                    boosts_applied  TEXT NOT NULL DEFAULT '',
                    created_at      TEXT NOT NULL
                ) STRICT;
                """,
                """
                id, request_id, document_key, rank, score, provider_source, is_exact_match,
                boosts_applied, created_at
                """,
                pkColumn: null, NullPkPolicy.SkipAndWarn);

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
    }

    private void InsertSchemaVersion(int version, string description)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO obs_schema_versions (version, applied_at, description)
            VALUES ($version, $appliedAt, $description);
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$description", description);
        cmd.ExecuteNonQueryRetrying();
    }
}

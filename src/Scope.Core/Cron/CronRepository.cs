using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;
using System.Globalization;

namespace Scope.Core.Cron;

/// <summary>
/// SQLite repository for cron-agent definitions and run records.
/// </summary>
public sealed class CronRepository(SqliteConnection connection)
{
    public void EnsureSchema() => new CronSchemaManager(connection).EnsureSchema();

    public void InsertAgent(CronAgentDefinition agent)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cron_agents (
                id, label, schedule, prompt, repo_path, harness, model, token_budget,
                enabled, created_at, updated_at, block_concurrent_runs, deleted_at, max_runtime_seconds)
            VALUES (
                $id, $label, $schedule, $prompt, $repoPath, $harness, $model, $tokenBudget,
                $enabled, $createdAt, $updatedAt, $blockConcurrentRuns, $deletedAt, $maxRuntimeSeconds);
            """;
        AddAgentParameters(cmd, agent);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CronAgentDefinition> ListAgents()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, schedule, prompt, repo_path, harness, model, token_budget,
                   enabled, created_at, updated_at, block_concurrent_runs, deleted_at, max_runtime_seconds
            FROM cron_agents
            WHERE deleted_at IS NULL
            ORDER BY label COLLATE NOCASE;
            """;

        using var reader = cmd.ExecuteReader();
        var agents = new List<CronAgentDefinition>();
        while (reader.Read())
            agents.Add(ReadAgent(reader));
        return agents;
    }

    public CronAgentDefinition? GetAgentByLabel(string label)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, schedule, prompt, repo_path, harness, model, token_budget,
                   enabled, created_at, updated_at, block_concurrent_runs, deleted_at, max_runtime_seconds
            FROM cron_agents
            WHERE label = $label COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$label", label);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAgent(reader) : null;
    }

    public int CountEnabledAgents()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cron_agents WHERE enabled = 1 AND deleted_at IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Soft-deletes the agent with the given label by setting <c>deleted_at</c> to now. The agent
    /// row and ALL of its cron_runs (including immutable financial fields: total_cost_usd,
    /// cost_provenance, session_key, timestamps) are preserved in perpetuity. No cron_runs row is
    /// ever deleted by this path, and the v5 FK RESTRICT prevents any cascade. Returns true when a
    /// row was updated (an already-deleted or missing label returns false). Existing remove-semantics
    /// (true on success, false on not-found) are preserved.
    /// </summary>
    public bool RemoveAgentByLabel(string label)
    {
        var agent = GetAgentByLabel(label);
        if (agent is null || agent.DeletedAt is not null)
            return false;

        using var softDelete = connection.CreateCommand();
        softDelete.CommandText = "UPDATE cron_agents SET deleted_at = $deletedAt WHERE id = $id;";
        softDelete.Parameters.AddWithValue("$deletedAt", DateTime.UtcNow.ToString("o"));
        softDelete.Parameters.AddWithValue("$id", agent.Id);
        return softDelete.ExecuteNonQuery() > 0;
    }

    public void InsertRun(CronRunRecord run)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cron_runs (
                id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance)
            VALUES (
                $id, $cronAgentId, $dispatchId, $sessionKey, $startedAt, $completedAt,
                $status, $exitCode, $totalCostUsd, $skipReason, $errorMessage, $costProvenance);
            """;
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$cronAgentId", run.CronAgentId);
        cmd.Parameters.AddWithValue("$dispatchId", (object?)run.DispatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sessionKey", (object?)run.SessionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startedAt", run.StartedAt);
        cmd.Parameters.AddWithValue("$completedAt", (object?)run.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", run.Status);
        cmd.Parameters.AddWithValue("$exitCode", (object?)run.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$totalCostUsd", (object?)run.TotalCostUsd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$skipReason", (object?)run.SkipReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorMessage", (object?)run.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$costProvenance", (object?)run.CostProvenance ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the most recent in-flight run (status 'running') for a definition, or null when
    /// none is in flight. Used by the run-now concurrency guard to avoid launching a concurrent
    /// copy of the same definition.
    /// </summary>
    public CronRunRecord? GetRunningRunForAgent(string cronAgentId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                   status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance
            FROM cron_runs
            WHERE cron_agent_id = $cronAgentId AND status = $running
            ORDER BY started_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$cronAgentId", cronAgentId);
        cmd.Parameters.AddWithValue("$running", CronRunStatuses.Running);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    /// <summary>
    /// Transitions a run row to a terminal state (completed/failed), setting exit code,
    /// completion timestamp, cost, cost provenance, and error message. Returns true when a row was updated.
    /// </summary>
    public bool UpdateRunCompletion(
        string runId,
        string status,
        int? exitCode,
        string completedAt,
        double? totalCostUsd,
        string? errorMessage,
        string? costProvenance = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE cron_runs
            SET status = $status,
                exit_code = $exitCode,
                completed_at = $completedAt,
                total_cost_usd = $totalCostUsd,
                error_message = $errorMessage,
                cost_provenance = $costProvenance
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$exitCode", (object?)exitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completedAt", completedAt);
        cmd.Parameters.AddWithValue("$totalCostUsd", (object?)totalCostUsd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$costProvenance", (object?)costProvenance ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Reads a single run row by id, or null when it does not exist.</summary>
    public CronRunRecord? GetRunById(string runId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                   status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance
            FROM cron_runs
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", runId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    /// <summary>
    /// Updates the enabled flag and updated_at timestamp for the agent with the given label.
    /// Returns true when a row was changed, false when the label was not found.
    /// </summary>
    public bool SetAgentEnabled(string label, bool enabled)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE cron_agents
            SET enabled = $enabled, updated_at = $updatedAt
            WHERE label = $label COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$label", label);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Full-replace update of an existing cron-agent's editable fields (T-2026-06-17-05). Updates
    /// schedule, prompt, repo_path, harness, model, token_budget, block_concurrent_runs,
    /// max_runtime_seconds, and updated_at on the row whose id matches <paramref name="agent"/>.Id and
    /// that is NOT soft-deleted (<c>deleted_at IS NULL</c>). The label, id, created_at, and enabled
    /// columns are deliberately NOT in the SET list: label/id are identity, created_at is immutable,
    /// and enabled is owned by <see cref="SetAgentEnabled"/>. No cron_runs row is touched. Returns true
    /// when exactly the one active row was updated; false when no active row matched (missing or
    /// soft-deleted), which the service maps to a 404.
    /// </summary>
    public bool UpdateAgent(CronAgentDefinition agent)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE cron_agents
            SET schedule = $schedule,
                prompt = $prompt,
                repo_path = $repoPath,
                harness = $harness,
                model = $model,
                token_budget = $tokenBudget,
                block_concurrent_runs = $blockConcurrentRuns,
                max_runtime_seconds = $maxRuntimeSeconds,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", agent.Id);
        cmd.Parameters.AddWithValue("$schedule", agent.Schedule);
        cmd.Parameters.AddWithValue("$prompt", agent.Prompt);
        cmd.Parameters.AddWithValue("$repoPath", agent.RepoPath);
        cmd.Parameters.AddWithValue("$harness", agent.Harness);
        cmd.Parameters.AddWithValue("$model", (object?)agent.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tokenBudget", (object?)agent.TokenBudget ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$blockConcurrentRuns", agent.BlockConcurrentRuns ? 1 : 0);
        cmd.Parameters.AddWithValue("$maxRuntimeSeconds", (object?)agent.MaxRuntimeSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAt", agent.UpdatedAt);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Marks all 'running' rows that started more than <paramref name="maxAge"/> ago as 'failed'
    /// with an honest error message and sets completed_at to now. Returns the count of rows reaped.
    /// </summary>
    public int ReapStaleRunningRows(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge).ToString("o");
        var now = DateTime.UtcNow.ToString("o");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE cron_runs
            SET status = 'failed',
                completed_at = $now,
                error_message = 'reaped: run exceeded max running duration; presumed dead'
            WHERE status = 'running' AND started_at < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the most-recent run summary for EVERY agent in a single query (one row per
    /// agent id, null fields when the agent has never run). Uses a correlated MAX(started_at)
    /// sub-select so the query is a single round-trip with no N+1 reads.
    /// </summary>
    public IReadOnlyList<CronAgentLastRunSummary> GetLatestRunPerAgent()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                a.id AS cron_agent_id,
                r.status,
                r.started_at,
                r.total_cost_usd,
                r.cost_provenance
            FROM cron_agents a
            LEFT JOIN cron_runs r
                ON r.cron_agent_id = a.id
                AND r.started_at = (
                    SELECT MAX(r2.started_at)
                    FROM cron_runs r2
                    WHERE r2.cron_agent_id = a.id
                )
            WHERE a.deleted_at IS NULL;
            """;

        using var reader = cmd.ExecuteReader();
        var results = new List<CronAgentLastRunSummary>();
        while (reader.Read())
        {
            results.Add(new CronAgentLastRunSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return results;
    }

    public IReadOnlyList<CronRunRecord> ListRunsForAgent(string cronAgentId, int limit = 50)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, cron_agent_id, dispatch_id, session_key, started_at, completed_at,
                   status, exit_code, total_cost_usd, skip_reason, error_message, cost_provenance
            FROM cron_runs
            WHERE cron_agent_id = $cronAgentId
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cronAgentId", cronAgentId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        using var reader = cmd.ExecuteReader();
        var runs = new List<CronRunRecord>();
        while (reader.Read())
            runs.Add(ReadRun(reader));
        return runs;
    }

    /// <summary>
    /// Lists cron runs across ALL agents (T-2026-06-15-16/ST-02), most-recent first. Each row is a
    /// cron_runs row joined to its cron_agents row for the human label and schedule (both in scope.db,
    /// one query, no N+1). Read-only: no run/agent rows are written. <paramref name="from"/>/
    /// <paramref name="to"/> are optional ISO-8601 UTC bounds on started_at (either independent).
    /// <paramref name="limit"/> is clamped to [1, 500].
    /// </summary>
    public IReadOnlyList<CronSessionRow> ListCronSessions(int limit = 50, string? from = null, string? to = null)
    {
        using var cmd = connection.CreateCommand();
        var conditions = new List<string>();
        if (from != null)
        {
            conditions.Add("r.started_at >= $from");
            cmd.Parameters.AddWithValue("$from", from);
        }
        if (to != null)
        {
            conditions.Add("r.started_at <= $to");
            cmd.Parameters.AddWithValue("$to", to);
        }
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        cmd.CommandText = $"""
            SELECT r.id, r.session_key, a.label, a.schedule, r.status,
                   r.total_cost_usd, r.started_at, r.completed_at, r.cost_provenance
            FROM cron_runs r
            JOIN cron_agents a ON a.id = r.cron_agent_id
            {where}
            ORDER BY r.started_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        using var reader = cmd.ExecuteReader();
        var rows = new List<CronSessionRow>();
        while (reader.Read())
        {
            rows.Add(new CronSessionRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return rows;
    }

    private static void AddAgentParameters(SqliteCommand cmd, CronAgentDefinition agent)
    {
        cmd.Parameters.AddWithValue("$id", agent.Id);
        cmd.Parameters.AddWithValue("$label", agent.Label);
        cmd.Parameters.AddWithValue("$schedule", agent.Schedule);
        cmd.Parameters.AddWithValue("$prompt", agent.Prompt);
        cmd.Parameters.AddWithValue("$repoPath", agent.RepoPath);
        cmd.Parameters.AddWithValue("$harness", agent.Harness);
        cmd.Parameters.AddWithValue("$model", (object?)agent.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tokenBudget", (object?)agent.TokenBudget ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", agent.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", agent.CreatedAt);
        cmd.Parameters.AddWithValue("$updatedAt", agent.UpdatedAt);
        cmd.Parameters.AddWithValue("$blockConcurrentRuns", agent.BlockConcurrentRuns ? 1 : 0);
        // deleted_at: NULL for new inserts (a freshly created agent is always active).
        cmd.Parameters.AddWithValue("$deletedAt", (object?)agent.DeletedAt ?? DBNull.Value);
        // max_runtime_seconds: NULL = fall back to the global supervisor default (schema v6).
        cmd.Parameters.AddWithValue("$maxRuntimeSeconds", (object?)agent.MaxRuntimeSeconds ?? DBNull.Value);
    }

    private static CronAgentDefinition ReadAgent(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.GetInt32(8) == 1,
            reader.GetString(9),
            reader.GetString(10),
            // block_concurrent_runs: column 11, added in schema v4; defaults to true for rows
            // pre-dating the column (the ALTER TABLE DEFAULT 1 ensures the DB returns 1 for them).
            reader.FieldCount > 11 ? reader.GetInt32(11) == 1 : true,
            // deleted_at: column 12, added in schema v5; NULL = active. Absent on selects that do
            // not project the column (FieldCount guard) and on rows pre-dating the column.
            reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null,
            // max_runtime_seconds: column 13, added in schema v6; NULL = use global default. Absent on
            // selects that do not project the column (FieldCount guard) and on rows pre-dating it.
            reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetInt32(13) : null);

    private static CronRunRecord ReadRun(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetDouble(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            // cost_provenance: column 11, added in schema v3; null for historical rows -> 'unavailable' at DTO level.
            reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null);
}

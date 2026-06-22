namespace Scope.Core.Telemetry;

using Microsoft.Data.Sqlite;
using Scope.Core.Cron.Run;
using Serilog;
using System.Globalization;

/// <summary>
/// Parameterizable cost-aggregation helper for telemetry.db obs_* tables.
///
/// This lane (T-36a) only exercises the compact 7-day default (window=7d, groupBy=CronAgent).
/// The helper is designed so the future Cost Ledger explorer (T-36b) can pass arbitrary
/// window / granularity / groupBy without restructuring this class.
///
/// Extension points:
///   - <see cref="CostGroupBy"/>: add new grouping dimensions (model, tool_bucket).
///   - <see cref="CostWindow"/>: add custom DateTimeOffset range when the predefined spans
///     are insufficient (the factory method <see cref="CostWindow.Custom"/> handles this).
///   - Granularity time-series: the <see cref="Aggregate"/> signature includes a
///     <c>granularityDays</c> parameter; set to 0 for the flat aggregate (default).
///     T-36b can pass 1 (daily), 7 (weekly), etc. and interpret the bucket field.
/// </summary>
public static class CostAggregator
{
    /// <summary>Grouping dimension for the top-driver breakdown.</summary>
    public enum CostGroupBy
    {
        /// <summary>Group by cron agent label (default for T-36a).</summary>
        CronAgent,
        /// <summary>Group by model id (future T-36b).</summary>
        Model,
        /// <summary>Group by tool bucket taxonomy (future T-36b).</summary>
        ToolBucket,
    }

    /// <summary>
    /// Describes the time window over which to aggregate costs.
    /// Predefined spans: 1/7/30/90/180/365 days. Custom range available via <see cref="Custom"/>.
    /// </summary>
    public sealed record CostWindow(DateTimeOffset From, DateTimeOffset To)
    {
        /// <summary>7-day rolling window ending now (default for T-36a).</summary>
        public static CostWindow Last7Days() => Rolling(7);

        /// <summary>Rolling window of the given number of days ending now.</summary>
        public static CostWindow Rolling(int days)
        {
            var now = DateTimeOffset.UtcNow;
            return new CostWindow(now.AddDays(-days), now);
        }

        /// <summary>Arbitrary custom range for T-36b cost explorer.</summary>
        public static CostWindow Custom(DateTimeOffset from, DateTimeOffset to)
            => new(from, to);

        /// <summary>Window length in fractional days.</summary>
        public int ApproximateDays => (int)Math.Round((To - From).TotalDays);
    }

    /// <summary>
    /// One aggregated group row.
    /// </summary>
    public sealed record CostGroup(
        string GroupKey,
        double TotalSpendUsd,
        int RowCount);

    /// <summary>
    /// Result of a cost aggregation pass.
    /// </summary>
    public sealed record CostAggregateResult(
        double TotalSpendUsd,
        string Provenance,
        IReadOnlyList<CostGroup> Groups);

    /// <summary>
    /// Aggregates cost from obs_cost_estimates + obs_requests within the given window,
    /// grouped by the selected dimension. Returns honest "unavailable" when telemetry
    /// is absent or the query fails — never throws.
    ///
    /// <paramref name="granularityDays"/> is reserved for T-36b time-series use (0 = flat aggregate).
    /// </summary>
    public static CostAggregateResult Aggregate(
        SqliteConnection connection,
        CostWindow window,
        CostGroupBy groupBy = CostGroupBy.CronAgent,
        int granularityDays = 0,
        int? limit = 20)
    {
        _ = granularityDays; // reserved for T-36b time-series bucketing

        try
        {
            if (!HasObsSchema(connection))
                return Unavailable();

            var fromStr = window.From.UtcDateTime.ToString("o");
            var toStr = window.To.UtcDateTime.ToString("o");

            // Flat aggregate: total spend in window.
            double totalSpend;
            try
            {
                using var totalCmd = connection.CreateCommand();
                totalCmd.CommandText = """
                    SELECT COALESCE(SUM(c.total_cost_usd), 0)
                    FROM obs_cost_estimates c
                    JOIN obs_requests r ON r.id = c.request_id
                    WHERE c.created_at >= $from AND c.created_at <= $to;
                    """;
                totalCmd.Parameters.AddWithValue("$from", fromStr);
                totalCmd.Parameters.AddWithValue("$to", toStr);
                totalSpend = Convert.ToDouble(totalCmd.ExecuteScalar() ?? 0.0, CultureInfo.InvariantCulture);
            }
            catch (SqliteException)
            {
                return Unavailable();
            }

            // Per-group breakdown.
            var groups = groupBy switch
            {
                CostGroupBy.CronAgent => AggregateByAgent(connection, fromStr, toStr, limit),
                CostGroupBy.Model => AggregateByModel(connection, fromStr, toStr, limit),
                _ => [],
            };

            // Provenance: if any cost row exists it came from obs_cost_estimates (scope_estimated).
            // If wire_calls exist and were joined, provenance would be wire_exact/joined_exact.
            // For this aggregate we only have obs_cost_estimates rows (scope_estimated path).
            var provenance = totalSpend > 0
                ? CostProvenance.ScopeEstimated
                : CostProvenance.Unavailable;

            return new CostAggregateResult(totalSpend, provenance, groups);
        }
        catch (Exception ex)
        {
            Log.Warning("CostAggregator.Aggregate failed (non-fatal): {Message}", ex.Message);
            return Unavailable();
        }
    }

    private static List<CostGroup> AggregateByAgent(
        SqliteConnection connection, string fromStr, string toStr, int? limit)
    {
        try
        {
            // Group by agent_session_key (closest proxy for cron agent without a DB join to cron_runs).
            // The session key for a cron run is formatted as "cron-{label}-{runId}" or similar;
            // for now we group by the session key and label it as the driver key.
            // limit == null -> full list (cost ledger); limit == N -> top-N (compact summary).
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(r.agent_session_key, 'unattributed') AS group_key,
                       COALESCE(SUM(c.total_cost_usd), 0) AS spend,
                       COUNT(*) AS row_count
                FROM obs_cost_estimates c
                JOIN obs_requests r ON r.id = c.request_id
                WHERE c.created_at >= $from AND c.created_at <= $to
                GROUP BY group_key
                ORDER BY spend DESC
                """ + (limit.HasValue ? $"\nLIMIT {limit.Value};" : ";");
            cmd.Parameters.AddWithValue("$from", fromStr);
            cmd.Parameters.AddWithValue("$to", toStr);

            var groups = new List<CostGroup>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.IsDBNull(0) ? "unattributed" : reader.GetString(0);
                var spend = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                var count = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2);
                groups.Add(new CostGroup(key, Math.Round(spend, 6), count));
            }
            return groups;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private static List<CostGroup> AggregateByModel(
        SqliteConnection connection, string fromStr, string toStr, int? limit)
    {
        try
        {
            // limit == null -> full list; limit == N -> top-N.
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(c.model_id, 'unknown') AS group_key,
                       COALESCE(SUM(c.total_cost_usd), 0) AS spend,
                       COUNT(*) AS row_count
                FROM obs_cost_estimates c
                JOIN obs_requests r ON r.id = c.request_id
                WHERE c.created_at >= $from AND c.created_at <= $to
                GROUP BY group_key
                ORDER BY spend DESC
                """ + (limit.HasValue ? $"\nLIMIT {limit.Value};" : ";");
            cmd.Parameters.AddWithValue("$from", fromStr);
            cmd.Parameters.AddWithValue("$to", toStr);

            var groups = new List<CostGroup>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                var spend = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                var count = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2);
                groups.Add(new CostGroup(key, Math.Round(spend, 6), count));
            }
            return groups;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>
    /// Aggregates cost per provenance category within the window.
    /// Returns one row per distinct provenance string found in obs_cost_estimates.created_at
    /// (all rows currently carry the same scope_estimated path, but the query is forward-compatible
    /// with richer provenance columns if added later).
    /// </summary>
    public static IReadOnlyList<ProvenanceGroup> AggregateByProvenance(
        SqliteConnection connection, CostWindow window)
    {
        try
        {
            if (!HasObsSchema(connection)) return [];

            var fromStr = window.From.UtcDateTime.ToString("o");
            var toStr = window.To.UtcDateTime.ToString("o");

            using var cmd = connection.CreateCommand();
            // obs_cost_estimates does not carry a provenance column (rows are scope_estimated by
            // construction), so we emit a fixed 'scope_estimated' label per row.
            // When the schema gains a provenance column this query should be updated.
            cmd.CommandText = """
                SELECT 'scope_estimated' AS provenance,
                       COALESCE(SUM(c.total_cost_usd), 0) AS spend,
                       COUNT(*) AS row_count
                FROM obs_cost_estimates c
                JOIN obs_requests r ON r.id = c.request_id
                WHERE c.created_at >= $from AND c.created_at <= $to;
                """;
            cmd.Parameters.AddWithValue("$from", fromStr);
            cmd.Parameters.AddWithValue("$to", toStr);

            var rows = new List<ProvenanceGroup>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var prov = reader.IsDBNull(0) ? CostProvenance.Unavailable : reader.GetString(0);
                var spend = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                var count = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2);
                if (spend > 0 || count > 0)
                    rows.Add(new ProvenanceGroup(prov, Math.Round(spend, 6), count));
            }
            return rows;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns one row per UTC calendar day in the window (zero-filled for days with no spend).
    /// Date strings are YYYY-MM-DD (UTC).
    /// </summary>
    public static IReadOnlyList<DayGroup> AggregateByDay(
        SqliteConnection connection, CostWindow window)
    {
        try
        {
            if (!HasObsSchema(connection)) return BuildZeroFilledDays(window);

            var fromStr = window.From.UtcDateTime.ToString("o");
            var toStr = window.To.UtcDateTime.ToString("o");

            using var cmd = connection.CreateCommand();
            // SQLite date() extracts YYYY-MM-DD in UTC when the timestamp is ISO-8601 with 'Z'.
            cmd.CommandText = """
                SELECT date(c.created_at) AS day,
                       COALESCE(SUM(c.total_cost_usd), 0) AS spend,
                       COUNT(*) AS row_count
                FROM obs_cost_estimates c
                JOIN obs_requests r ON r.id = c.request_id
                WHERE c.created_at >= $from AND c.created_at <= $to
                GROUP BY day
                ORDER BY day ASC;
                """;
            cmd.Parameters.AddWithValue("$from", fromStr);
            cmd.Parameters.AddWithValue("$to", toStr);

            var spendByDay = new Dictionary<string, (double Spend, int Count)>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var day = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var spend = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                var count = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2);
                if (!string.IsNullOrEmpty(day))
                    spendByDay[day] = (Math.Round(spend, 6), count);
            }

            return ZeroFillDays(window, spendByDay);
        }
        catch (SqliteException)
        {
            return BuildZeroFilledDays(window);
        }
    }

    /// <summary>
    /// Aggregates cost per model in the window. Input/output token counts are summed from
    /// obs_cost_estimates. Model ids are returned as-is (normalization happens in the caller
    /// where ModelIdNormalizer is available; CostAggregator stays in Scope.Core.Telemetry with
    /// no CostEstimation dependency).
    /// </summary>
    public static IReadOnlyList<ModelGroup> AggregateByModelWithTokens(
        SqliteConnection connection, CostWindow window)
    {
        try
        {
            if (!HasObsSchema(connection)) return [];

            var fromStr = window.From.UtcDateTime.ToString("o");
            var toStr = window.To.UtcDateTime.ToString("o");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(c.model_id, 'unknown') AS model_id,
                       COALESCE(SUM(c.total_cost_usd), 0) AS spend,
                       COALESCE(SUM(c.input_tokens), 0) AS input_tokens,
                       COALESCE(SUM(c.estimated_output_tokens), 0) AS output_tokens,
                       COUNT(*) AS row_count
                FROM obs_cost_estimates c
                JOIN obs_requests r ON r.id = c.request_id
                WHERE c.created_at >= $from AND c.created_at <= $to
                GROUP BY model_id
                ORDER BY spend DESC;
                """;
            cmd.Parameters.AddWithValue("$from", fromStr);
            cmd.Parameters.AddWithValue("$to", toStr);

            var groups = new List<ModelGroup>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var model = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                var spend = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                var inputTokens = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                var outputTokens = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);
                var count = reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4);
                groups.Add(new ModelGroup(model, Math.Round(spend, 6), inputTokens, outputTokens, count));
            }
            return groups;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    // ---- zero-fill helpers ----

    private static List<DayGroup> ZeroFillDays(
        CostWindow window, Dictionary<string, (double Spend, int Count)> spendByDay)
    {
        var days = new List<DayGroup>();
        var cursor = window.From.UtcDateTime.Date;
        var end = window.To.UtcDateTime.Date;

        while (cursor <= end)
        {
            var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var (spend, count) = spendByDay.TryGetValue(key, out var v) ? v : (0.0, 0);
            days.Add(new DayGroup(key, spend, count));
            cursor = cursor.AddDays(1);
        }
        return days;
    }

    private static List<DayGroup> BuildZeroFilledDays(CostWindow window)
        => ZeroFillDays(window, new Dictionary<string, (double, int)>(StringComparer.Ordinal));

    // ---- result types for new dimensions ----

    /// <summary>One provenance-bucket row from <see cref="AggregateByProvenance"/>.</summary>
    public sealed record ProvenanceGroup(string Provenance, double TotalSpendUsd, int RowCount);

    /// <summary>One UTC calendar-day row from <see cref="AggregateByDay"/>.</summary>
    public sealed record DayGroup(string Date, double TotalSpendUsd, int RowCount);

    /// <summary>One model row with token buckets from <see cref="AggregateByModelWithTokens"/>.</summary>
    public sealed record ModelGroup(
        string ModelId,
        double TotalSpendUsd,
        long InputTokens,
        long OutputTokens,
        int RowCount);

    // by_tool_type / by_day_tool_type are NO LONGER aggregated here (T-2026-06-20-04, OBS-08). The old
    // AggregateByToolType / AggregateByDayAndToolType SUMMED the obs_cost_estimates projection joined to
    // obs_requests.operation -- a ~2309x per-request model fan-out that over-counted spend by ~1009x.
    // They are DELETED so that broken projection can NEVER be summed for these dimensions again. Both
    // dimensions now derive from the canonical per-session rollups via WireCostAttribution in
    // ScopeApp.CommandLedger (per-actual-tool-call cost), which reconciles to the headline by construction.

    private static bool HasObsSchema(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='obs_cost_estimates';";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static CostAggregateResult Unavailable()
        => new(0.0, CostProvenance.Unavailable, []);
}

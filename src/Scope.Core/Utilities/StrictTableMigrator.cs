namespace Scope.Core.Utilities;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Serilog;

/// <summary>
/// How <see cref="StrictTableMigrator.Recreate"/> handles rows whose primary key is NULL.
/// STRICT tables make every PRIMARY KEY implicitly NOT NULL (https://sqlite.org/stricttables.html),
/// so a pre-existing NULL-PK row (legal only in the old non-STRICT rowid table) cannot be copied
/// into the new STRICT table as-is.
/// </summary>
public enum NullPkPolicy
{
    /// <summary>
    /// Immutable/critical ledgers (financial rows, agent definitions): a NULL-PK row is thrown as an
    /// <see cref="InvalidOperationException"/> naming the table and row count. Never silently drop a
    /// financial or definitional record.
    /// </summary>
    Throw,

    /// <summary>
    /// Best-effort telemetry (obs_*/retrieval_* rows): a NULL-PK row is already unreadable by the app
    /// (nothing can look it up by its own missing key), so it is logged as a warning naming the table
    /// and row count, then skipped from the copy. This must never hard-throw: EnsureSchema runs on the
    /// write path, and a single degenerate row would otherwise brick ingestion on that db forever.
    /// </summary>
    SkipAndWarn,
}

/// <summary>
/// Shared primitive for the "recreate a table as STRICT" migration pattern used by
/// <see cref="Cron.CronSchemaManager"/>, <see cref="Telemetry.WireCallSchemaManager"/>, and
/// <see cref="Telemetry.ObservabilitySchemaManager"/> (T-2026-07-12-01). Each call recreates ONE
/// table: <c>CREATE TABLE {table}_new (...) STRICT</c> -> copy rows with an explicit column list
/// (never <c>SELECT *</c>) -> assert the post-copy row count -> <c>DROP</c> the old table ->
/// <c>RENAME</c> the new one into place -> replay every index DDL read verbatim from
/// <c>sqlite_master</c> (so partial/filtered unique indexes survive exactly; indexes are never
/// hand-enumerated).
/// <para>
/// Every method here uses bare (non-retrying) <see cref="SqliteCommand"/> execution and expects to run
/// INSIDE a caller-owned transaction (BEGIN/COMMIT/ROLLBACK is the caller's responsibility). The
/// caller wraps the WHOLE transaction in a single <see cref="SqliteRetry"/> unit so a transient
/// BUSY/LOCKED failure rolls the transaction back first and then retries the entire sequence from
/// scratch -- never a partial statement. This differs from (and does not use) the
/// <see cref="SqliteCommandExtensions"/> *Retrying helpers, whose own doc comment warns against
/// wrapping a multi-statement sequence: here the sequence IS one atomic transaction, so retrying it as
/// a unit is safe by construction.
/// </para>
/// </summary>
public static class StrictTableMigrator
{
    /// <summary>
    /// Returns true when <paramref name="tableName"/> exists and is already declared STRICT (via
    /// <c>PRAGMA table_list</c>, not <c>sqlite_master</c>: shadow/virtual tables report a different
    /// <c>type</c> there and must never be misread as needing a STRICT recreate). Returns false when
    /// the table does not exist (nothing to convert) or exists but is not yet STRICT.
    /// </summary>
    public static bool IsStrict(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT strict FROM pragma_table_list($name) WHERE type = 'table';";
        cmd.Parameters.AddWithValue("$name", tableName);
        var result = cmd.ExecuteScalar();
        return result is not null && Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>Returns true when a table with this name exists in the database.</summary>
    public static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Reads every named index's CREATE statement for <paramref name="tableName"/> directly from
    /// <c>sqlite_master</c> (auto-generated PK/UNIQUE indexes have a NULL <c>sql</c> column and are
    /// excluded automatically -- CREATE TABLE regenerates those on its own). Must be called BEFORE the
    /// table is dropped; dropping a table cascades to drop its indexes' <c>sqlite_master</c> rows.
    /// </summary>
    public static IReadOnlyList<string> GetIndexDdls(SqliteConnection connection, string tableName)
    {
        var ddls = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = $name AND sql IS NOT NULL;";
        cmd.Parameters.AddWithValue("$name", tableName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ddls.Add(reader.GetString(0));
        return ddls;
    }

    /// <summary>
    /// Recreates <paramref name="tableName"/> as STRICT. No-ops immediately when the table is already
    /// STRICT (idempotent) or does not exist (nothing to convert -- used for tables owned by a
    /// different, non-versioned schema manager that may not have run yet on this connection).
    /// </summary>
    /// <param name="connection">The open connection; the caller owns the surrounding transaction.</param>
    /// <param name="tableName">The table to recreate.</param>
    /// <param name="createNewTableSql">
    /// The full <c>CREATE TABLE {tableName}_new (...) STRICT;</c> statement for the final shape.
    /// </param>
    /// <param name="columnList">
    /// Explicit, comma-separated column list (identical on both sides of the copy) used for both the
    /// INSERT target and the SELECT source. Never <c>SELECT *</c>, so the copy is robust to column-order
    /// drift.
    /// </param>
    /// <param name="pkColumn">
    /// The single-column primary key name to NULL-check before copying, or null when the table has no
    /// NULL-PK risk (composite PK over already-NOT-NULL columns, or an INTEGER PRIMARY KEY rowid alias
    /// that SQLite never stores as NULL).
    /// </param>
    /// <param name="nullPkPolicy">How to handle rows found with a NULL primary key.</param>
    public static void Recreate(
        SqliteConnection connection,
        string tableName,
        string createNewTableSql,
        string columnList,
        string? pkColumn,
        NullPkPolicy nullPkPolicy)
    {
        if (!TableExists(connection, tableName) || IsStrict(connection, tableName))
            return;

        var oldCount = ScalarLong(connection, $"SELECT COUNT(*) FROM {tableName};");
        var skipped = 0L;
        if (pkColumn is not null)
        {
            skipped = ScalarLong(connection, $"SELECT COUNT(*) FROM {tableName} WHERE {pkColumn} IS NULL;");
            if (skipped > 0)
            {
                if (nullPkPolicy == NullPkPolicy.Throw)
                {
                    throw new InvalidOperationException(
                        $"STRICT migration aborted: {tableName} has {skipped} row(s) with a NULL primary key " +
                        $"({pkColumn}). This table is an immutable/critical ledger; a NULL-PK row is never " +
                        "silently dropped. Manual data repair is required before this migration can proceed.");
                }

                Log.Warning(
                    "STRICT migration: {Table} has {Count} row(s) with a NULL primary key ({PkColumn}); " +
                    "these rows are already unreadable by the app and are skipped from the STRICT copy " +
                    "(best-effort telemetry table: EnsureSchema never hard-throws from the write path).",
                    tableName, skipped, pkColumn);
            }
        }

        var indexDdls = GetIndexDdls(connection, tableName);

        Exec(connection, createNewTableSql);

        var whereClause = pkColumn is not null ? $" WHERE {pkColumn} IS NOT NULL" : string.Empty;
        Exec(connection, $"INSERT INTO {tableName}_new ({columnList}) SELECT {columnList} FROM {tableName}{whereClause};");

        var expected = oldCount - skipped;
        var newCount = ScalarLong(connection, $"SELECT COUNT(*) FROM {tableName}_new;");
        if (newCount != expected)
        {
            throw new InvalidOperationException(
                $"STRICT migration row-count mismatch on {tableName}: expected {expected} " +
                $"(old {oldCount} - skipped {skipped}), got {newCount}.");
        }

        Exec(connection, $"DROP TABLE {tableName};");
        Exec(connection, $"ALTER TABLE {tableName}_new RENAME TO {tableName};");

        foreach (var ddl in indexDdls)
            Exec(connection, ddl);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

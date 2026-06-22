namespace Scope.Core.Telemetry;

using Microsoft.Data.Sqlite;
using Scope.Core.Utilities;
using Serilog;

/// <summary>
/// One L0 wire metering row: usage, latency, and identifiers for a single
/// host-to-provider API call observed by the metering proxy. Content-free by
/// construction (hashes and ids only, never bodies).
/// </summary>
public record WireCallRecord(
    string Id,
    string StartedAt,
    long? TtftMs,
    long TotalMs,
    string Endpoint,
    int Status,
    bool Stream,
    string? Model,
    string? MessageId,
    string? StopReason,
    long? InputTokens,
    long? OutputTokens,
    long? CacheCreationTokens,
    long? CacheReadTokens,
    string? ToolUseIdsJson,
    string? SystemHash,
    string? FirstUserHash,
    int? MessageCount,
    long RequestBytes,
    long ResponseBytes,
    string? MachineId = null,
    string? WorkspaceId = null,
    string? OrgId = null,
    string? CcSessionId = null,
    string? CcAgentId = null,
    string? CcParentAgentId = null);

/// <summary>
/// Writer for the wire_calls table in .scope/telemetry.db. Opens a connection
/// per write so the proxy never holds a long-lived lock against other Scope
/// processes. A locked database drops the row with a logged warning; metering
/// failures must never become traffic failures.
/// </summary>
public class WireCallDb(string telemetryDbPath)
{
    private bool _schemaEnsured;

    /// <summary>
    /// Inserts one wire call row. Returns true when persisted, false when the
    /// row was dropped (db locked or any other storage failure).
    /// </summary>
    public bool TryInsert(WireCallRecord record)
    {
        try
        {
            using var connection = SQLite.OpenDb(telemetryDbPath);
            if (!_schemaEnsured)
            {
                new WireCallSchemaManager(connection).EnsureSchema();
                _schemaEnsured = true;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO wire_calls (
                    id, started_at, ttft_ms, total_ms, endpoint, status, stream,
                    model, message_id, stop_reason,
                    input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens,
                    tool_use_ids, system_hash, first_user_hash, message_count,
                    request_bytes, response_bytes, machine_id, workspace_id, org_id,
                    cc_session_id, cc_agent_id, cc_parent_agent_id)
                VALUES (
                    $id, $startedAt, $ttftMs, $totalMs, $endpoint, $status, $stream,
                    $model, $messageId, $stopReason,
                    $inputTokens, $outputTokens, $cacheCreationTokens, $cacheReadTokens,
                    $toolUseIds, $systemHash, $firstUserHash, $messageCount,
                    $requestBytes, $responseBytes, $machineId, $workspaceId, $orgId,
                    $ccSessionId, $ccAgentId, $ccParentAgentId);
                """;
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
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            Log.Warning("wire_calls row dropped (db unavailable): {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning("wire_calls row dropped: {Message}", ex.Message);
            return false;
        }
    }
}

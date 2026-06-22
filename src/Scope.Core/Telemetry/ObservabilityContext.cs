namespace Scope.Core.Telemetry;

using Scope.Core.Utilities;
using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// Correlation carrier that flows through a single ScopeApp operation.
/// Tracks request identity, caller surface, agent session, and per-stage timing.
/// </summary>
public sealed class ObservabilityContext(
    CallerSurface surface,
    string operation,
    string? agentSessionKey = null,
    string? parentRequestId = null,
    string? dispatchId = null,
    string? parentSessionKey = null)
{
    private readonly List<StageRecord> _stages = [];
    private int _nextOrder;

    /// <summary>UUIDv7 identifier for this request.</summary>
    public string RequestId { get; } = Guid.GetGuid7String();

    /// <summary>UUIDv7 of the parent request when this is a sub-operation (e.g., MCP tool composing multiple ScopeApp calls).</summary>
    public string? ParentRequestId { get; } = parentRequestId;

    /// <summary>Which entry surface initiated this request.</summary>
    public CallerSurface Surface { get; } = surface;

    /// <summary>The ScopeApp operation name (find, pack, inspect, etc.).</summary>
    public string Operation { get; } = operation;

    /// <summary>Compound agent identity key (type:subtype:random), null for non-agent callers.</summary>
    public string? AgentSessionKey { get; } = agentSessionKey;

    /// <summary>Dispatch ID for correlating multiple agent requests within a single dispatch.</summary>
    public string? DispatchId { get; } = dispatchId;

    /// <summary>Session key of the parent agent that spawned this session, for session tree aggregation.</summary>
    public string? ParentSessionKey { get; } = parentSessionKey;

    /// <summary>
    /// Resolves the dispatch ID from the SCOPE_DISPATCH_ID environment variable.
    /// Returns null if the variable is not set.
    /// </summary>
    public static string? ResolveDispatchId()
        => Environment.GetEnvironmentVariable("SCOPE_DISPATCH_ID");

    /// <summary>
    /// Resolves the parent session key from the SCOPE_PARENT_SESSION_KEY environment variable.
    /// Returns null if the variable is not set.
    /// </summary>
    public static string? ResolveParentSessionKey()
        => Environment.GetEnvironmentVariable("SCOPE_PARENT_SESSION_KEY");

    /// <summary>UTC timestamp when this context was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// Starts timing a pipeline stage. Dispose the returned handle to record elapsed time.
    /// </summary>
#pragma warning disable MA0016 // public Scope.Core observability API (T-2026-06-17 sweep, owner): metadata bag is serialized via DictionaryStringObject; public signature preserved
    public IDisposable TimeStage(string stageName, Dictionary<string, object>? metadata = null)
    {
#pragma warning restore MA0016
        var order = Interlocked.Increment(ref _nextOrder);
        return new StageTimer(this, stageName, order, metadata);
    }

    /// <summary>
    /// Adds a completed stage record directly (for stages timed externally).
    /// </summary>
#pragma warning disable MA0016 // public Scope.Core observability API (T-2026-06-17 sweep, owner): metadata bag is serialized via DictionaryStringObject; public signature preserved
    public void AddStage(string stageName, long elapsedMs, Dictionary<string, object>? metadata = null)
    {
#pragma warning restore MA0016
        var order = Interlocked.Increment(ref _nextOrder);
        var metadataJson = metadata.IsInhabited
            ? JsonSerializer.Serialize(metadata, ScopeJsonSerializerContext.Default.DictionaryStringObject)
            : null;
        lock (_stages)
        {
            _stages.Add(new StageRecord(stageName, order, elapsedMs, metadataJson, DateTime.UtcNow));
        }
    }

    /// <summary>Returns all recorded stages in order.</summary>
    public IReadOnlyList<StageRecord> GetStages()
    {
        lock (_stages)
        {
            return [.. _stages.OrderBy(s => s.Order)];
        }
    }

    /// <summary>Returns total elapsed time across all recorded stages.</summary>
    public long TotalStageMs()
    {
        lock (_stages)
        {
            return _stages.Sum(s => s.ElapsedMs);
        }
    }

    private sealed class StageTimer(
        ObservabilityContext context,
        string stageName,
        int order,
        Dictionary<string, object>? metadata) : IDisposable
    {
        private readonly long _startTix = Stopwatch.GetTimestamp();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            long endTix = Stopwatch.GetTimestamp();
            long elapsedTix = endTix - _startTix;
            long elapsedMs = elapsedTix * 1000 / Stopwatch.Frequency;
            var metadataJson = metadata.IsInhabited
                ? JsonSerializer.Serialize(metadata, ScopeJsonSerializerContext.Default.DictionaryStringObject)
                : null;
            lock (context._stages)
            {
                context._stages.Add(new StageRecord(
                    stageName, order, elapsedMs, metadataJson, DateTime.UtcNow));
            }
        }
    }
}

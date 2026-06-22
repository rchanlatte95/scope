namespace Scope.Core.Telemetry;

/// <summary>
/// Records timing and metadata for a single pipeline stage within a request.
/// </summary>
public sealed record StageRecord(
    string Name,
    int Order,
    long ElapsedMs,
    string? MetadataJson,
    DateTime CreatedAt);

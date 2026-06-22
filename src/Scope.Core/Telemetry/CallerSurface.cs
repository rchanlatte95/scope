namespace Scope.Core.Telemetry;

/// <summary>
/// Identifies the calling surface that initiated a retrieval request.
/// Used for telemetry segmentation and diagnostics.
/// </summary>
public enum CallerSurface
{
    /// <summary>Request originated from the CLI.</summary>
    Cli,

    /// <summary>Request originated from the MCP server.</summary>
    Mcp,

    /// <summary>Request originated from the HTTP server.</summary>
    Server,

    /// <summary>Request originated from a test harness.</summary>
    Test,

    /// <summary>Request originated from the benchmark harness.</summary>
    Benchmark,

    /// <summary>Caller surface was not specified.</summary>
    Unknown
}

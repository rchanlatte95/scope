namespace Scope.Proxy;

/// <summary>
/// Provider-agnostic wire-metering contract. One implementation per upstream provider
/// (Anthropic, OpenAI Responses). The <see cref="Forwarder"/> selects the implementation
/// from <see cref="ProxyServerOptions.Provider"/> at startup; all other forwarding,
/// security, and passthrough invariants remain provider-independent.
/// </summary>
internal interface IWireMeter
{
    /// <summary>
    /// Returns true when this meter should meter the given endpoint. Called for POST
    /// requests only; the Forwarder retains the HTTP-method gate.
    /// </summary>
    bool IsMetered(string endpoint);

    /// <summary>
    /// All endpoints this meter understands (used for drift-count logging on unknown paths).
    /// </summary>
    string[] KnownEndpoints { get; }

    /// <summary>Creates a fresh streaming scanner for one SSE response body.</summary>
    IWireSseScanner CreateSseScanner();

    /// <summary>Parses a non-streaming response body teed into a bounded buffer.</summary>
    ResponseUsage ParseNonStreaming(BoundedTeeBuffer responseTee, string? contentEncoding);

    /// <summary>Extracts content-free hashes and counts from a teed request body.</summary>
    RequestSummary SummarizeRequest(BoundedTeeBuffer requestTee);
}

/// <summary>
/// Incremental SSE byte scanner produced by <see cref="IWireMeter.CreateSseScanner"/>.
/// Fed raw response bytes chunk-by-chunk during the proxy copy loop. Must be disposed
/// after use so implementations that write capture files can flush on completion.
/// Implementations must never throw from <see cref="Feed"/>; any internal failure must
/// set <see cref="Failed"/> so the meter row is abandoned without breaking forwarding.
/// </summary>
internal interface IWireSseScanner : IDisposable
{
    /// <summary>Feeds one chunk of raw SSE bytes. Never throws.</summary>
    void Feed(ReadOnlySpan<byte> chunk);

    /// <summary>True when an internal parse error occurred; the meter row must be abandoned.</summary>
    bool Failed { get; }

    /// <summary>
    /// True when the terminal usage-bearing event was successfully observed. When false
    /// (e.g. a truncated stream or a Phase-A stub that does not yet parse) the Forwarder
    /// treats usage as Failed and no wire_calls row is written.
    /// </summary>
    bool SawStart { get; }

    string? Model { get; }
    string? MessageId { get; }
    string? StopReason { get; }
    long? InputTokens { get; }
    long? OutputTokens { get; }
    long? CacheCreationTokens { get; }
    long? CacheReadTokens { get; }
    List<string> ToolUseIds { get; }
}

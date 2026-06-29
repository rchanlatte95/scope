namespace Scope.Proxy;

/// <summary>
/// <see cref="IWireMeter"/> implementation for the Anthropic Messages API (<c>/v1/messages</c>).
/// Delegates all parsing to the static <see cref="AnthropicMeter"/> and
/// <see cref="AnthropicSseScanner"/> helpers whose behavior is covered by the existing
/// proxy test suite. This wrapper adds no logic; it satisfies the provider abstraction
/// so the <see cref="Forwarder"/> no longer depends directly on Anthropic-specific types.
/// </summary>
internal sealed class AnthropicWireMeter : IWireMeter
{
    private static readonly string[] Endpoints =
    [
        "/v1/messages", "/v1/messages/count_tokens", "/v1/messages/batches", "/v1/models",
    ];

    /// <inheritdoc/>
    public bool IsMetered(string endpoint) =>
        endpoint.Equals("/v1/messages", StringComparison.Ordinal);

    /// <inheritdoc/>
    public string[] KnownEndpoints => Endpoints;

    /// <inheritdoc/>
    public IWireSseScanner CreateSseScanner() => new AnthropicSseScanner();

    /// <inheritdoc/>
    public ResponseUsage ParseNonStreaming(BoundedTeeBuffer responseTee, string? contentEncoding) =>
        AnthropicMeter.ParseMessageResponse(responseTee, contentEncoding);

    /// <inheritdoc/>
    public RequestSummary SummarizeRequest(BoundedTeeBuffer requestTee) =>
        AnthropicMeter.SummarizeRequest(requestTee);
}

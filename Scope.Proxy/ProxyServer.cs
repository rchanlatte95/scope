namespace Scope.Proxy;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Text;

/// <summary>Configuration for one proxy instance.</summary>
internal sealed record ProxyServerOptions
{
    public required string Token { get; init; }
    public required string TelemetryDbPath { get; init; }
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5135;
    public string UpstreamBaseUrl { get; init; } = "https://api.anthropic.com";

    /// <summary>
    /// Upstream provider key. <c>"anthropic"</c> (default) routes /v1/messages and meters
    /// with <see cref="AnthropicWireMeter"/>. <c>"openai"</c> routes /v1/responses and meters
    /// with <see cref="OpenAiResponsesMeter"/>. Both write wire_calls rows.
    /// </summary>
    public string Provider { get; init; } = "anthropic";

    /// <summary>
    /// When set, the OpenAI meter also appends every proxied /v1/responses body to this
    /// file (one labeled block per round-trip, streaming and non-streaming), for inspecting
    /// real Codex traffic. Ignored for provider != "openai".
    /// Also settable via the <c>SCOPE_PROXY_CAPTURE_FILE</c> environment variable.
    /// </summary>
    public string? CaptureFilePath { get; init; }
}

/// <summary>
/// The localhost metering proxy server. Security gates run before any
/// forwarding: loopback bind (validated by the entry point), per-start token
/// path prefix (404 otherwise), Origin header rejection, and Host header
/// validation. Auth headers are never logged at any level (allowlist-only
/// logging lives in the forwarder, which logs no header values at all).
/// </summary>
internal sealed class ProxyServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Forwarder _forwarder;
    private bool _disposed;

    public string BoundUrl { get; private set; } = "";
    public int BoundPort { get; private set; }
    public string StartedAt { get; } = DateTime.UtcNow.ToString("o");

    public long DriftCount => _forwarder.DriftCount;
    public long RecordedCount => _forwarder.RecordedCount;

    private ProxyServer(WebApplication app, Forwarder forwarder)
    {
        _app = app;
        _forwarder = forwarder;
    }

    /// <summary>Builds and starts a proxy instance bound to the configured address.</summary>
    public static async Task<ProxyServer> StartAsync(ProxyServerOptions options, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{FormatHost(options.BindAddress)}:{options.Port}");

        IWireMeter meter = options.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiResponsesMeter(options.CaptureFilePath)
            : new AnthropicWireMeter();
        var forwarder = new Forwarder(options.UpstreamBaseUrl, options.TelemetryDbPath, meter: meter);
        var app = builder.Build();
        var server = new ProxyServer(app, forwarder);
        var tokenBytes = Encoding.UTF8.GetBytes(options.Token);

        app.Run(async context =>
        {
            // 1. Reject any request bearing an Origin header (browser CSRF surface).
            if (context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // 2. Host header must name the loopback listen address.
            if (!IsAllowedHost(context, options.BindAddress, server.BoundPort))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            var path = context.Request.Path.Value ?? "/";

            // 3. Health endpoint (no token, no secrets in the payload).
            if (HttpMethods.IsGet(context.Request.Method)
                && path.Equals("/healthz", StringComparison.Ordinal))
            {
                var health = new HealthzResponse(
                    "ok", Environment.ProcessId, server.BoundPort, server.StartedAt,
                    forwarder.DriftCount, forwarder.RecordedCount);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(health.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // 4. Everything else requires the per-start token prefix: /t/{token}/...
            if (!TryStripTokenPrefix(path, tokenBytes, out var remainder))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await forwarder.ForwardAsync(context, remainder).ConfigureAwait(false);
        });

        await app.StartAsync(ct).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        server.BoundUrl = addresses?.FirstOrDefault() ?? $"http://{options.BindAddress}:{options.Port}";
        server.BoundPort = new Uri(server.BoundUrl).Port;
        return server;
    }

    /// <summary>Blocks until the host shuts down (Ctrl+C / SIGTERM).</summary>
    public Task WaitForShutdownAsync(CancellationToken ct = default) => _app.WaitForShutdownAsync(ct);

    /// <summary>
    /// Extracts the forwarding path from /t/{token}/{remainder}, comparing the
    /// token in fixed time. Returns false (caller responds 404) on any mismatch.
    /// </summary>
    internal static bool TryStripTokenPrefix(string path, byte[] expectedToken, out string remainder)
    {
        remainder = "";
        const string prefix = "/t/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var rest = path.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');
        var candidate = slash >= 0 ? rest[..slash] : rest;

        Span<byte> candidateBytes = stackalloc byte[256];
        if (Encoding.UTF8.GetByteCount(candidate) > candidateBytes.Length) return false;
        var written = Encoding.UTF8.GetBytes(candidate, candidateBytes);

        if (written != expectedToken.Length ||
            !CryptographicOperations.FixedTimeEquals(candidateBytes[..written], expectedToken))
        {
            return false;
        }

        remainder = slash >= 0 ? rest[(slash + 1)..].ToString() : "";
        return true;
    }

    /// <summary>
    /// Validates the Host header: hostname must be the loopback listen address
    /// (or a loopback alias) and the port, when present, must match the bound port.
    /// </summary>
    internal static bool IsAllowedHost(HttpContext context, string bindAddress, int boundPort)
    {
        var host = context.Request.Host;
        if (!host.HasValue) return false;

        var name = host.Host;
        var allowed = name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                      || name.Equals("::1", StringComparison.OrdinalIgnoreCase)
                      || name.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
                      || name.Equals(bindAddress, StringComparison.OrdinalIgnoreCase);
        if (!allowed) return false;

        return host.Port is null || boundPort == 0 || host.Port == boundPort;
    }

    /// <summary>
    /// Refuses non-loopback bind addresses unless the caller passed --unsafe-bind.
    /// </summary>
    internal static bool ValidateBind(string bindAddress, bool unsafeBind, out string error)
    {
        error = "";
        if (unsafeBind) return true;

        var isLoopback = bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                         || (IPAddress.TryParse(bindAddress, out var ip) && IPAddress.IsLoopback(ip));
        if (isLoopback) return true;

        error = $"Refusing to bind to non-loopback address '{bindAddress}'. " +
                "The metering proxy forwards credentialed traffic and must stay on 127.0.0.1. " +
                "Pass --unsafe-bind to override (not recommended).";
        return false;
    }

    private static string FormatHost(string bindAddress) =>
        bindAddress.Contains(':') && !bindAddress.StartsWith('[') ? $"[{bindAddress}]" : bindAddress;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _forwarder.Dispose();
        GC.SuppressFinalize(this);
    }
}

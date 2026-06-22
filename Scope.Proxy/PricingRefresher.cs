namespace Scope.Proxy;

using Scope.Core.CostEstimation;
using Serilog;

/// <summary>
/// Background loop that refreshes the public model-pricing catalog roughly hourly from
/// inside the long-running proxy process (ST-04). The proxy is the only persistent
/// component in the wire-metering stack, so the refresh lives here rather than in the
/// short-lived CLI.
///
/// Behavior (owner-approved 2026-06-11):
/// <list type="bullet">
///   <item>Fetch the LiteLLM catalog (<see cref="LiteLlmPricingSource.DefaultSourceUrl"/>)
///   over the same <c>SocketsHttpHandler</c> stack the forwarder uses (zero new deps).</item>
///   <item>Validate it parses to at least one priced model, then cache the verbatim catalog
///   plus the fetch timestamp to <c>.scope/pricing.cache.json</c> via
///   <see cref="PricingCache.TryWrite"/>.</item>
///   <item>Tolerant: any network or parse failure keeps the last-known cache on disk, is
///   logged at debug, and never crashes the proxy or blocks forwarding. The loop runs on a
///   detached background task entirely off the request path.</item>
///   <item>Offline-first: if the first fetch fails there simply is no cache; the cost layer
///   falls back to a local pricing.json or to no estimate, exactly as before ST-04.</item>
/// </list>
///
/// An immediate refresh runs at startup (so a long-lived proxy is useful at once), then on a
/// fixed interval. A successful fetch is not required for the proxy to be healthy.
/// </summary>
internal sealed class PricingRefresher : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly string _cachePath;
    private readonly string _sourceUrl;
    private readonly TimeSpan _interval;
    private readonly HttpMessageInvoker _client;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>Count of successful catalog refreshes (exposed for tests/telemetry).</summary>
    public long RefreshCount;

    public PricingRefresher(string scopeDir, string? sourceUrl = null, TimeSpan? interval = null)
    {
        _cachePath = PricingResolver.CacheFilePath(scopeDir);
        _sourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? LiteLlmPricingSource.DefaultSourceUrl : sourceUrl!;
        _interval = interval ?? RefreshInterval;
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        }, disposeHandler: true);
    }

    /// <summary>Starts the detached refresh loop. Returns immediately; never throws.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop ??= Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshOnceAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Performs one fetch-validate-cache cycle. Fully tolerant: returns true only on a
    /// confirmed parse + cache write, false on any failure (last-known cache untouched).
    /// Exposed internal for tests.
    /// </summary>
    internal async Task<bool> RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _sourceUrl);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(RequestTimeout);

            using var response = await _client.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug("Pricing refresh: source returned {Status}; keeping last-known prices", (int)response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            // Validate before caching: a structurally broken document must not overwrite a
            // good cache. Parse throws on a bad top-level shape; require >=1 priced model.
            var fetchedAt = DateTime.UtcNow;
            var parsed = LiteLlmPricingSource.Parse(json, fetchedAt.ToString("o"));
            if (parsed.Models.Count == 0)
            {
                Log.Debug("Pricing refresh: catalog parsed to zero priced models; keeping last-known prices");
                return false;
            }

            if (!PricingCache.TryWrite(_cachePath, json, fetchedAt))
                return false;

            Interlocked.Increment(ref RefreshCount);
            Log.Debug("Pricing refresh: cached {Count} models (prices as of {AsOf}) to {Path}",
                parsed.Models.Count, fetchedAt.ToString("o"), _cachePath);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false; // proxy shutting down
        }
        catch (Exception ex)
        {
            // Network down, DNS failure, timeout, malformed JSON: keep last-known, never throw.
            Log.Debug("Pricing refresh failed (non-fatal); keeping last-known prices: {Message}", ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* best effort */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _cts.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}

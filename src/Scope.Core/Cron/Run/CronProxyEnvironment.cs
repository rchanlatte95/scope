namespace Scope.Core.Cron.Run;

using Scope.Core.Telemetry;

/// <summary>
/// Resolves the advertised metering-proxy base URL for a cron run, reusing the existing
/// <see cref="ProxyState"/> contract (the same state file written by <c>scope proxy run</c>).
///
/// Reuse honesty: the full proxy lifecycle (start-or-attach, health probing, bounded restart)
/// lives in the CLI-layer ProxyLauncher and is not reusable from Scope.Core. What IS reusable and
/// host-independent is the state-file contract: when a proxy is already advertised at
/// <c>.scope/proxy.json</c>, its <see cref="ProxyState.BaseUrl"/> (token included) is handed to the
/// launcher adapter as the host-independent <c>HarnessLaunchRequest.ProxyBaseUrl</c>. Which provider
/// environment variable that URL becomes is the adapter's knowledge, not the runner's
/// (platform-independence rule).
///
/// When no proxy is advertised, null is returned: the run executes un-metered and the absent wire
/// cost is recorded honestly as null. The runner never starts a proxy itself (that is the
/// supervisor/CLI concern), so this is the correct conservative default.
/// </summary>
public static class CronProxyEnvironment
{
    /// <summary>The Anthropic proxy state file name, relative to the workspace <c>.scope/</c> directory.</summary>
    public const string StateFileName = "proxy.json";

    /// <summary>The OpenAI proxy state file name, relative to the workspace <c>.scope/</c> directory.</summary>
    public const string OpenAiStateFileName = "proxy.openai.json";

    /// <summary>
    /// Resolves the advertised Anthropic proxy base URL given the workspace <c>.scope/</c> directory.
    /// Returns null when no proxy state file is present or it cannot be parsed.
    /// </summary>
    public static string? TryResolveBaseUrl(string scopeDirectory)
    {
        try
        {
            var stateFile = Path.Combine(scopeDirectory, StateFileName);
            return ProxyState.TryLoad(stateFile)?.BaseUrl;
        }
        catch
        {
            // Proxy wiring is best-effort: an unreadable state file means un-metered, never a crash.
            return null;
        }
    }

    /// <summary>
    /// Resolves the advertised OpenAI proxy base URL given the workspace <c>.scope/</c> directory,
    /// reading from <c>proxy.openai.json</c> (the OpenAI-provider state file written by
    /// <c>scope proxy run --provider openai</c> / <see cref="ProxyLauncher.EnsureProxyAsync"/> with
    /// <c>provider = "openai"</c>). Returns null when no OpenAI proxy state file is present or it
    /// cannot be parsed. Proxy wiring is best-effort: an absent or unreadable file means un-metered,
    /// never a crash.
    /// </summary>
    public static string? TryResolveOpenAiBaseUrl(string scopeDirectory)
    {
        try
        {
            var stateFile = Path.Combine(scopeDirectory, OpenAiStateFileName);
            return ProxyState.TryLoad(stateFile)?.BaseUrl;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Appends the proxy-local budget-scope segment <c>/sb/{session}/{budget}</c> to a resolved proxy
    /// base URL so a budgeted run self-declares its session id + enforced token budget on every model
    /// call (the proxy reads it from the path to gate NEW calls; see <c>BudgetEnforcer</c>). Returns the
    /// base URL unchanged when there is no positive budget, and null when there is no base URL. The
    /// session id is path-safe by construction (it is a Scope-minted <c>cron-{uuid7}</c> session key).
    /// </summary>
    public static string? WithBudgetScope(string? baseUrl, string sessionId, int? tokenBudget)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return null;
        if (tokenBudget is not int budget || budget <= 0 || string.IsNullOrEmpty(sessionId))
            return baseUrl;

        var trimmed = baseUrl.TrimEnd('/');
        return $"{trimmed}/sb/{sessionId}/{budget}";
    }
}

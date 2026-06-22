namespace Scope.Cli.Proxy;

using Scope.Core.Cron.Launch;

/// <summary>
/// Pure decision logic for whether an interactive <c>scope claude</c> session is routed through the
/// Scope wire-metering proxy (owner D1: AUTO-METER when an Anthropic API key is present in the parent
/// environment). The proxy speaks the Anthropic wire protocol, so this gate is the Claude path ONLY;
/// <c>scope codex</c> talks to OpenAI and is never wired here.
/// <para>
/// Correctness rules baked in:
/// <list type="bullet">
/// <item>Meter ONLY when a non-empty API key is present AND the user did not opt out with
/// <c>--no-meter</c>. A Max subscription (OAuth, no API key) stays on the direct, un-metered path
/// EXACTLY as today: we never forward a subscription OAuth token through a custom base URL.</item>
/// <item>We DETECT a user-provided key; we never inject, synthesize, or read a key from anywhere but
/// the inherited parent environment.</item>
/// <item>The env-var NAMES are sourced from the host adapter constants by the caller, so no provider
/// string is hardcoded outside the adapter (platform-independence rule).</item>
/// </list>
/// </para>
/// </summary>
public static class InteractiveMeterGate
{
    /// <summary>
    /// Decides whether the interactive Claude session should be metered. Pure and side-effect-free so
    /// the gate is unit-testable without a real proxy or process.
    /// </summary>
    /// <param name="apiKeyFromParentEnv">The Anthropic API key read from the PARENT process environment, or null/empty when absent.</param>
    /// <param name="noMeter">True when the user passed <c>--no-meter</c> to opt this session out of metering.</param>
    /// <returns>True only when a non-empty key is present and the user did not opt out.</returns>
    public static bool ShouldMeter(string? apiKeyFromParentEnv, bool noMeter)
        => !noMeter && !string.IsNullOrWhiteSpace(apiKeyFromParentEnv);

    /// <summary>
    /// Builds the child-environment proxy wiring for a metered Claude session, sourcing the env-var
    /// names from the Claude adapter constants (never hardcoded here). The API key already flows to the
    /// child via the inherited parent environment, but it is routed explicitly through the adapter
    /// constant so the metered child is self-describing and robust to env scrubbing.
    /// </summary>
    /// <param name="proxyBaseUrl">The tokenized proxy base URL resolved by <c>EnsureProxyAsync</c>.</param>
    /// <param name="apiKeyFromParentEnv">The detected API key (set explicitly via the adapter constant).</param>
    public static InteractiveProxyWiring BuildClaudeWiring(string proxyBaseUrl, string? apiKeyFromParentEnv)
        => new(
            BaseUrlEnvVar: ClaudeCodeHarnessLauncher.ProxyBaseUrlEnvVar,
            BaseUrl: proxyBaseUrl,
            ApiKeyEnvVar: ClaudeCodeHarnessLauncher.ApiKeyEnvVar,
            ApiKey: apiKeyFromParentEnv);

    /// <summary>
    /// The Anthropic API key env var, read from the parent environment by the gate. Sourced from the
    /// Claude adapter constant so the provider string lives only in the adapter.
    /// </summary>
    public static string ApiKeyEnvVar => ClaudeCodeHarnessLauncher.ApiKeyEnvVar;
}

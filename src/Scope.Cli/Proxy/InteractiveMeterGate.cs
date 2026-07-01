namespace Scope.Cli.Proxy;

using Scope.Core.Cron.Launch;

/// <summary>
/// Pure decision logic for whether an interactive <c>scope claude</c> or <c>scope codex</c> session
/// is routed through the Scope wire-metering proxy.
/// <list type="bullet">
/// <item>Claude path (owner D1 AUTO-METER): meter when <c>ANTHROPIC_API_KEY</c> is present in the
/// parent environment and the user did not opt out with <c>--no-meter</c>. The Anthropic proxy
/// injects <c>ANTHROPIC_BASE_URL</c> into the child env.</item>
/// <item>Codex path: meter when <c>CODEX_API_KEY</c> is present. The OpenAI proxy is started on
/// port 5136; metering is injected via <c>-c model_providers.scope.*</c> args (not an env var),
/// keeping <c>OPENAI_BASE_URL</c> and <c>~/.codex/auth.json</c> untouched.</item>
/// </list>
/// <para>
/// Correctness rules baked in:
/// <list type="bullet">
/// <item>Meter ONLY when a non-empty API key is present (and the user did not opt out for Claude).
/// A subscription user with no key stays on the direct, un-metered path: we never forward a
/// subscription OAuth token through a custom base URL.</item>
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
    /// Decides whether an interactive session should be metered. Pure and side-effect-free so the gate
    /// is unit-testable without a real proxy or process. Used by both the Claude and Codex paths
    /// (the Codex path always passes <c>noMeter: false</c> — there is no Codex --no-meter flag).
    /// </summary>
    /// <param name="apiKeyFromParentEnv">The provider API key read from the PARENT process environment, or null/empty when absent.</param>
    /// <param name="noMeter">True when the user passed <c>--no-meter</c> to opt this session out of metering (Claude only; always false for Codex).</param>
    /// <returns>True only when a non-empty key is present and the user did not opt out.</returns>
    public static bool ShouldMeter(string? apiKeyFromParentEnv, bool noMeter)
        => !noMeter && !string.IsNullOrWhiteSpace(apiKeyFromParentEnv);

    /// <summary>
    /// Builds the child-environment proxy wiring for a metered Claude session, sourcing the env-var
    /// names from the Claude adapter constants (never hardcoded here). The API key already flows to the
    /// child via the inherited parent environment, but it is routed explicitly through the adapter
    /// constant so the metered child is self-describing and robust to env scrubbing.
    /// </summary>
    /// <param name="proxyBaseUrl">The tokenized Anthropic proxy base URL resolved by <c>EnsureProxyAsync</c>.</param>
    /// <param name="apiKeyFromParentEnv">The detected Anthropic API key (set explicitly in the child env via the adapter constant).</param>
    public static InteractiveProxyWiring BuildClaudeWiring(string proxyBaseUrl, string? apiKeyFromParentEnv)
        => new(
            BaseUrlEnvVar: ClaudeCodeHarnessLauncher.ProxyBaseUrlEnvVar,
            BaseUrl: proxyBaseUrl,
            ApiKeyEnvVar: ClaudeCodeHarnessLauncher.ApiKeyEnvVar,
            ApiKey: apiKeyFromParentEnv);

    /// <summary>
    /// Builds the interactive proxy wiring for a metered Codex session. Unlike the Claude path, Codex
    /// routes traffic through the OpenAI proxy via <c>-c model_providers.scope.*</c> args injected by
    /// <see cref="CodexHarnessLauncher.BuildOpenAiMeteringArgs"/> — no <c>OPENAI_BASE_URL</c> or
    /// <c>base_url</c> env var is touched. <c>CODEX_API_KEY</c> is set in the child env so the Codex
    /// <c>env_key</c> config reads it from a well-known name. This leaves <c>~/.codex/auth.json</c>
    /// byte-identical and is non-destructive, matching the procedure verified on real Codex 0.141 traffic.
    /// </summary>
    /// <param name="proxyBaseUrl">The tokenized OpenAI proxy base URL resolved by <c>EnsureProxyAsync(provider: "openai")</c>.</param>
    /// <param name="apiKeyFromParentEnv">The detected <c>CODEX_API_KEY</c> (set explicitly in the child env).</param>
    public static InteractiveProxyWiring BuildCodexWiring(string proxyBaseUrl, string? apiKeyFromParentEnv)
        => new(
            ApiKeyEnvVar: CodexHarnessLauncher.ApiKeyEnvVar,
            ApiKey: apiKeyFromParentEnv,
            ExtraArgs: CodexHarnessLauncher.BuildOpenAiMeteringArgs(proxyBaseUrl));

    /// <summary>
    /// The Anthropic API key env var, read from the parent environment by the Claude gate. Sourced from
    /// the Claude adapter constant so the provider string lives only in the adapter.
    /// </summary>
    public static string ApiKeyEnvVar => ClaudeCodeHarnessLauncher.ApiKeyEnvVar;

    /// <summary>
    /// The OpenAI/Codex API key env var, read from the parent environment by the Codex gate. Sourced
    /// from the Codex adapter constant so the provider string lives only in the adapter.
    /// </summary>
    public static string CodexApiKeyEnvVar => CodexHarnessLauncher.ApiKeyEnvVar;
}

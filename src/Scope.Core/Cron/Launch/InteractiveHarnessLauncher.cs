namespace Scope.Core.Cron.Launch;

using Scope.Core.Utilities;

/// <summary>
/// Outcome of an interactive harness launch (<c>scope claude</c> / <c>scope codex</c>).
/// </summary>
/// <param name="SessionKey">The Scope-minted <c>cli-{uuid7}</c> exported to the child, or null when the launch never ran.</param>
/// <param name="Capability">Capability observed at launch time; non-available means the harness never started.</param>
/// <param name="ExitCode">The harness exit code when it ran, or a Scope-supplied non-zero code when it could not start.</param>
/// <param name="FailureReason">Human-readable reason the launch did not run; null when the harness started.</param>
public sealed record InteractiveLaunchResult(
    string? SessionKey,
    HarnessCapability Capability,
    int ExitCode,
    string? FailureReason)
{
    /// <summary>True only when the harness binary was found and the child process was started.</summary>
    public bool Started => FailureReason is null;
}

/// <summary>
/// Optional metering-proxy wiring for an interactive launch. Carries the host-independent env-var
/// NAMES (sourced by the caller from the host adapter constants, never hardcoded here, per the
/// platform-independence rule) plus the values to set in the CHILD environment only: the tokenized
/// proxy base URL and, optionally, the API key to route explicitly. The launcher stays
/// harness-agnostic; only the caller's gate decides whether to supply this and which adapter constants
/// to source the names from.
/// <para>
/// <see cref="BaseUrlEnvVar"/> and <see cref="BaseUrl"/> are optional. When both are provided, the
/// child env gains the proxy base-URL env var (e.g. <c>ANTHROPIC_BASE_URL</c> for Claude). When
/// omitted (null), no base-URL env var is set — the Codex path uses this because it routes via
/// <see cref="ExtraArgs"/> <c>-c model_providers</c> flags instead of a base-URL env var.
/// </para>
/// <para>
/// When <see cref="ApiKeyEnvVar"/> and <see cref="ApiKey"/> are both non-empty, the key is set
/// explicitly in the child env; otherwise the key is left to flow through the inherited parent
/// environment unchanged.
/// </para>
/// <para>
/// <see cref="ExtraArgs"/> are prepended to the user's passthrough arguments before the harness is
/// launched. Used by the Codex path to inject <c>-c model_providers.scope.*</c> flags that route
/// <c>/v1/responses</c> through the Scope metering proxy without touching any env var.
/// </para>
/// </summary>
/// <param name="BaseUrlEnvVar">Host adapter's proxy base-url env-var name (e.g. <c>ANTHROPIC_BASE_URL</c>), or null when the harness routes via args instead of an env var.</param>
/// <param name="BaseUrl">Tokenized proxy base URL the child must call (<c>http://127.0.0.1:{port}/t/{token}</c>), or null when unused.</param>
/// <param name="ApiKeyEnvVar">Host adapter's API-key env-var name (e.g. <c>ANTHROPIC_API_KEY</c>), or null to inherit the key from the parent env unchanged.</param>
/// <param name="ApiKey">API key value to set explicitly, or null to inherit the parent env unchanged.</param>
/// <param name="ExtraArgs">Extra arguments prepended to the user passthrough args at launch (e.g. Codex <c>-c model_providers.*</c> flags). Null or empty means no prepend.</param>
public sealed record InteractiveProxyWiring(
    string? BaseUrlEnvVar = null,
    string? BaseUrl = null,
    string? ApiKeyEnvVar = null,
    string? ApiKey = null,
    IReadOnlyList<string>? ExtraArgs = null);

/// <summary>
/// Launches an agent harness (Claude Code or Codex) INTERACTIVELY as a transparent passthrough for
/// the thin <c>scope claude</c> / <c>scope codex</c> aliases. This is the durable tier-1 attribution
/// path for interactive sessions: it mints a real Scope-owned session key (<c>cli-{uuid7}</c>) and
/// exports it as <c>SCOPE_SESSION_KEY</c> in the CHILD environment ONLY, so the harness and all of
/// its children (the PostToolUse hook, the MCP server) resolve tier-1 attribution with no
/// resolution-logic change. The wrapper forwards the user's args verbatim, inherits stdio, and
/// returns the harness exit code.
/// <para>
/// This is NOT the headless cron path (<see cref="HarnessLaunchRequest"/> pipes a prompt on stdin
/// and captures artifacts). It reuses only the binary-discovery (<see cref="ExecutableResolver"/>)
/// and the capability probe (<see cref="IHarnessLauncher.ProbeCapabilityAsync"/>) from the cron
/// launchers, plus their <c>SCOPE_SESSION_KEY</c> env-var-name contract. Host-specific knowledge
/// (executable name, env-var name) stays behind the cron launcher adapters; this wrapper is
/// harness-agnostic.
/// </para>
/// </summary>
public sealed class InteractiveHarnessLauncher
{
    /// <summary>
    /// Env var that carries the Scope-owned session key into the child. Identical to the constant
    /// the cron launchers inject (ClaudeCodeHarnessLauncher / CodexHarnessLauncher); the capture
    /// channels already honor it for tier-1 attribution.
    /// </summary>
    public const string SessionEnvVar = "SCOPE_SESSION_KEY";

    /// <summary>Prefix for an interactive CLI session key, mirroring the cron launchers' <c>cron-</c> prefix.</summary>
    public const string SessionKeyPrefix = "cli-";

    /// <summary>
    /// Exit code returned when the harness binary cannot be found or started, so the wrapper still
    /// fails with a clean non-zero code (POSIX convention 127 = command not found) rather than an
    /// exception. The harness's own exit code passes through verbatim when it does run.
    /// </summary>
    public const int BinaryUnavailableExitCode = 127;

    private readonly IHarnessLauncher _capabilityProbe;
    private readonly IInteractiveProcessRunner _interactiveRunner;
    private readonly string _executable;
    private readonly Func<string, ResolvedExecutable> _resolve;
    private readonly Func<string> _mintSessionKey;

    /// <summary>
    /// Creates an interactive launcher for the given harness.
    /// </summary>
    /// <param name="capabilityProbe">Cron launcher reused only for its <see cref="IHarnessLauncher.ProbeCapabilityAsync"/> + default executable.</param>
    /// <param name="interactiveRunner">Interactive spawn seam (production: <see cref="SystemInteractiveProcessRunner"/>).</param>
    /// <param name="executable">Logical harness executable name resolved against PATH (e.g. <c>claude</c>, <c>codex</c>).</param>
    public InteractiveHarnessLauncher(
        IHarnessLauncher capabilityProbe,
        IInteractiveProcessRunner interactiveRunner,
        string executable)
        : this(capabilityProbe, interactiveRunner, executable, name => ExecutableResolver.Resolve(name), MintSessionKey)
    {
    }

    /// <summary>
    /// Test-only constructor that injects the PATH-resolution seam and the session-key minter so
    /// tests can assert the child env carries a deterministic <c>cli-{uuid7}</c> and exercise both
    /// the directly-launchable and cmd.exe-wrapped shim paths without a real install or console.
    /// </summary>
    public InteractiveHarnessLauncher(
        IHarnessLauncher capabilityProbe,
        IInteractiveProcessRunner interactiveRunner,
        string executable,
        Func<string, ResolvedExecutable> resolve,
        Func<string> mintSessionKey)
    {
        _capabilityProbe = capabilityProbe;
        _interactiveRunner = interactiveRunner;
        _executable = executable;
        _resolve = resolve;
        _mintSessionKey = mintSessionKey;
    }

    /// <summary>The stable harness identifier this launcher serves.</summary>
    public string Harness => _capabilityProbe.Harness;

    /// <summary>
    /// Mints a Scope-owned interactive session key of the form <c>cli-{uuid7}</c> using the same
    /// uuid7 helper <c>ObservabilityContext</c> uses. Never derived from a process id.
    /// </summary>
    public static string MintSessionKey() => SessionKeyPrefix + Guid.GetGuid7String();

    /// <summary>
    /// Mints the session key, discovers the harness binary, sets <c>SCOPE_SESSION_KEY</c> in the
    /// child environment ONLY, and launches the harness interactively with the user's passthrough
    /// args, returning its exit code. On a missing/unlaunchable binary it returns an
    /// <see cref="InteractiveLaunchResult"/> carrying the existing capability message and a clean
    /// non-zero exit code, never an exception.
    /// </summary>
    /// <param name="passthroughArgs">User arguments forwarded verbatim to the harness binary.</param>
    /// <param name="workingDirectory">Working directory for the child; null uses the current directory.</param>
    /// <param name="proxyWiring">
    /// Optional metering-proxy wiring. When supplied, the proxy base URL (and optionally the API key)
    /// are set in the CHILD environment only, via the env-var names the caller sourced from the host
    /// adapter constants. Null leaves the child env byte-identical to the un-metered path (only
    /// <c>SCOPE_SESSION_KEY</c> beyond the inherited environment).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<InteractiveLaunchResult> LaunchAsync(
        IReadOnlyList<string> passthroughArgs,
        string? workingDirectory = null,
        InteractiveProxyWiring? proxyWiring = null,
        CancellationToken cancellationToken = default)
    {
        // Probe capability first so a missing/old binary or unsupported OS yields the existing
        // discrete capability message rather than an opaque spawn failure.
        var capability = await _capabilityProbe.ProbeCapabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.IsAvailable)
        {
            return new InteractiveLaunchResult(
                SessionKey: null,
                Capability: capability,
                ExitCode: BinaryUnavailableExitCode,
                FailureReason: capability.Detail);
        }

        // Resolve the logical name to a concrete launchable form (direct .exe vs npm .cmd shim on
        // Windows). The capability probe already confirmed launchability, but resolving here keeps
        // the interactive launch self-contained and honest if PATH changed in between.
        var resolved = _resolve(_executable);
        if (!resolved.IsLaunchable)
        {
            var detail = resolved.Diagnostic
                ?? $"'{_executable}' was not found on PATH. Install it to enable this harness.";
            return new InteractiveLaunchResult(
                SessionKey: null,
                Capability: HarnessCapability.BinaryNotFound(detail),
                ExitCode: BinaryUnavailableExitCode,
                FailureReason: detail);
        }

        var sessionKey = _mintSessionKey();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Child-only: never written to the parent shell. Claude Code / Codex and every child
            // they spawn (hooks, MCP server) inherit it, so both capture channels resolve tier-1.
            [SessionEnvVar] = sessionKey,
        };

        // Optional metering wiring: route the child through the Scope wire-metering proxy. Env-var
        // NAMES come from the caller's host adapter constants so no provider string is hardcoded here.
        // When proxyWiring is null this block is skipped and the child env stays byte-identical to the
        // un-metered subscription path.
        //
        // BaseUrlEnvVar is nullable: Claude supplies ANTHROPIC_BASE_URL; Codex routes via -c flags
        // (ExtraArgs) instead and leaves BaseUrlEnvVar null — so the guard skips the env-var write.
        if (proxyWiring is not null)
        {
            var baseUrlEnvVar = proxyWiring.BaseUrlEnvVar;
            if (!string.IsNullOrEmpty(baseUrlEnvVar))
                environment[baseUrlEnvVar] = proxyWiring.BaseUrl ?? "";
            if (!string.IsNullOrWhiteSpace(proxyWiring.ApiKeyEnvVar) && !string.IsNullOrWhiteSpace(proxyWiring.ApiKey))
                environment[proxyWiring.ApiKeyEnvVar] = proxyWiring.ApiKey;
        }

        // ExtraArgs from wiring are prepended to the user's passthrough args before launch. Used by
        // the Codex metered path to inject -c model_providers.scope.* flags that route /v1/responses
        // through the Scope OpenAI proxy without touching any env var. Claude wiring sets ExtraArgs to
        // null, leaving the passthrough byte-identical to the un-metered path.
        IReadOnlyList<string> effectiveArgs = proxyWiring?.ExtraArgs is { Count: > 0 } extraArgs
            ? [..extraArgs, ..passthroughArgs]
            : passthroughArgs;

        // A Windows .cmd shim must be launched through cmd.exe with the caller owning quoting; a
        // direct image uses ArgumentList. ExecutableResolver/ResolvedExecutable own this platform
        // knowledge so it does not leak into the runner or CLI layers.
        InteractiveProcessRunRequest runRequest;
        try
        {
            var rawArguments = resolved.BuildCmdRawArguments(effectiveArgs);
            runRequest = new InteractiveProcessRunRequest
            {
                FileName = resolved.LaunchFileName,
                Arguments = rawArguments is null ? effectiveArgs : [],
                RawArgumentString = rawArguments,
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
                Environment = environment,
            };
        }
        catch (CmdShimArgumentException ex)
        {
            // A passthrough arg contained a cmd metacharacter unsafe for the .cmd shim; refuse with
            // the actionable, secret-free diagnostic rather than reaching cmd.exe.
            return new InteractiveLaunchResult(
                SessionKey: sessionKey,
                Capability: HarnessCapability.BinaryNotFound(ex.Message),
                ExitCode: BinaryUnavailableExitCode,
                FailureReason: ex.Message);
        }

        int exitCode;
        try
        {
            exitCode = await _interactiveRunner.RunAsync(runRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            var detail = $"'{_executable}' (resolved to '{resolved.ResolvedPath}') could not be started.";
            return new InteractiveLaunchResult(
                SessionKey: sessionKey,
                Capability: HarnessCapability.BinaryNotFound(detail),
                ExitCode: BinaryUnavailableExitCode,
                FailureReason: detail);
        }

        // The harness ran: its exit code is the source of truth and passes through verbatim.
        return new InteractiveLaunchResult(
            SessionKey: sessionKey,
            Capability: capability,
            ExitCode: exitCode,
            FailureReason: null);
    }

    private static bool IsBinaryMissing(Exception ex)
        => ex is System.ComponentModel.Win32Exception or FileNotFoundException;
}

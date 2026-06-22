namespace Scope.Core.Cron.Launch;

/// <summary>
/// Host-independent request to launch a headless harness for one cron run. All host-specific
/// command construction happens inside the launcher; callers describe intent, not flags.
/// </summary>
public sealed record HarnessLaunchRequest
{
    /// <summary>Absolute path to the repository the harness runs against (its workspace root).</summary>
    public required string RepoPath { get; init; }

    /// <summary>The assembled prompt delivered to the harness on stdin.</summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Absolute path to the run artifact directory. Raw event stream, final message, stdout, and
    /// stderr are written here. The launcher creates it if it does not exist.
    /// </summary>
    public required string ArtifactDirectory { get; init; }

    /// <summary>Optional model override applied for this single run; null uses the host default.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Filesystem access level granted to the harness. Defaults to <see cref="HarnessSandbox.WorkspaceWrite"/>.
    /// <see cref="HarnessSandbox.FullAccess"/> is never selected implicitly and is reserved for an
    /// explicit future controlled-run option.
    /// </summary>
    public HarnessSandbox Sandbox { get; init; } = HarnessSandbox.WorkspaceWrite;

    /// <summary>
    /// Dispatch ID injected into the child process only, for trace correlation. Null omits it.
    /// </summary>
    public string? DispatchId { get; init; }

    /// <summary>
    /// Session key injected into the child process only, for trace correlation. Null omits it.
    /// </summary>
    public string? SessionKey { get; init; }

    /// <summary>
    /// Provider-specific proxy/base-url variables injected into the child process only, used by
    /// wire-metering. Keys and values are passed through verbatim and never persisted by Scope.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ProxyEnvironment { get; init; }

    /// <summary>
    /// Host-independent base URL of an advertised Scope metering proxy, or null when none is
    /// running. Each adapter maps it to its provider's base-url variable, or honestly ignores it
    /// when Scope's proxy cannot meter that provider yet (visible degradation: the run is recorded
    /// with a null wire cost). The value may embed a proxy token and must never be logged or
    /// written into artifacts.
    /// </summary>
    public string? ProxyBaseUrl { get; init; }

    /// <summary>
    /// Optional one-invocation API key applied only to the child process. It is never persisted,
    /// logged, or written into artifacts. Null leaves the host to resolve its own managed auth.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Enforced total-wire-token budget for this run (input + output + cache), or null when the run is
    /// unbudgeted. The launcher surfaces it to the agent as an up-front context note (so a cooperative
    /// harness can plan to finish within it) and the supervisor force-kills the child when cumulative
    /// tokens reach <see cref="CronRunawayGuard.HardKillThreshold"/>. Distinct from the proxy's terminal
    /// refusal at exactly the budget; this is the host-independent declaration of the cap.
    /// </summary>
    public int? TokenBudget { get; init; }

    /// <summary>
    /// Effective wall-clock cutoff (seconds) for this run: the per-agent override or the global
    /// supervisor default, already resolved by the runner. The supervisor force-kills a run that
    /// exceeds it, which catches a local loop that burns no API tokens (invisible to the proxy).
    /// Zero or null means no wall-clock guard for this launch (callers that want the guard pass a
    /// positive value).
    /// </summary>
    public int? MaxRuntimeSeconds { get; init; }

    /// <summary>
    /// Redacts the API key and elides prompt content so formatting this request for logs or
    /// diagnostics can never leak the secret or the assembled context.
    /// </summary>
    public override string ToString()
        => $"HarnessLaunchRequest {{ RepoPath = {RepoPath}, ArtifactDirectory = {ArtifactDirectory}, " +
           $"Model = {Model ?? "null"}, Sandbox = {Sandbox}, DispatchId = {DispatchId ?? "null"}, " +
           $"SessionKey = {SessionKey ?? "null"}, PromptLength = {Prompt.Length}, " +
           $"ApiKey = {(ApiKey is null ? "null" : "[redacted]")} }}";
}

/// <summary>
/// Filesystem access level requested for a harness run. Mapped to host-specific sandbox flags
/// inside the adapter.
/// </summary>
public enum HarnessSandbox
{
    /// <summary>Read-only access. Used for capability smoke checks.</summary>
    ReadOnly,

    /// <summary>Read and write within the workspace root only. The Scope default for cron runs.</summary>
    WorkspaceWrite,

    /// <summary>Unrestricted access. Never selected by default; reserved for an explicit future option.</summary>
    FullAccess,
}

/// <summary>
/// Outcome of a single harness launch. The exit code is authoritative for success; event-stream
/// fields only enrich diagnostics and trace linkage.
/// </summary>
public sealed record HarnessLaunchResult
{
    /// <summary>Capability state observed at launch time. Non-available states mean the run did not start.</summary>
    public required HarnessCapability Capability { get; init; }

    /// <summary>True only when the harness was available and the process exited with code 0.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Process exit code when the harness ran; null when it never started.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Human-readable failure reason for surfacing in run history; null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>UTC start timestamp (ISO 8601 round-trip), null when the process never started.</summary>
    public string? StartedAt { get; init; }

    /// <summary>UTC completion timestamp (ISO 8601 round-trip), null when the process never started.</summary>
    public string? CompletedAt { get; init; }

    /// <summary>Artifact paths written for this run.</summary>
    public HarnessArtifacts Artifacts { get; init; } = HarnessArtifacts.None;

    /// <summary>Host thread/session id parsed from the event stream when available; null otherwise.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Token/usage rollup parsed from the event stream when present; null otherwise.</summary>
    public HarnessUsage? Usage { get; init; }
}

/// <summary>
/// Absolute paths to the run artifacts captured under the request artifact directory.
/// </summary>
public sealed record HarnessArtifacts(
    string? RawEventStreamPath,
    string? FinalMessagePath,
    string? StdoutPath,
    string? StderrPath)
{
    /// <summary>An empty artifact set for runs that never produced output.</summary>
    public static readonly HarnessArtifacts None = new(null, null, null, null);
}

/// <summary>
/// Content-free usage rollup parsed from a harness event stream. Token counts are safe to keep in
/// metadata and trace fields; no message content is carried here.
/// </summary>
public sealed record HarnessUsage(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? TotalTokens);

/// <summary>
/// Discrete capability states a frontend can render distinctly when a harness cannot run.
/// </summary>
public enum HarnessCapabilityStatus
{
    /// <summary>The harness is installed, supported, and ready to run.</summary>
    Available,

    /// <summary>The harness binary was not found on PATH or at the configured location.</summary>
    BinaryNotFound,

    /// <summary>The harness CLI was found but is too old or otherwise unsupported by Scope.</summary>
    UnsupportedCli,

    /// <summary>The harness is installed but has no usable authentication configured.</summary>
    MissingAuth,

    /// <summary>The current operating system or configuration cannot run this harness.</summary>
    UnsupportedOs,
}

/// <summary>
/// Result of probing whether a harness can run, carrying a discrete status and an actionable
/// human-readable detail string for display.
/// </summary>
public sealed record HarnessCapability(HarnessCapabilityStatus Status, string Detail)
{
    /// <summary>True only when the harness is ready to run.</summary>
    public bool IsAvailable => Status == HarnessCapabilityStatus.Available;

    /// <summary>Creates an available capability with an optional detected version detail.</summary>
    public static HarnessCapability Available(string detail = "Harness is available.")
        => new(HarnessCapabilityStatus.Available, detail);

    /// <summary>Creates a binary-not-found capability.</summary>
    public static HarnessCapability BinaryNotFound(string detail)
        => new(HarnessCapabilityStatus.BinaryNotFound, detail);

    /// <summary>Creates an unsupported-CLI capability.</summary>
    public static HarnessCapability UnsupportedCli(string detail)
        => new(HarnessCapabilityStatus.UnsupportedCli, detail);

    /// <summary>Creates a missing-auth capability.</summary>
    public static HarnessCapability MissingAuth(string detail)
        => new(HarnessCapabilityStatus.MissingAuth, detail);

    /// <summary>Creates an unsupported-OS capability.</summary>
    public static HarnessCapability UnsupportedOs(string detail)
        => new(HarnessCapabilityStatus.UnsupportedOs, detail);
}

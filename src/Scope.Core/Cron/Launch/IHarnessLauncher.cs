namespace Scope.Core.Cron.Launch;

/// <summary>
/// Host-independent contract for launching a headless agent harness on behalf of a cron run.
/// Implementations adapt a specific host (for example Codex or Claude Code) behind this
/// Scope-owned boundary. No host-specific vocabulary, flags, or env-var names appear on this
/// interface; that knowledge lives only inside the adapter implementation.
/// </summary>
public interface IHarnessLauncher
{
    /// <summary>
    /// Stable harness identifier this launcher serves (see <see cref="CronHarnesses"/>).
    /// </summary>
    string Harness { get; }

    /// <summary>
    /// Probes whether this harness can run in the current environment. The result reports a
    /// discrete <see cref="HarnessCapabilityStatus"/> so a frontend can distinguish a missing
    /// binary from an unsupported CLI, missing auth, or an unsupported OS/config.
    /// </summary>
    Task<HarnessCapability> ProbeCapabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches the harness for a single cron run. The process exit code is the source of truth
    /// for success or failure; any host event-stream diagnostics only enrich the result. The
    /// returned <see cref="HarnessLaunchResult"/> records artifact paths, usage totals, and any
    /// capability degradation reason. Implementations must never throw for an unmet capability;
    /// they return a result whose <see cref="HarnessLaunchResult.Capability"/> explains the gap.
    /// </summary>
    Task<HarnessLaunchResult> LaunchAsync(HarnessLaunchRequest request, CancellationToken cancellationToken = default);
}

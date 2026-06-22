namespace Scope.Core.Cron.Run;

using Scope.Core.Cron.Launch;

/// <summary>
/// Scope-owned factory that selects the <see cref="IHarnessLauncher"/> for a harness id. This is
/// the only place that maps a definition's harness string ('claude-code', 'codex') to a concrete
/// adapter; the runner consumes launchers only through this seam and never names a host directly.
/// Tests pass a registry seeded with fake launchers; production seeds the real adapters.
/// </summary>
public sealed class CronLauncherRegistry
{
    private readonly Dictionary<string, IHarnessLauncher> _launchers;

    /// <summary>
    /// Builds a registry from a set of launchers, keyed by each launcher's <see cref="IHarnessLauncher.Harness"/>.
    /// A duplicate harness id is a programming error and throws.
    /// </summary>
    public CronLauncherRegistry(IEnumerable<IHarnessLauncher> launchers)
    {
        _launchers = new Dictionary<string, IHarnessLauncher>(StringComparer.Ordinal);
        foreach (var launcher in launchers)
        {
            if (!_launchers.TryAdd(launcher.Harness, launcher))
                throw new ArgumentException($"Duplicate harness launcher registered: {launcher.Harness}", nameof(launchers));
        }
    }

    /// <summary>
    /// Builds the default production registry: the real Codex and Claude Code adapters over the
    /// system process runner. Capability probes report each adapter's honest availability, which
    /// the runner and the capabilities endpoint surface unchanged.
    /// </summary>
    public static CronLauncherRegistry CreateDefault()
        => new(
        [
            new CodexHarnessLauncher(new SystemProcessRunner()),
            new ClaudeCodeHarnessLauncher(new SystemProcessRunner()),
        ]);

    /// <summary>The harness ids this registry can serve, in stable sorted order.</summary>
    public IReadOnlyList<string> SupportedHarnesses
        => _launchers.Keys.OrderBy(h => h, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Resolves the launcher for a harness id, or null when no adapter is registered for it.
    /// </summary>
    public IHarnessLauncher? Resolve(string harness)
        => _launchers.GetValueOrDefault(harness);

    /// <summary>All registered launchers in stable sorted order by harness id.</summary>
    public IReadOnlyList<IHarnessLauncher> All
        => SupportedHarnesses.Select(h => _launchers[h]).ToList();
}

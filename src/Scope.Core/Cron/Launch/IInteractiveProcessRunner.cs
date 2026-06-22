namespace Scope.Core.Cron.Launch;

/// <summary>
/// Fully described request to run an external process INTERACTIVELY: the child inherits the parent's
/// console (stdin/stdout/stderr are NOT redirected) so a terminal UI works as a transparent
/// passthrough. This is the interactive analogue of <see cref="ProcessRunRequest"/>, which instead
/// captures output for headless runs. Environment overrides are applied to the child process only.
/// </summary>
public sealed record InteractiveProcessRunRequest
{
    /// <summary>Executable file name or path to launch (for a Windows .cmd shim this is cmd.exe).</summary>
    public required string FileName { get; init; }

    /// <summary>Ordered argument list, applied via ProcessStartInfo.ArgumentList for safe quoting.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Pre-formatted raw argument string applied verbatim via ProcessStartInfo.Arguments instead of
    /// <see cref="Arguments"/>. Used only when the caller must own quoting itself (a Windows
    /// <c>.cmd</c> shim launched through <c>cmd.exe</c>). Null uses <see cref="Arguments"/>.
    /// </summary>
    public string? RawArgumentString { get; init; }

    /// <summary>Working directory for the child process.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Environment overrides applied to the child process only. Values are never persisted.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Injectable seam over INTERACTIVE process execution. Unlike <see cref="IProcessRunner"/> (which
/// redirects and captures stdout/stderr for headless cron runs), the production implementation lets
/// the child inherit the parent's console so a terminal UI such as the interactive Claude Code or
/// Codex harness renders directly. The seam exists so the launch wrapper can be unit-tested without
/// spawning a real harness binary or attaching to a real console.
/// </summary>
public interface IInteractiveProcessRunner
{
    /// <summary>
    /// Runs the described process to completion with inherited stdio, returning its exit code.
    /// Throws when the executable cannot be started (for example a missing binary) so callers can
    /// map that to a capability diagnostic.
    /// </summary>
    Task<int> RunAsync(InteractiveProcessRunRequest request, CancellationToken cancellationToken = default);
}

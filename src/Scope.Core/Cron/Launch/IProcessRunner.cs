namespace Scope.Core.Cron.Launch;

/// <summary>
/// Fully described request to run an external process. The runner sets these fields on
/// <see cref="System.Diagnostics.ProcessStartInfo"/> using its argument list (never a shell-built
/// command string) and applies <see cref="Environment"/> entries to the child process only.
/// </summary>
public sealed record ProcessRunRequest
{
    /// <summary>Executable file name or path to launch.</summary>
    public required string FileName { get; init; }

    /// <summary>Ordered argument list, applied via ProcessStartInfo.ArgumentList for safe quoting.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Pre-formatted raw argument string applied verbatim via ProcessStartInfo.Arguments instead of
    /// <see cref="Arguments"/>. Used only when the caller must own quoting itself, for example
    /// wrapping a Windows <c>.cmd</c> shim through <c>cmd.exe /d /s /c "..."</c>, whose parsing rules
    /// differ from the MSVCRT-style quoting .NET applies to ArgumentList. Null uses <see cref="Arguments"/>.
    /// </summary>
    public string? RawArgumentString { get; init; }

    /// <summary>Working directory for the child process.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Environment overrides applied to the child process only. Values are never persisted.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Text piped to the child's standard input; null sends no stdin.</summary>
    public string? StandardInput { get; init; }
}

/// <summary>
/// Captured outcome of running an external process.
/// </summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Injectable seam over process execution so launchers can be unit-tested without a real
/// binary or network access. The production implementation uses
/// <see cref="System.Diagnostics.Process"/>.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the described process to completion, capturing stdout and stderr separately. Throws
    /// when the executable cannot be started (for example a missing binary) so callers can map
    /// that to a capability diagnostic.
    /// </summary>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}

namespace Scope.Core.Cron.Launch;

using System.Diagnostics;

/// <summary>
/// Production <see cref="IInteractiveProcessRunner"/> backed by <see cref="Process"/>. The child
/// INHERITS the parent's console: stdin/stdout/stderr are not redirected, so a terminal UI renders
/// as a transparent passthrough. Arguments are supplied via <see cref="ProcessStartInfo.ArgumentList"/>
/// for OS-safe quoting; a caller that owns its own quoting (a Windows <c>.cmd</c> shim wrapped
/// through <c>cmd.exe</c>) supplies <see cref="InteractiveProcessRunRequest.RawArgumentString"/>
/// instead. Environment overrides apply to the spawned child only.
/// <para>
/// Windows has no <c>exec()</c>-style image replacement, so the wrapper spawns the harness as a
/// child and forwards its exit code. Because stdio is inherited (not piped), the Console.CancelKeyPress
/// / Ctrl+C the user sends reaches the foreground child directly; the wrapper simply waits for it to
/// exit and returns its code.
/// </para>
/// </summary>
public sealed class SystemInteractiveProcessRunner : IInteractiveProcessRunner
{
    /// <inheritdoc />
    public async Task<int> RunAsync(InteractiveProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            // UseShellExecute=false keeps env overrides honored and the child attached to this
            // console; leaving the standard streams un-redirected makes stdio a direct passthrough
            // so the harness's terminal UI works exactly as if the user launched it themselves.
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            CreateNoWindow = false,
        };

        if (request.RawArgumentString is not null)
        {
            startInfo.Arguments = request.RawArgumentString;
        }
        else
        {
            foreach (var argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in request.Environment)
            startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

namespace Scope.Core.Cron.Launch;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Production <see cref="IProcessRunner"/> backed by <see cref="Process"/>. Arguments are supplied
/// via <see cref="ProcessStartInfo.ArgumentList"/> so the OS handles quoting safely on Windows and
/// POSIX. When a caller must own quoting itself (a cmd.exe wrapper around a Windows <c>.cmd</c> shim),
/// it supplies <see cref="ProcessRunRequest.RawArgumentString"/>, which is applied verbatim instead.
/// Environment overrides are applied to the spawned child process only.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            CreateNoWindow = true,
        };

        if (request.RawArgumentString is not null)
        {
            // The caller owns quoting (cmd.exe wrapper); pass the command line through untouched.
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

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (request.StandardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child exited before consuming stdin (broken pipe); its exit code and
                // captured output remain authoritative, so the run result is still honest.
            }
            catch (OperationCanceledException)
            {
                // Cancellation arrived while priming stdin (e.g. the runaway guard tripped immediately).
                // Fall through to the load-bearing kill below so the child is never left running.
            }
        }

        // LOAD-BEARING force-kill (T-2026-06-17-06): when the caller cancels (the runaway guard tripped
        // on token budget or wall-clock), terminate the child process TREE so a spinning or
        // refusal-ignoring harness cannot keep running. WaitForExitAsync(ct) throws on cancel but leaves
        // the process alive; we must kill it explicitly, then surface the cancellation to the caller so
        // the run is recorded as stopped-by-guard rather than completed.
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    // Bounded reap so a killed tree's exit code/streams are flushed before we return.
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best effort: the process may have exited between the check and the kill.
            }
            throw;
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

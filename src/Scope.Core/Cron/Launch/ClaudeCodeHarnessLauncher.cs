namespace Scope.Core.Cron.Launch;

using System.Runtime.InteropServices;

/// <summary>
/// Claude Code adapter for <see cref="IHarnessLauncher"/>. All Claude Code-specific command shape,
/// flag names, and env-var knowledge live here, behind the host-independent contract
/// (platform-independence rule). The non-interactive command is:
/// <code>
/// claude -p --output-format json --permission-mode {mode} [--dangerously-skip-permissions]
///   [--model {model}]
/// </code>
/// run with the working directory set to the repo path and the assembled prompt delivered on
/// stdin (headless <c>-p</c> reads the prompt from stdin when no prompt argument is given;
/// <c>--input-format text</c> is the default). The process exit code is the source of truth for
/// success; the result JSON's <c>is_error</c>/<c>subtype</c> fields enrich diagnostics only.
/// CronRunner writes deterministic project-local <c>.claude/settings.json</c> hook config before
/// invoking this launcher, so Claude Code cron runs get the same noninteractive activation story as
/// Codex without writing global Claude configuration.
/// </summary>
/// <remarks>
/// Flag surface verified against Claude Code CLI 2.1.175 via <c>claude --help</c>:
/// <list type="bullet">
/// <item><c>-p, --print</c> Print response and exit (headless).</item>
/// <item><c>--output-format json</c> single result object with session_id, usage, total_cost_usd.</item>
/// <item><c>--model</c> model alias or full name for the run.</item>
/// <item><c>--permission-mode</c> choices: acceptEdits, auto, bypassPermissions, default, dontAsk, plan.</item>
/// <item><c>--dangerously-skip-permissions</c> bypass all permission checks (FullAccess only).</item>
/// </list>
/// Claude Code has no strict filesystem read-only mode. <see cref="HarnessSandbox.ReadOnly"/> maps to
/// <c>--permission-mode plan</c>, the most restrictive non-interactive mode: the agent plans without
/// applying edits. This is documented honestly here because it is policy-level, not a kernel sandbox.
/// </remarks>
public sealed class ClaudeCodeHarnessLauncher(IProcessRunner processRunner, string executable = ClaudeCodeHarnessLauncher.DefaultExecutable)
    : IHarnessLauncher
{
    /// <summary>Default Claude Code CLI executable resolved from PATH.</summary>
    public const string DefaultExecutable = "claude";

    /// <summary>File name written under the artifact directory for the raw result JSON object.</summary>
    public const string ResultJsonFileName = "result.json";

    /// <summary>File name written under the artifact directory for captured stdout (the result JSON).</summary>
    public const string StdoutFileName = "stdout.log";

    /// <summary>File name written under the artifact directory for captured stderr.</summary>
    public const string StderrFileName = "stderr.log";

    /// <summary>
    /// Claude Code one-invocation API key env var, the analogue of Codex's CODEX_API_KEY. Scoped to
    /// the child process and never persisted, logged, or written into artifacts or arguments.
    /// </summary>
    public const string ApiKeyEnvVar = "ANTHROPIC_API_KEY";

    /// <summary>The base-url variable this adapter maps the host-independent proxy base URL onto.</summary>
    public const string ProxyBaseUrlEnvVar = "ANTHROPIC_BASE_URL";

    private const string DispatchEnvVar = "SCOPE_DISPATCH_ID";
    private const string SessionEnvVar = "SCOPE_SESSION_KEY";

    /// <inheritdoc />
    public string Harness => CronHarnesses.ClaudeCode;

    /// <inheritdoc />
    public async Task<HarnessCapability> ProbeCapabilityAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
            return HarnessCapability.UnsupportedOs("Claude Code cannot run on this operating system.");

        ProcessRunResult result;
        try
        {
            result = await processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = executable,
                    Arguments = ["--version"],
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            return HarnessCapability.BinaryNotFound(
                $"Claude Code CLI '{executable}' was not found on PATH. Install Claude Code to enable this harness.");
        }

        if (result.ExitCode != 0)
        {
            return HarnessCapability.UnsupportedCli(
                $"Claude Code CLI '{executable}' returned exit code {result.ExitCode} for --version. Update Claude Code to a supported version.");
        }

        var version = result.StandardOutput.Trim();
        return HarnessCapability.Available(version.Length == 0 ? "Claude Code is available." : $"Claude Code {version}");
    }

    /// <inheritdoc />
    public async Task<HarnessLaunchResult> LaunchAsync(HarnessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        var capability = await ProbeCapabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.IsAvailable)
        {
            return new HarnessLaunchResult
            {
                Capability = capability,
                Succeeded = false,
                FailureReason = capability.Detail,
            };
        }

        Directory.CreateDirectory(request.ArtifactDirectory);
        var resultJsonPath = Path.Combine(request.ArtifactDirectory, ResultJsonFileName);
        var stdoutPath = Path.Combine(request.ArtifactDirectory, StdoutFileName);
        var stderrPath = Path.Combine(request.ArtifactDirectory, StderrFileName);

        var runRequest = new ProcessRunRequest
        {
            FileName = executable,
            Arguments = BuildArguments(request),
            WorkingDirectory = request.RepoPath,
            Environment = BuildChildEnvironment(request),
            // Headless -p reads the prompt from stdin (default --input-format text). Keeping the
            // prompt off the argument list avoids OS arg-length limits and never logs context.
            // The up-front token-budget note (decision 2) is prepended here in the host adapter so
            // host-shape injection stays out of Scope.Core (platform-independence rule).
            StandardInput = BudgetNotice.PrependTo(request.Prompt, request.TokenBudget),
        };

        var startedAt = DateTime.UtcNow.ToString("o");
        ProcessRunResult processResult;
        try
        {
            processResult = await processRunner.RunAsync(runRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            return new HarnessLaunchResult
            {
                Capability = HarnessCapability.BinaryNotFound(
                    $"Claude Code CLI '{executable}' was not found on PATH at launch."),
                Succeeded = false,
                FailureReason = $"Claude Code CLI '{executable}' was not found on PATH at launch.",
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow.ToString("o"),
            };
        }

        var completedAt = DateTime.UtcNow.ToString("o");

        // --output-format json emits a single result object on stdout. Persist raw streams for review.
        await File.WriteAllTextAsync(resultJsonPath, processResult.StandardOutput, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(stdoutPath, processResult.StandardOutput, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(stderrPath, processResult.StandardError, cancellationToken).ConfigureAwait(false);

        var summary = ClaudeCodeResultParser.Parse(processResult.StandardOutput);
        var succeeded = processResult.ExitCode == 0;

        return new HarnessLaunchResult
        {
            Capability = capability,
            Succeeded = succeeded,
            ExitCode = processResult.ExitCode,
            FailureReason = succeeded ? null : BuildFailureReason(processResult.ExitCode, summary.ErrorDetail),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            // The result JSON is both the structured stream and the final-message artifact for Claude.
            Artifacts = new HarnessArtifacts(resultJsonPath, resultJsonPath, stdoutPath, stderrPath),
            ThreadId = summary.SessionId,
            Usage = summary.Usage,
        };
    }

    private static List<string> BuildArguments(HarnessLaunchRequest request)
    {
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--permission-mode", MapPermissionMode(request.Sandbox),
        };

        // FullAccess is the only mode that escalates to the explicit dangerous flag; never implicit.
        if (request.Sandbox == HarnessSandbox.FullAccess)
            arguments.Add("--dangerously-skip-permissions");

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        return arguments;
    }

    private static Dictionary<string, string> BuildChildEnvironment(HarnessLaunchRequest request)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(request.DispatchId))
            environment[DispatchEnvVar] = request.DispatchId;

        if (!string.IsNullOrWhiteSpace(request.SessionKey))
            environment[SessionEnvVar] = request.SessionKey;

        // The Scope metering proxy speaks the Anthropic wire protocol, so this adapter owns the
        // mapping of the host-independent ProxyBaseUrl to ANTHROPIC_BASE_URL.
        if (!string.IsNullOrWhiteSpace(request.ProxyBaseUrl))
            environment[ProxyBaseUrlEnvVar] = request.ProxyBaseUrl;

        if (request.ProxyEnvironment is not null)
        {
            // Proxy wiring is non-destructive: pass provider vars through to the child verbatim.
            foreach (var (key, value) in request.ProxyEnvironment)
                environment[key] = value;
        }

        // ANTHROPIC_API_KEY is applied to the child process only and is never persisted or logged.
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            environment[ApiKeyEnvVar] = request.ApiKey;

        return environment;
    }

    /// <summary>
    /// Maps the host-independent sandbox intent to a Claude Code <c>--permission-mode</c>.
    /// WorkspaceWrite (the default) uses <c>acceptEdits</c>, which applies file edits within the
    /// working directory without prompting. FullAccess uses <c>bypassPermissions</c> and is further
    /// gated by the explicit <c>--dangerously-skip-permissions</c> flag; it is never the default.
    /// ReadOnly uses <c>plan</c>, the most restrictive non-interactive mode Claude Code offers (the
    /// agent plans without applying edits); Claude Code has no strict kernel-level read-only sandbox.
    /// </summary>
    private static string MapPermissionMode(HarnessSandbox sandbox) => sandbox switch
    {
        HarnessSandbox.ReadOnly => "plan",
        HarnessSandbox.WorkspaceWrite => "acceptEdits",
        HarnessSandbox.FullAccess => "bypassPermissions",
        _ => "acceptEdits",
    };

    private static string BuildFailureReason(int exitCode, string? errorDetail)
        => errorDetail is null
            ? $"Claude Code exited with code {exitCode}."
            : $"Claude Code exited with code {exitCode}: {errorDetail}";

    private static bool IsBinaryMissing(Exception ex)
        => ex is System.ComponentModel.Win32Exception or FileNotFoundException;
}

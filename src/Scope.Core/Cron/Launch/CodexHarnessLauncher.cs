namespace Scope.Core.Cron.Launch;

using System.Runtime.InteropServices;

/// <summary>
/// Codex adapter for <see cref="IHarnessLauncher"/>. All Codex-specific command shape, flag names,
/// and env-var knowledge live here, behind the host-independent contract. The non-interactive
/// command is:
/// <code>
/// codex exec --cd {repoPath} --sandbox {sandbox} --ask-for-approval never --json
///   --dangerously-bypass-hook-trust --output-last-message {artifactDir}/final-message.md [-m {model}] -
/// </code>
/// with the assembled prompt delivered on stdin. The process exit code is the source of truth for
/// success; JSONL error events enrich diagnostics only.
/// CronRunner writes Scope's deterministic project-local <c>.codex/hooks.json</c> before invoking
/// this launcher; <c>--dangerously-bypass-hook-trust</c> is used only for that vetted automation path,
/// matching Codex's documented "automation that already vets hook sources" intent.
/// <para>
/// The logical executable name is resolved against PATH (respecting PATHEXT precedence) before each
/// probe and launch via <see cref="ExecutableResolver"/>. This lets the adapter detect and run the
/// standard npm Windows install shape, where <c>codex</c> is a <c>codex.cmd</c> shim that Win32
/// <c>CreateProcess</c> cannot start directly; such shims are launched through <c>cmd.exe</c>. The
/// prompt is still delivered on stdin in every case, never on the command line.
/// </para>
/// </summary>
public sealed class CodexHarnessLauncher : IHarnessLauncher
{
    /// <summary>Default Codex CLI logical name resolved from PATH (no extension; .exe/.cmd resolved at runtime).</summary>
    public const string DefaultExecutable = "codex";

    /// <summary>File name written under the artifact directory for the final agent message.</summary>
    public const string FinalMessageFileName = "final-message.md";

    /// <summary>File name written under the artifact directory for the raw JSONL event stream.</summary>
    public const string RawEventStreamFileName = "events.jsonl";

    /// <summary>File name written under the artifact directory for captured stdout.</summary>
    public const string StdoutFileName = "stdout.log";

    /// <summary>File name written under the artifact directory for captured stderr.</summary>
    public const string StderrFileName = "stderr.log";

    /// <summary>Codex-only one-invocation API key env var. Scoped to the child process and never persisted.</summary>
    public const string ApiKeyEnvVar = "CODEX_API_KEY";

    private const string DispatchEnvVar = "SCOPE_DISPATCH_ID";
    private const string SessionEnvVar = "SCOPE_SESSION_KEY";

    /// <summary>
    /// Default interval at which the final-message sentinel (see <see cref="LaunchAsync"/>) polls
    /// for <see cref="FinalMessageFileName"/> to appear. Within the 2-5s band called for by
    /// T-2026-07-02-01: frequent enough to resolve a finished-but-hung <c>codex exec</c> run
    /// promptly, sparse enough to be negligible I/O overhead against an artifact directory.
    /// </summary>
    internal static readonly TimeSpan DefaultSentinelPollInterval = TimeSpan.FromSeconds(3);

    private readonly IProcessRunner _processRunner;
    private readonly string _executable;
    private readonly Func<string, ResolvedExecutable> _resolve;
    private readonly TimeSpan _sentinelPollInterval;
    private readonly Action? _onSentinelPollForTests;

    /// <summary>
    /// Creates the Codex adapter over a process runner, resolving the logical executable name
    /// against the live PATH/PATHEXT via <see cref="ExecutableResolver.Resolve(string, string?, string?)"/>.
    /// </summary>
    /// <param name="processRunner">Process execution seam (production: <see cref="SystemProcessRunner"/>).</param>
    /// <param name="executable">Logical Codex executable name; defaults to <see cref="DefaultExecutable"/>.</param>
    public CodexHarnessLauncher(IProcessRunner processRunner, string executable = DefaultExecutable)
        : this(processRunner, executable, name => ExecutableResolver.Resolve(name))
    {
    }

    /// <summary>
    /// Test-only constructor that injects the PATH-resolution seam so unit tests can exercise both
    /// the directly-launchable and cmd.exe-wrapped shim paths against a controlled PATH without
    /// depending on the machine's real Codex install.
    /// </summary>
    /// <param name="processRunner">Process execution seam.</param>
    /// <param name="executable">Logical Codex executable name.</param>
    /// <param name="resolve">Resolver mapping the logical name to a <see cref="ResolvedExecutable"/>.</param>
    public CodexHarnessLauncher(IProcessRunner processRunner, string executable, Func<string, ResolvedExecutable> resolve)
        : this(processRunner, executable, resolve, DefaultSentinelPollInterval)
    {
    }

    /// <summary>
    /// Test-only constructor that additionally injects the final-message sentinel's poll interval
    /// (and an optional per-poll test hook) so tests can exercise the codex-exec-hang fix
    /// (T-2026-07-02-01) deterministically and fast, without waiting on the production 2-5s cadence.
    /// </summary>
    /// <param name="processRunner">Process execution seam.</param>
    /// <param name="executable">Logical Codex executable name.</param>
    /// <param name="resolve">Resolver mapping the logical name to a <see cref="ResolvedExecutable"/>.</param>
    /// <param name="sentinelPollInterval">Interval between <see cref="FinalMessageFileName"/> existence checks.</param>
    /// <param name="onSentinelPollForTests">Invoked once per sentinel poll iteration; test-only observability hook.</param>
    internal CodexHarnessLauncher(
        IProcessRunner processRunner,
        string executable,
        Func<string, ResolvedExecutable> resolve,
        TimeSpan sentinelPollInterval,
        Action? onSentinelPollForTests = null)
    {
        _processRunner = processRunner;
        _executable = executable;
        _resolve = resolve;
        _sentinelPollInterval = sentinelPollInterval;
        _onSentinelPollForTests = onSentinelPollForTests;
    }

    /// <inheritdoc />
    public string Harness => CronHarnesses.Codex;

    /// <inheritdoc />
    public async Task<HarnessCapability> ProbeCapabilityAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
            return HarnessCapability.UnsupportedOs("Codex cannot run on this operating system.");

        var resolved = _resolve(_executable);
        if (!resolved.IsLaunchable)
            return HarnessCapability.BinaryNotFound(ResolutionDiagnostic(resolved));

        ProcessRunRequest probeRequest;
        try
        {
            // The probe passes no user-controlled arguments, but the resolved shim path itself is
            // validated here so a PATH directory containing a cmd metacharacter is reported honestly
            // rather than reaching cmd.exe.
            probeRequest = BuildProbeRequest(resolved);
        }
        catch (CmdShimArgumentException ex)
        {
            return HarnessCapability.BinaryNotFound(ex.Message);
        }

        ProcessRunResult result;
        try
        {
            result = await _processRunner.RunAsync(probeRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            return HarnessCapability.BinaryNotFound(
                $"Codex CLI '{_executable}' (resolved to '{resolved.ResolvedPath}') could not be started.");
        }

        if (result.ExitCode != 0)
        {
            return HarnessCapability.UnsupportedCli(
                $"Codex CLI '{_executable}' returned exit code {result.ExitCode} for --version. Update Codex to a supported version.");
        }

        var version = result.StandardOutput.Trim();
        return HarnessCapability.Available(version.Length == 0 ? "Codex is available." : $"Codex {version}");
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

        // Resolve once for the launch. The probe above already confirmed launchability, but
        // re-resolving keeps the launch path self-contained and honest if PATH changed in between.
        var resolved = _resolve(_executable);
        if (!resolved.IsLaunchable)
        {
            return new HarnessLaunchResult
            {
                Capability = HarnessCapability.BinaryNotFound(ResolutionDiagnostic(resolved)),
                Succeeded = false,
                FailureReason = ResolutionDiagnostic(resolved),
            };
        }

        Directory.CreateDirectory(request.ArtifactDirectory);
        var finalMessagePath = Path.Combine(request.ArtifactDirectory, FinalMessageFileName);
        var rawEventStreamPath = Path.Combine(request.ArtifactDirectory, RawEventStreamFileName);
        var stdoutPath = Path.Combine(request.ArtifactDirectory, StdoutFileName);
        var stderrPath = Path.Combine(request.ArtifactDirectory, StderrFileName);

        // User-controlled fields (model id, repo path) reach the .cmd command line, so building the
        // launch request can reject an injection/expansion vector. Turn that into an honest failure
        // result rather than an uncaught throw; the diagnostic is secret-free and actionable.
        ProcessRunRequest runRequest;
        try
        {
            runRequest = BuildLaunchRequest(resolved, request, finalMessagePath);
        }
        catch (CmdShimArgumentException ex)
        {
            return new HarnessLaunchResult
            {
                Capability = capability,
                Succeeded = false,
                FailureReason = ex.Message,
            };
        }

        var startedAt = DateTime.UtcNow.ToString("o");

        // T-2026-07-02-01: `codex exec` writes its --output-last-message artifact to
        // finalMessagePath but does NOT exit afterward, so a naive wall-clock guard would force-kill
        // an already-finished run and misclassify it as WallClock/failed. This sentinel polls for
        // the artifact concurrently and, on appearance, self-cancels the process via its OWN
        // cancellation source (never the caller's) so a genuine external/guard cancellation is
        // always distinguishable and always still kills + propagates (never swallowed).
        using var watcherOwnedCts = new CancellationTokenSource();
        using var sentinelLifetime = new CancellationTokenSource();
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watcherOwnedCts.Token);
        using var sentinelBoundCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sentinelLifetime.Token);
        var sentinelTask = WatchForFinalMessageAsync(
            finalMessagePath, watcherOwnedCts, sentinelBoundCts.Token, _sentinelPollInterval, _onSentinelPollForTests);

        ProcessRunResult processResult;
        try
        {
            try
            {
                processResult = await _processRunner.RunAsync(runRequest, runCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsBinaryMissing(ex))
            {
                var detail = $"Codex CLI '{_executable}' (resolved to '{resolved.ResolvedPath}') could not be started at launch.";
                return new HarnessLaunchResult
                {
                    Capability = HarnessCapability.BinaryNotFound(detail),
                    Succeeded = false,
                    FailureReason = detail,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow.ToString("o"),
                };
            }
            catch (OperationCanceledException) when (
                watcherOwnedCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && File.Exists(finalMessagePath))
            {
                // The sentinel found the artifact and self-cancelled the still-running process. The
                // caller did NOT ask to stop (checked above), so this is the codex-exec-hang case,
                // not a genuine guard kill: resolve as a SUCCESS from the on-disk artifacts.
                return await BuildSentinelSuccessResultAsync(
                    capability, finalMessagePath, rawEventStreamPath, stdoutPath, stderrPath, startedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Bounded sentinel: stop it and wait for it to actually finish before this method
            // returns, so it never outlives the process it was watching.
            await sentinelLifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await sentinelTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the sentinel's delay observes sentinelLifetime/cancellationToken ending.
            }
        }

        var completedAt = DateTime.UtcNow.ToString("o");

        // Codex --json writes JSONL events to stdout. Persist raw streams for local review.
        await File.WriteAllTextAsync(rawEventStreamPath, processResult.StandardOutput, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(stdoutPath, processResult.StandardOutput, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(stderrPath, processResult.StandardError, cancellationToken).ConfigureAwait(false);

        var summary = CodexEventStreamParser.Parse(processResult.StandardOutput);
        var succeeded = processResult.ExitCode == 0;

        return new HarnessLaunchResult
        {
            Capability = capability,
            Succeeded = succeeded,
            ExitCode = processResult.ExitCode,
            FailureReason = succeeded ? null : BuildFailureReason(processResult.ExitCode, summary.ErrorDetail),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Artifacts = new HarnessArtifacts(rawEventStreamPath, finalMessagePath, stdoutPath, stderrPath),
            ThreadId = summary.ThreadId,
            Usage = summary.Usage,
        };
    }

    /// <summary>
    /// Polls for <paramref name="finalMessagePath"/> to appear and, on appearance, cancels
    /// <paramref name="watcherOwnedCts"/> (never the caller's own token) so <see cref="LaunchAsync"/>
    /// can distinguish "our sentinel fired" from "the caller/guard cancelled" when it later inspects
    /// which source requested cancellation. Bounded by <paramref name="boundToken"/>, which the
    /// caller cancels once the process run has concluded for any reason, so this loop can never
    /// outlive the process it is watching. Never throws: a cancellation of <paramref name="boundToken"/>
    /// (external stop or normal process completion) simply ends the loop with no artifact found.
    /// </summary>
    private static async Task WatchForFinalMessageAsync(
        string finalMessagePath,
        CancellationTokenSource watcherOwnedCts,
        CancellationToken boundToken,
        TimeSpan pollInterval,
        Action? onPollForTests)
    {
        try
        {
            while (!boundToken.IsCancellationRequested)
            {
                onPollForTests?.Invoke();

                if (File.Exists(finalMessagePath))
                {
                    await watcherOwnedCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await Task.Delay(pollInterval, boundToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // boundToken ended (the process run concluded, or the caller/guard cancelled) before the
            // artifact appeared. The sentinel's job is done either way.
        }
    }

    /// <summary>
    /// Builds the SUCCESS result for the codex-exec-hang case (T-2026-07-02-01): the sentinel
    /// confirmed <paramref name="finalMessagePath"/> landed and force-cancelled the still-running
    /// process, so no exit code was ever produced. The stdout/stderr/JSONL stream that
    /// <see cref="SystemProcessRunner"/> was buffering is discarded on cancellation (it never returns
    /// a <see cref="ProcessRunResult"/> for a cancelled run), so this writes honest empty stubs for
    /// those artifacts rather than fabricating captured output; the exit code is normalized to 0 and
    /// thread id/usage stay null since they can only be parsed from the (unavailable) JSONL stream.
    /// </summary>
    private static async Task<HarnessLaunchResult> BuildSentinelSuccessResultAsync(
        HarnessCapability capability,
        string finalMessagePath,
        string rawEventStreamPath,
        string stdoutPath,
        string stderrPath,
        string startedAt,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(rawEventStreamPath))
            await File.WriteAllTextAsync(rawEventStreamPath, string.Empty, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(stdoutPath))
            await File.WriteAllTextAsync(stdoutPath, string.Empty, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(stderrPath))
            await File.WriteAllTextAsync(stderrPath, string.Empty, cancellationToken).ConfigureAwait(false);

        return new HarnessLaunchResult
        {
            Capability = capability,
            Succeeded = true,
            ExitCode = 0,
            FailureReason = null,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow.ToString("o"),
            Artifacts = new HarnessArtifacts(rawEventStreamPath, finalMessagePath, stdoutPath, stderrPath),
            ThreadId = null,
            Usage = null,
        };
    }

    /// <summary>
    /// Builds the <c>--version</c> probe request, routing a resolved <c>.cmd</c> shim through cmd.exe
    /// (so a Win32 image-only launch never throws) while a direct executable is launched as-is.
    /// </summary>
    private static ProcessRunRequest BuildProbeRequest(ResolvedExecutable resolved)
        => BuildResolvedRequest(
            resolved,
            ["--version"],
            workingDirectory: Directory.GetCurrentDirectory(),
            environment: null,
            standardInput: null);

    /// <summary>
    /// Builds the headless launch request, routing a resolved <c>.cmd</c> shim through cmd.exe. The
    /// prompt is delivered on stdin in every case (never on the command line), exactly as the
    /// pre-resolution launcher did; cmd.exe wrapping only changes how the executable is invoked.
    /// </summary>
    private static ProcessRunRequest BuildLaunchRequest(ResolvedExecutable resolved, HarnessLaunchRequest request, string finalMessagePath)
        => BuildResolvedRequest(
            resolved,
            BuildArguments(request, finalMessagePath),
            workingDirectory: request.RepoPath,
            environment: BuildChildEnvironment(request),
            // Prepend the up-front token-budget note (decision 2) in the host adapter; the prompt is
            // still delivered on stdin so host-shape injection stays out of Scope.Core.
            standardInput: BudgetNotice.PrependTo(request.Prompt, request.TokenBudget));

    /// <summary>
    /// Maps a resolved executable plus a logical argument list onto a <see cref="ProcessRunRequest"/>.
    /// For a directly-launchable image the args go through ArgumentList (OS-safe quoting); for a
    /// command-script shim the args are pre-quoted into a single cmd.exe command line so cmd.exe,
    /// not the MSVCRT parser, governs argument splitting.
    /// </summary>
    private static ProcessRunRequest BuildResolvedRequest(
        ResolvedExecutable resolved,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput)
    {
        var rawArguments = resolved.BuildCmdRawArguments(arguments);
        return new ProcessRunRequest
        {
            FileName = resolved.LaunchFileName,
            // When wrapping via cmd.exe, RawArgumentString owns quoting and Arguments is unused.
            Arguments = rawArguments is null ? arguments : [],
            RawArgumentString = rawArguments,
            WorkingDirectory = workingDirectory,
            Environment = environment ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StandardInput = standardInput,
        };
    }

    private static string ResolutionDiagnostic(ResolvedExecutable resolved)
        => resolved.Diagnostic
           ?? $"Codex CLI '{DefaultExecutable}' was not found on PATH. Install Codex to enable this harness.";

    private static List<string> BuildArguments(HarnessLaunchRequest request, string finalMessagePath)
    {
        var arguments = new List<string>
        {
            "exec",
            "--cd", request.RepoPath,
            "--sandbox", MapSandbox(request.Sandbox),
            "--ask-for-approval", "never",
            "--json",
            "--dangerously-bypass-hook-trust",
            "--output-last-message", finalMessagePath,
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("-m");
            arguments.Add(request.Model);
        }

        // Metered path: route /v1/responses through the OpenAI metering proxy via a custom Codex
        // model_providers entry, verified on real Codex 0.141 traffic. Gated on BOTH a proxy URL
        // AND an api key being present; missing either keeps the run on the direct un-metered path
        // (ChatGPT-subscription / no-key case), mirroring InteractiveMeterGate's subscription
        // carve-out. The env_key tells Codex to read the api key from CODEX_API_KEY, which
        // BuildChildEnvironment sets explicitly for this run.
        if (!string.IsNullOrWhiteSpace(request.ProxyBaseUrl) && !string.IsNullOrWhiteSpace(request.ApiKey))
            arguments.AddRange(BuildOpenAiMeteringArgs(request.ProxyBaseUrl));

        // Trailing '-' tells Codex to read the prompt from stdin.
        arguments.Add("-");
        return arguments;
    }

    /// <summary>
    /// Returns the 5 <c>-c</c> flag-pairs that route Codex's <c>/v1/responses</c> calls through the
    /// Scope OpenAI metering proxy, verified on real Codex 0.141 traffic. The single source of truth
    /// for both the cron (<see cref="BuildArguments"/>) and interactive (<c>InteractiveMeterGate</c>)
    /// launch paths; changing this method propagates byte-identical flags to both paths.
    /// </summary>
    /// <param name="proxyBaseUrl">
    /// Tokenized proxy base URL of the form <c>http://127.0.0.1:5136/t/{token}</c>. The method
    /// appends <c>/v1</c> so the Codex <c>base_url</c> config entry points at the correct API root.
    /// </param>
    public static IReadOnlyList<string> BuildOpenAiMeteringArgs(string proxyBaseUrl)
        =>
        [
            "-c", "model_provider=scope",
            "-c", "model_providers.scope.name=scope",
            "-c", $"model_providers.scope.base_url={proxyBaseUrl}/v1",
            "-c", "model_providers.scope.wire_api=responses",
            "-c", $"model_providers.scope.env_key={ApiKeyEnvVar}",
        ];

    private static Dictionary<string, string> BuildChildEnvironment(HarnessLaunchRequest request)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(request.DispatchId))
            environment[DispatchEnvVar] = request.DispatchId;

        if (!string.IsNullOrWhiteSpace(request.SessionKey))
            environment[SessionEnvVar] = request.SessionKey;

        // OpenAI/Codex metering routing is done via -c model_providers flags injected by BuildArguments,
        // api-key-gated (ProxyBaseUrl AND ApiKey must both be set). ProxyBaseUrl is NOT mapped to an
        // env var for Codex: Codex uses a custom model_providers entry, not an OpenAI base-URL env var.
        // Without both values the run is un-metered (direct path); the ChatGPT-subscription / no-key
        // case stays exactly on the direct un-metered path, mirroring InteractiveMeterGate's carve-out.
        if (request.ProxyEnvironment is not null)
        {
            // Proxy wiring is non-destructive: pass provider vars through to the child verbatim.
            foreach (var (key, value) in request.ProxyEnvironment)
                environment[key] = value;
        }

        // CODEX_API_KEY is applied to the child process only and is never persisted or logged.
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            environment[ApiKeyEnvVar] = request.ApiKey;

        return environment;
    }

    private static string MapSandbox(HarnessSandbox sandbox) => sandbox switch
    {
        HarnessSandbox.ReadOnly => "read-only",
        HarnessSandbox.WorkspaceWrite => "workspace-write",
        HarnessSandbox.FullAccess => "danger-full-access",
        _ => "workspace-write",
    };

    private static string BuildFailureReason(int exitCode, string? errorDetail)
        => errorDetail is null
            ? $"Codex exited with code {exitCode}."
            : $"Codex exited with code {exitCode}: {errorDetail}";

    private static bool IsBinaryMissing(Exception ex)
        => ex is System.ComponentModel.Win32Exception or FileNotFoundException;
}

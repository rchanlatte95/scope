namespace Scope.Cli.Proxy;

using Scope.Core.Telemetry;
using Scope.Core.Utilities;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;

/// <summary>
/// CLI-side launcher for the wire-metering proxy (<c>scope proxy run/status/stop</c>).
///
/// Lifecycle contract (fail-safe, see internal design notes):
/// the launcher owns the <c>ANTHROPIC_BASE_URL</c> env var. It is injected into the
/// CHILD process environment only, never the launcher's own, and only after the proxy
/// reports healthy on <c>/healthz</c>. The env dies with the child: when the supervised
/// command exits, the proxy this launcher started is stopped. Nothing persistent is
/// installed (no certs, no system proxy, no services).
///
/// Start-or-attach: a running proxy advertised by a fresh state file is reused (its
/// per-start token and port are read from the state file). Otherwise a new proxy child
/// is spawned with a freshly generated token and supervised with bounded restarts.
/// </summary>
public static class ProxyLauncher
{
    private const int DefaultPort = 5135;
    private const int MaxRestarts = 5;
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A handle to an ensured-healthy proxy (start-or-attach), carrying the tokenized base URL the
    /// child must point <c>ANTHROPIC_BASE_URL</c> at and a stop action whose lifecycle is correct by
    /// construction: <see cref="Stop"/> is a NO-OP when this lease ATTACHED to an already-running
    /// advertised proxy (a shared :5135 or a cron proxy is never torn down), and tears down ONLY a
    /// proxy this lease itself spawned. Used by the interactive launch gate so an interactive session
    /// that spawned its own proxy stops it on exit, while a session that reused a shared proxy leaves
    /// it running.
    /// </summary>
    public sealed class ProxyLease(string baseUrl, bool spawned, Action stop)
    {
        /// <summary>Tokenized proxy base URL (<c>http://127.0.0.1:{port}/t/{token}</c>) for the child.</summary>
        public string BaseUrl { get; } = baseUrl;

        /// <summary>True when this lease spawned a fresh proxy; false when it attached to an existing one.</summary>
        public bool Spawned { get; } = spawned;

        /// <summary>Stops the proxy only if this lease spawned it; a no-op when attached to a shared proxy.</summary>
        public void Stop() => stop();
    }

    /// <summary>
    /// Start-or-attach to a healthy wire-metering proxy and return a <see cref="ProxyLease"/>. Attaches
    /// to the proxy advertised by a fresh <c>.scope/proxy.json</c> when it is reachable on
    /// <c>/healthz</c>; otherwise spawns a fresh proxy on <paramref name="port"/> with a per-start token
    /// and waits for it to report healthy. The returned lease's stop action is a no-op on the attach
    /// path (never tears down a proxy this call did not start), so callers may always invoke
    /// <see cref="ProxyLease.Stop"/> on exit without risking a shared service. This is the reusable seam
    /// shared by <c>scope proxy run</c> and the interactive launch gate.
    /// </summary>
    public static async Task<ProxyLease> EnsureProxyAsync(int? port, string? telemetryDbOverride, CancellationToken ct)
    {
        var telemetryDb = telemetryDbOverride ?? ResolveTelemetryDbPath();
        var stateFile = StateFilePath(telemetryDb);

        var (handle, _) = await AttachOrStartAsync(port ?? DefaultPort, telemetryDb, stateFile, ct).ConfigureAwait(false);

        // StopIfOwned() is already a no-op when Process is null (the attach path), so the lease's stop
        // action is safe to invoke unconditionally: it tears down only a proxy this call spawned.
        return new ProxyLease(handle.State.BaseUrl, spawned: handle.Process is not null, handle.StopIfOwned);
    }

    /// <summary>
    /// Attaches to a fresh advertised proxy when reachable on <c>/healthz</c>, else spawns one. Returns
    /// the handle plus whether it was attached (vs spawned), without emitting any console output, so it
    /// is reusable by both the foregrounded/supervised <c>scope proxy run</c> path and the quiet
    /// interactive launch gate. The callers own their own logging.
    /// </summary>
    private static async Task<(ProxyHandle Handle, bool Attached)> AttachOrStartAsync(
        int port, string telemetryDb, string stateFile, CancellationToken ct)
    {
        var attached = await TryAttachAsync(stateFile, ct).ConfigureAwait(false);
        if (attached is not null)
            return (attached, true);

        var started = await StartProxyAsync(port, telemetryDb, stateFile, ct).ConfigureAwait(false);
        return (started, false);
    }

    /// <summary>
    /// <c>scope proxy run [--port N] [-- cmd args...]</c>: ensures a healthy proxy
    /// (start-or-attach), then either runs the supervised child command with
    /// <c>ANTHROPIC_BASE_URL</c> injected, or blocks foregrounded when no command is given.
    /// </summary>
    public static async Task<int> RunAsync(int? port, string[] childCommand, string? telemetryDbOverride, CancellationToken ct)
    {
        var telemetryDb = telemetryDbOverride ?? ResolveTelemetryDbPath();
        var stateFile = StateFilePath(telemetryDb);

        // 1. Attach to an already-healthy proxy if one is advertised, else start a fresh one.
        var (handle, attached) = await AttachOrStartAsync(port ?? DefaultPort, telemetryDb, stateFile, ct).ConfigureAwait(false);
        if (attached)
            await Console.Error.WriteLineAsync($"scope proxy: attached to existing proxy pid {handle.State.Pid} on 127.0.0.1:{handle.State.Port}").ConfigureAwait(false);
        else
            await Console.Error.WriteLineAsync($"scope proxy: started proxy pid {handle.Process!.Id} on 127.0.0.1:{handle.State.Port}").ConfigureAwait(false);

        var baseUrl = handle.State.BaseUrl;

        // No child command: foreground the proxy. Print the base URL for manual use,
        // then block until the proxy exits or the user cancels.
        if (childCommand.Length == 0)
        {
            await Console.Out.WriteLineAsync(baseUrl).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("scope proxy: running. Set ANTHROPIC_BASE_URL to the URL above (token included). Ctrl+C to stop.").ConfigureAwait(false);
            try
            {
                if (handle.Process is { } proc)
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                else
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* user asked to stop */ }
            finally
            {
                handle.StopIfOwned();
            }
            return 0;
        }

        // Child command: inject ANTHROPIC_BASE_URL into the child env ONLY and supervise.
        try
        {
            return await SuperviseChildAsync(handle, childCommand, baseUrl, port ?? DefaultPort, telemetryDb, stateFile, ct).ConfigureAwait(false);
        }
        finally
        {
            handle.StopIfOwned();
        }
    }

    /// <summary>
    /// Runs the child command with <c>ANTHROPIC_BASE_URL</c> set in its environment.
    /// While the child runs, the proxy is supervised: if a launcher-owned proxy dies,
    /// it is restarted (bounded) and the new base URL re-applied for subsequent calls.
    /// The child's already-set env var keeps the old port if the proxy restarts on a
    /// new port, so restarts reuse the original port to keep the child's URL valid.
    /// </summary>
    private static async Task<int> SuperviseChildAsync(
        ProxyHandle handle, string[] childCommand, string baseUrl,
        int port, string telemetryDb, string stateFile, CancellationToken ct)
    {
        var child = StartChild(childCommand, baseUrl);
        await Console.Error.WriteLineAsync($"scope proxy: launched '{childCommand[0]}' (pid {child.Id}) with ANTHROPIC_BASE_URL -> proxy.").ConfigureAwait(false);

        var restarts = 0;
        while (true)
        {
            var childExit = child.WaitForExitAsync(ct);

            // Only supervise restarts for a proxy we own (an attached external proxy is
            // not ours to restart).
            if (handle.Process is { } proc)
            {
                var proxyExit = proc.WaitForExitAsync(CancellationToken.None);
                var completed = await Task.WhenAny(childExit, proxyExit).ConfigureAwait(false);
                if (completed == childExit)
                {
                    await childExit.ConfigureAwait(false);
                    return child.ExitCode;
                }

                // Proxy died while the child is still running. Restart bounded.
                if (restarts >= MaxRestarts)
                {
                    await Console.Error.WriteLineAsync($"scope proxy: proxy exited and restart budget ({MaxRestarts}) exhausted; killing child.").ConfigureAwait(false);
                    TryKill(child);
                    return 1;
                }
                restarts++;
                await Console.Error.WriteLineAsync($"scope proxy: proxy exited; restarting (attempt {restarts}/{MaxRestarts}).").ConfigureAwait(false);
                // Reuse the original port so the child's ANTHROPIC_BASE_URL stays valid only
                // if the same token is reused. The token is regenerated per start, so we must
                // re-read state and re-point: the child already captured the old URL, so a
                // restart with a new token would break it. We therefore restart with the SAME
                // token by passing it through the environment for the proxy child.
                handle = await RestartProxyAsync(handle, port, telemetryDb, stateFile, ct).ConfigureAwait(false);
            }
            else
            {
                await childExit.ConfigureAwait(false);
                return child.ExitCode;
            }
        }
    }

    /// <summary>
    /// <c>scope proxy status</c>: reads the state file and probes <c>/healthz</c>.
    /// Prints a one-line human summary to stdout. Exit 0 when healthy, 3 (FileNotFound)
    /// when no proxy is advertised, 19 (RemoteFailure) when advertised but unreachable.
    /// </summary>
    public static async Task<int> StatusAsync(string? telemetryDbOverride, CancellationToken ct)
    {
        var telemetryDb = telemetryDbOverride ?? ResolveTelemetryDbPath();
        var stateFile = StateFilePath(telemetryDb);

        var state = ProxyState.TryLoad(stateFile);
        if (state is null)
        {
            await Console.Out.WriteLineAsync("scope proxy: not running (no state file).").ConfigureAwait(false);
            return 2;
        }

        var health = await ProbeHealthAsync(state.Port, ct).ConfigureAwait(false);
        if (health is null)
        {
            await Console.Out.WriteLineAsync($"scope proxy: state file present (pid {state.Pid}, port {state.Port}) but /healthz unreachable; likely stale.").ConfigureAwait(false);
            return 19;
        }

        await Console.Out.WriteLineAsync(
            $"scope proxy: healthy. pid={health.Pid} port={health.Port} started={health.StartedAt} " +
            $"recorded={health.RecordedCount} drift={health.DriftCount}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"  ANTHROPIC_BASE_URL={state.BaseUrl}").ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// <c>scope proxy stop</c>: reads the state file and terminates the advertised
    /// proxy process. Best-effort; removing a stale state file always succeeds.
    /// </summary>
    public static async Task<int> StopAsync(string? telemetryDbOverride, CancellationToken ct)
    {
        var telemetryDb = telemetryDbOverride ?? ResolveTelemetryDbPath();
        var stateFile = StateFilePath(telemetryDb);

        var state = ProxyState.TryLoad(stateFile);
        if (state is null)
        {
            await Console.Out.WriteLineAsync("scope proxy: nothing to stop (no state file)").ConfigureAwait(false);
            return 0;
        }

        try
        {
            var proc = Process.GetProcessById(state.Pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            await Console.Out.WriteLineAsync($"scope proxy: stopped pid {state.Pid}.").ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await Console.Out.WriteLineAsync($"scope proxy: pid {state.Pid} not running; clearing stale state.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"scope proxy: failed to stop pid {state.Pid}: {ex.Message}").ConfigureAwait(false);
            return 19;
        }
        finally
        {
            TryDelete(stateFile);
        }
        return 0;
    }

    // --- start / attach ---

    private sealed class ProxyHandle(ProxyState state, Process? process, string token, string stateFile)
    {
        public ProxyState State { get; set; } = state;
        public Process? Process { get; set; } = process;
        public string Token { get; } = token;
        public string StateFile { get; } = stateFile;

        public void StopIfOwned()
        {
            if (Process is null) return; // attached to an external proxy; leave it running
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(5000);
                }
            }
            catch { /* best effort */ }
            // Killed processes do not run their own state-file cleanup, so the
            // launcher removes the file it caused to be written.
            TryDelete(StateFile);
        }
    }

    private static async Task<ProxyHandle?> TryAttachAsync(string stateFile, CancellationToken ct)
    {
        var state = ProxyState.TryLoad(stateFile);
        if (state is null) return null;
        var health = await ProbeHealthAsync(state.Port, ct).ConfigureAwait(false);
        if (health is null || health.Pid != state.Pid) return null;
        return new ProxyHandle(state, process: null, state.Token, stateFile);
    }

    private static async Task<ProxyHandle> StartProxyAsync(int port, string telemetryDb, string stateFile, CancellationToken ct)
    {
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var proc = SpawnProxy(port, telemetryDb, stateFile, token);
        var state = await WaitForHealthyAsync(proc, port, token, telemetryDb, ct).ConfigureAwait(false);
        return new ProxyHandle(state, proc, token, stateFile);
    }

    private static async Task<ProxyHandle> RestartProxyAsync(ProxyHandle prev, int port, string telemetryDb, string stateFile, CancellationToken ct)
    {
        // Reuse the same token so the child's already-captured ANTHROPIC_BASE_URL stays valid.
        var proc = SpawnProxy(port, telemetryDb, stateFile, prev.Token);
        var state = await WaitForHealthyAsync(proc, port, prev.Token, telemetryDb, ct).ConfigureAwait(false);
        return new ProxyHandle(state, proc, prev.Token, stateFile);
    }

    private static Process SpawnProxy(int port, string telemetryDb, string stateFile, string token)
    {
        var (fileName, prefixArgs) = ResolveProxyInvocation();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--telemetry-db");
        psi.ArgumentList.Add(telemetryDb);
        psi.ArgumentList.Add("--state-file");
        psi.ArgumentList.Add(stateFile);

        // Detached-quiet: logs go to the rolling file only, never the inherited console.
        psi.Environment["SCOPE_PROXY_QUIET"] = "1";
        // Per-start token passed via env so supervised restarts can reuse it.
        psi.Environment["SCOPE_PROXY_TOKEN"] = token;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the scope-proxy process.");
        return proc;
    }

    /// <summary>
    /// Resolves how to invoke the proxy executable. Resolution order (no dev-tree probing):
    /// <list type="number">
    ///   <item><c>SCOPE_PROXY_EXE</c> env override: an executable run directly, or a
    ///   <c>Scope.Proxy.dll</c> run via <c>dotnet</c>.</item>
    ///   <item>Packaged framework-dependent <c>proxy/Scope.Proxy.dll</c> next to the CLI
    ///   (the global-tool install layout; ships in the CLI nupkg, run via <c>dotnet</c>).</item>
    ///   <item><c>Scope.Proxy[.exe]</c> next to the CLI (the self-contained
    ///   <c>scripts/publish.ps1</c> layout; run directly).</item>
    /// </list>
    /// Returns the file to run plus any prefix args (the proxy dll path when invoked
    /// through <c>dotnet</c>). No fallback to a Scope.Proxy/bin build tree: an unresolved
    /// proxy is a clear, actionable error, not a silent dev-tree dependency.
    /// </summary>
    internal static (string FileName, string[] PrefixArgs) ResolveProxyInvocation()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1. Explicit override. A .dll is launched via the dotnet host; anything else
        //    (a self-contained exe) is launched directly.
        var overridePath = Environment.GetEnvironmentVariable("SCOPE_PROXY_EXE");
        if (overridePath.HasContent && File.Exists(overridePath))
        {
            return overridePath!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? (DotnetHostPath(), [overridePath!])
                : (overridePath!, []);
        }

        // 2. Packaged framework-dependent proxy shipped inside the CLI tool nupkg
        //    (tools/<tfm>/any/proxy/Scope.Proxy.dll -> extracted next to the CLI).
        var packagedDll = Path.Combine(baseDir, "proxy", "Scope.Proxy.dll");
        if (File.Exists(packagedDll))
            return (DotnetHostPath(), [packagedDll]);

        // 3. Self-contained proxy executable next to the CLI (scripts/publish.ps1).
        var exeName = OperatingSystem.IsWindows() ? "Scope.Proxy.exe" : "Scope.Proxy";
        var siblingExe = Path.Combine(baseDir, exeName);
        if (File.Exists(siblingExe))
            return (siblingExe, []);

        throw new InvalidOperationException(
            "Could not locate the scope-proxy executable. Expected it next to the CLI at " +
            $"'{packagedDll}' (global-tool install) or '{siblingExe}' (self-contained publish). " +
            "Reinstall with scripts/install.ps1 (or scripts/publish.ps1) so the proxy ships " +
            "with the tool, or set SCOPE_PROXY_EXE to a Scope.Proxy executable or Scope.Proxy.dll.");
    }

    /// <summary>
    /// Resolves the dotnet host used to launch a framework-dependent proxy dll. Prefers
    /// the host that is running this CLI (process path), then DOTNET_ROOT, then the
    /// "dotnet" command resolved off PATH.
    /// </summary>
    private static string DotnetHostPath()
    {
        var hostName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // The CLI itself runs under the dotnet host when started as `dotnet scope.dll`,
        // but a tool shim launches the apphost, so MainModule is not reliably dotnet.
        // Prefer DOTNET_ROOT, then fall back to the bare command (resolved via PATH).
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot.HasContent)
        {
            var candidate = Path.Combine(dotnetRoot!, hostName);
            if (File.Exists(candidate)) return candidate;
        }

        return hostName;
    }

    private static Process StartChild(string[] command, string baseUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
        };
        for (var i = 1; i < command.Length; i++) psi.ArgumentList.Add(command[i]);

        // The env var lives on the CHILD only, set after the proxy is healthy.
        psi.Environment["ANTHROPIC_BASE_URL"] = baseUrl;

        return Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start child command '{command[0]}'.");
    }

    // --- health probing ---

    private static async Task<ProxyState> WaitForHealthyAsync(
        Process proc, int port, string token, string telemetryDb, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HealthTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
                throw new InvalidOperationException($"scope-proxy exited during startup (code {proc.ExitCode}).");

            // Prefer the state file the proxy writes (authoritative port after bind),
            // then confirm liveness via /healthz.
            var state = ProxyState.TryLoad(StateFilePath(telemetryDb));
            if (state is not null && state.Pid == proc.Id)
            {
                var health = await ProbeHealthAsync(state.Port, ct).ConfigureAwait(false);
                if (health is not null) return state;
            }

            try { await Task.Delay(150, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        TryKill(proc);
        throw new TimeoutException($"scope-proxy did not become healthy within {HealthTimeout.TotalSeconds:0}s.");
    }

    private static async Task<HealthzReport?> ProbeHealthAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{port}/healthz", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, ProxyCliJsonContext.Default.HealthzReport);
        }
        catch
        {
            return null;
        }
    }

    // --- paths / helpers ---

    /// <summary>
    /// Resolves the proxy state file path (<c>proxy.json</c>) next to the given telemetry.db. Public so
    /// the interactive launch gate and tests can locate the advertised proxy's state file.
    /// </summary>
    public static string StateFilePath(string telemetryDbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(telemetryDbPath)) ?? ".";
        return Path.Combine(dir, "proxy.json");
    }

    /// <summary>
    /// Resolves the telemetry.db path the same way the proxy does: SCOPE_DB_PATH's
    /// directory when set, else walk up from CWD for an existing .scope/ directory,
    /// else .scope/ in CWD.
    /// </summary>
    internal static string ResolveTelemetryDbPath()
    {
        var dbPath = Environment.GetEnvironmentVariable("SCOPE_DB_PATH");
        if (dbPath.HasContent)
        {
            var scopeDir = Path.GetDirectoryName(Path.GetFullPath(dbPath!));
            if (scopeDir is not null) return Path.Combine(scopeDir, "telemetry.db");
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".scope");
            if (Directory.Exists(candidate)) return Path.Combine(candidate, "telemetry.db");
            dir = dir.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".scope", "telemetry.db");
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

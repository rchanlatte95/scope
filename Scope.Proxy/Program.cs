using Scope.Core.Telemetry;
using Scope.Core.Utilities;
using Scope.Proxy;
using Serilog;
using System.Globalization;
using System.Security.Cryptography;

// scope-proxy: localhost passthrough metering proxy for the Anthropic and OpenAI APIs.
// Meter, never gateway. See internal design notes
//
// Usage:
//   scope-proxy [--port 5135] [--bind 127.0.0.1] [--unsafe-bind]
//               [--token <t>] [--telemetry-db <path>] [--state-file <path>]
//               [--upstream <url>] [--provider anthropic|openai]
//               [--capture-file <path>]
//
// --provider anthropic  (default) meters /v1/messages and writes wire_calls rows.
// --provider openai     routes /v1/responses; Phase A capture stub only (no wire_calls
//                       rows yet). Use --capture-file or SCOPE_PROXY_CAPTURE_FILE to tee
//                       raw response bodies to disk for Phase B fixture recording.
//
// The per-start token also comes from SCOPE_PROXY_TOKEN (set by the launcher so
// supervised restarts keep the child's ANTHROPIC_BASE_URL valid); when absent a
// fresh random token is generated. The token is written to the state file and
// is never logged.

// The launcher starts the proxy detached and sets SCOPE_PROXY_QUIET=1 so log
// output goes to the rolling file only, never the inherited console.
if (string.Equals(Environment.GetEnvironmentVariable("SCOPE_PROXY_QUIET"), "1", StringComparison.Ordinal))
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.File(
            ScopeLogConfiguration.GetLogFilePath("Proxy"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate: ScopeLogConfiguration.OutputTemplate)
        .CreateLogger();
}
else
{
    ScopeLogConfiguration.ConfigureLogger("Proxy");
}

try
{
    var port = 5135;
    var bind = "127.0.0.1";
    var unsafeBind = false;
    string? token = null;
    string? telemetryDb = null;
    string? stateFile = null;
    var upstream = "https://api.anthropic.com";
    var provider = "anthropic";
    string? captureFile = null;
    // Track whether --upstream was explicitly provided so we can default it per-provider.
    var upstreamExplicit = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--port": port = int.Parse(Next(args, ref i, "--port"), CultureInfo.InvariantCulture); break;
            case "--bind": bind = Next(args, ref i, "--bind"); break;
            case "--unsafe-bind": unsafeBind = true; break;
            case "--token": token = Next(args, ref i, "--token"); break;
            case "--telemetry-db": telemetryDb = Next(args, ref i, "--telemetry-db"); break;
            case "--state-file": stateFile = Next(args, ref i, "--state-file"); break;
            case "--upstream": upstream = Next(args, ref i, "--upstream"); upstreamExplicit = true; break;
            case "--provider": provider = Next(args, ref i, "--provider"); break;
            case "--capture-file": captureFile = Next(args, ref i, "--capture-file"); break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 13;
        }
    }

    // When --provider openai is specified and --upstream was not explicitly overridden,
    // default to the OpenAI base URL instead of Anthropic.
    if (!upstreamExplicit && provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        upstream = "https://api.openai.com";

    // Capture file: CLI flag takes precedence, env var as fallback.
    captureFile ??= Environment.GetEnvironmentVariable("SCOPE_PROXY_CAPTURE_FILE");

    if (!ProxyServer.ValidateBind(bind, unsafeBind, out var bindError))
    {
        Console.Error.WriteLine(bindError);
        return 13;
    }

    token ??= Environment.GetEnvironmentVariable("SCOPE_PROXY_TOKEN");
    token ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    telemetryDb ??= ResolveTelemetryDbPath();
    var telemetryDir = Path.GetDirectoryName(Path.GetFullPath(telemetryDb));
    if (telemetryDir != null) Directory.CreateDirectory(telemetryDir);
    stateFile ??= Path.Combine(telemetryDir ?? ".", "proxy.json");

    var options = new ProxyServerOptions
    {
        Token = token,
        TelemetryDbPath = Path.GetFullPath(telemetryDb),
        BindAddress = bind,
        Port = port,
        UpstreamBaseUrl = upstream,
        Provider = provider,
        CaptureFilePath = captureFile,
    };

    var server = await ProxyServer.StartAsync(options).ConfigureAwait(false);
    await using var serverScope = server.ConfigureAwait(false);

    var state = new ProxyState(
        Environment.ProcessId, server.BoundPort, token,
        server.StartedAt, options.TelemetryDbPath);
    state.Save(stateFile);

    // ST-04: refresh public model pricing roughly hourly from inside this long-running
    // process. Detached, off the request path, fully tolerant (network/parse failure keeps
    // last-known and never affects forwarding). The cache lands next to telemetry.db (.scope).
    using var pricingRefresher = new PricingRefresher(telemetryDir ?? Directory.GetCurrentDirectory());
    pricingRefresher.Start();

    Log.Information("scope-proxy listening on 127.0.0.1:{Port} -> {Upstream} (provider={Provider}); telemetry: {Db}; state: {State}",
        server.BoundPort, upstream, provider, options.TelemetryDbPath, stateFile);

    try
    {
        await server.WaitForShutdownAsync().ConfigureAwait(false);
    }
    finally
    {
        TryDeleteStateFile(stateFile, Environment.ProcessId);
    }

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "scope-proxy terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static string Next(string[] args, ref int i, string name)
{
    if (i + 1 >= args.Length)
        throw new ArgumentException($"Missing value for {name}.", name);
    return args[++i];
}

// Telemetry db resolution: SCOPE_DB_PATH's directory when set, else walk up
// from CWD for an existing .scope/ directory, else create .scope/ in CWD.
static string ResolveTelemetryDbPath()
{
    var dbPath = Environment.GetEnvironmentVariable("SCOPE_DB_PATH");
    if (dbPath.HasContent)
    {
        var scopeDir = Path.GetDirectoryName(Path.GetFullPath(dbPath!));
        if (scopeDir != null) return Path.Combine(scopeDir, "telemetry.db");
    }

    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, ".scope");
        if (Directory.Exists(candidate)) return Path.Combine(candidate, "telemetry.db");
        dir = dir.Parent;
    }

    return Path.Combine(Directory.GetCurrentDirectory(), ".scope", "telemetry.db");
}

static void TryDeleteStateFile(string path, int ownPid)
{
    try
    {
        // Only remove the file if it still belongs to this process.
        if (ProxyState.TryLoad(path) is { } existing && existing.Pid == ownPid)
            File.Delete(path);
    }
    catch
    {
        // Best effort; stale state files are detected by the launcher via pid + healthz.
    }
}

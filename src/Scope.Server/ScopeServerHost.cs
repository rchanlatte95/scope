namespace Scope.Server;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Scope.Core.App;
using Scope.Core.CostEstimation;
using Scope.Core.Cron;
using Scope.Core.Cron.Launch;
using Scope.Core.Cron.Run;
using Scope.Core.Entitlement;
using Scope.Core.Telemetry;
using Scope.Core.Telemetry.Trace;
using Scope.Core.Utilities;
using Serilog;

/// <summary>
/// Invocable entry point for the Scope.Server ASP.NET host.
/// </summary>
/// <remarks>
/// Exposes the server as a callable static method so Scope.Cli's <c>scope serve</c> command
/// can boot the dashboard in-proc without duplicating host setup. The traditional standalone
/// entry point (src/Scope.Server/Program.cs) delegates here unchanged.
/// XP7a: Scope.Cli referencing Scope.Server pulls ASP.NET into the CLI tool — acceptable per
/// the task brief; the "Core free of ASP.NET" rule targets Scope.Core, not Scope.Cli.
/// </remarks>
public static class ScopeServerHost
{
    /// <summary>
    /// Builds and runs the Scope.Server ASP.NET host.
    /// </summary>
    /// <param name="args">
    /// Standard WebApplication args, e.g. <c>["--urls", "http://127.0.0.1:5130"]</c>.
    /// </param>
    /// <param name="cancellationToken">
    /// Triggers graceful host shutdown (e.g. Ctrl+C from the CLI caller).
    /// </param>
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Configure the Serilog static logger from the resolved database path so each server process
        // writes to a project-scoped log directory (%APPDATA%/.scope/{projectName}/logs/). The db is
        // resolved preferring the "db" configuration setting (set by WebApplicationFactory tests via
        // UseSetting), then the --db CLI arg, then CWD discovery. This MUST mirror the per-request
        // resolution in ResolveDbOrProblem (app.Configuration["db"]): when it does not, every test
        // factory falls back to the same "default" project name and races on a single shared rolling
        // log file (Serilog spills to scope-{date}_NNN.log when the handle is locked), and an unlucky
        // startup-time file-sink collision surfaces as an HTTP 500 on the factory's first request
        // (T-2026-06-19-06).
        var dbPath = ScopeApp.FindDbPath(explicitPath: builder.Configuration["db"] ?? FindDbArg(args));
        var projectName = ScopeLogConfiguration.ExtractProjectNameFromDbPath(dbPath);
        ScopeLogConfiguration.ConfigureLogger(projectName);
        try
        {

            var urls = builder.Configuration["urls"] ?? "http://localhost:5130";
            builder.WebHost.UseUrls(urls);

            // Register the server-owned background-task registry for cron runs. Lifetime is singleton
            // (one per server process); it is constructed manually because there is no DI container.
            var cronTasks = new CronBackgroundTaskService();

            // Register the cron launcher registry as a DI singleton so WebApplicationFactory-based tests can
            // override it via ConfigureTestServices without touching Program.cs logic. At runtime the
            // factory reads the "cron-harness" seam setting from configuration. Tests inject a replacement
            // CronLauncherRegistry singleton via ConfigureTestServices; that registration takes precedence
            // because it is added after this one (last-registration-wins for singletons in the test host).
            builder.Services.AddSingleton<CronLauncherRegistry>(
                sp => ResolveCronRegistryFromConfig(builder.Configuration) ?? CronLauncherRegistry.CreateDefault());

            var app = builder.Build();

            // Drain in-flight cron background tasks on graceful shutdown (before the process exits).
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                const int DrainSeconds = 30;
                Log.Information("Scope.Server stopping: draining cron background tasks (deadline {S}s).", DrainSeconds);
                cronTasks.DrainAsync(TimeSpan.FromSeconds(DrainSeconds)).GetAwaiter().GetResult();
            });

            // SPA serving (trace-ux-v2 ST-13): the trace dashboard is built by
            // scripts/build-frontend.ps1 into wwwroot/ and embedded into this assembly at
            // build time (see Scope.Server.csproj). ManifestEmbeddedFileProvider keeps
            // single-file publish a true single binary (no loose wwwroot folder next to the
            // exe). When no assets were embedded (dotnet-only build without node), the
            // server runs in API-only mode and "/" returns a plain-text notice.
            // SPA provider selection is driven by the "spa" configuration value (see
            // ResolveSpaFileProvider): "off" forces API-only mode (deterministic test seam), a
            // directory path serves a loose SPA build (integration tests + dev convenience), and
            // anything else uses the embedded manifest baked into this assembly at build time.
            var spaProvider = ResolveSpaFileProvider(app.Configuration["spa"]);
            if (spaProvider != null)
            {
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaProvider });
                app.UseStaticFiles(new StaticFileOptions { FileProvider = spaProvider });
            }
            else
            {
                app.MapGet("/", () => Results.Text(
                    "Scope.Server is running in API-only mode (no SPA assets were embedded at build time). " +
                    "Run scripts/build-frontend.ps1 and rebuild Scope.Server to serve the trace UI at /. " +
                    "API endpoints remain available under /api/.",
                    "text/plain"));
            }

            app.MapGet("/api/status", async (CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "status", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.StatusAsync(db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            // Unified Command summary (T-2026-06-13-36). Content-free, offline-capable. Returns workspace
            // identity / entitlement state / uplink / compact 7d budget / security / operating + recent
            // runs and sessions. Always 200: honest "unavailable" on any absent data source, never an error.
            app.MapGet("/api/command/status", async (CancellationToken ct) =>
            {
                var db = ResolveDbOrProblem();
                // When scope.db is not found we still return a valid response (all sections degrade).
                var dbPath2 = db.Problem == null ? db.Path : null;
                var result = await ScopeApp.CommandStatusAsync(
                    dbPath2,
                    searchAllParents: false,
                    activeRunCount: cronTasks.ActiveCount,
                    cancellationToken: ct).ConfigureAwait(false);
                return Results.Text(result.Stdout ?? "{}", "application/json");
            });

            // Deep cost-ledger explorer (T-05a). Content-free. Always 200; honest "unavailable"
            // when no telemetry data exists. window_days defaults to 30 and is clamped to [1, 3650].
            // Custom range (T-2026-06-16-14 / ST-02): when BOTH from_utc and to_utc are supplied as parseable
            // ISO-8601 UTC strings, an explicit CostWindow.Custom is used (window_days ignored); an unparseable
            // or invalid pair degrades to the window_days rolling window. Backend is always UTC.
            app.MapGet("/api/command/budget", async (int? window_days, string? from_utc, string? to_utc, CancellationToken ct) =>
            {
                var db = ResolveDbOrProblem();
                // When scope.db is not found we still return a valid response (all sections degrade).
                var dbPath2 = db.Problem == null ? db.Path : null;

                // Parse the optional custom-range bounds. An unparseable bound is treated as absent, which makes
                // CommandBudgetAsync fall back to the window_days path (no 500). Parse with explicit UTC normalization.
                var fromUtc = ParseUtc(from_utc);
                var toUtc = ParseUtc(to_utc);

                var result = await ScopeApp.CommandBudgetAsync(
                    dbPath2,
                    windowDays: window_days ?? 30,
                    fromUtc: fromUtc,
                    toUtc: toUtc,
                    cancellationToken: ct).ConfigureAwait(false);
                return Results.Text(result.Stdout ?? "{}", "application/json");

                static DateTimeOffset? ParseUtc(string? value)
                    => DateTimeOffset.TryParse(
                            value,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed)
                        ? parsed
                        : null;
            });

            // Base-launch cost rollup (T-2026-06-19-03 Wave 2 / ST-06). Content-free. Always 200; honest empty
            // rollup when no telemetry data exists. Per-CLI-launch cost == SUM of its `/clear`-split generations,
            // derived ADDITIVELY from the SAME per-session rollups the Cost Ledger sums (SUM(by_base) == headline
            // by construction). Window/clamping/custom-range semantics match /api/command/budget exactly.
            app.MapGet("/api/command/base-launches", async (int? window_days, string? from_utc, string? to_utc, CancellationToken ct) =>
            {
                var db = ResolveDbOrProblem();
                var dbPath2 = db.Problem == null ? db.Path : null;

                var fromUtc = ParseUtc(from_utc);
                var toUtc = ParseUtc(to_utc);

                var result = await ScopeApp.CommandBaseLaunchCostsAsync(
                    dbPath2,
                    windowDays: window_days ?? 30,
                    fromUtc: fromUtc,
                    toUtc: toUtc,
                    cancellationToken: ct).ConfigureAwait(false);
                return Results.Text(result.Stdout ?? "{}", "application/json");

                static DateTimeOffset? ParseUtc(string? value)
                    => DateTimeOffset.TryParse(
                            value,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed)
                        ? parsed
                        : null;
            });

            app.MapGet("/api/symbols/search", async (string? q, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "query", dispatchId: ObservabilityContext.ResolveDispatchId());
                if (q.IsNullOrWhitespace)
                    return Results.Problem("Query parameter 'q' is required.", statusCode: 400, title: "Missing query parameter");

                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.QueryAsync(q, db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            app.MapGet("/api/impact", async (string? symbol, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "impact", dispatchId: ObservabilityContext.ResolveDispatchId());
                if (symbol.IsNullOrWhitespace)
                    return Results.Problem("Query parameter 'symbol' is required.", statusCode: 400, title: "Missing query parameter");

                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.ImpactAsync(
                    symbol,
                    changedFilesPath: null,
                    baseBranch: null,
                    format: "json",
                    db.Path!,
                    searchAllParents: false,
                    jsonErrors: true,
                    ct,
                    ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            app.MapGet("/api/pack", async (string? symbol, int? budget, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "pack", dispatchId: ObservabilityContext.ResolveDispatchId());
                if (symbol.IsNullOrWhitespace)
                    return Results.Problem("Query parameter 'symbol' is required.", statusCode: 400, title: "Missing query parameter");

                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.PackAsync(
                    symbol,
                    budget ?? 2048,
                    format: "json",
                    allowOverBudget: true,
                    db.Path!,
                    searchAllParents: false,
                    jsonErrors: true,
                    ct,
                    ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            app.MapGet("/api/complexity", async (string? symbol, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "complexity", dispatchId: ObservabilityContext.ResolveDispatchId());
                if (symbol.IsNullOrWhitespace)
                    return Results.Problem("Query parameter 'symbol' is required.", statusCode: 400, title: "Missing query parameter");

                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.ComplexityAsync(
                    symbol,
                    budget: 2048,
                    format: "json",
                    db.Path!,
                    searchAllParents: false,
                    jsonErrors: true,
                    ct,
                    ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            // Cron agent CRUD + run history (CR-M1D). Localhost-only. The adapter maps HTTP only:
            // all validation, normalization, label uniqueness, and the free-tier enabled-agent limit
            // live in Scope.Core CronService. CronResult failures surface as 400 (or 404 when the
            // service reports a missing label, via ScopeExitCode.NoSymbolFound). Creation returns 201
            // to match REST resource-creation conventions; the rest of the API has no creating endpoint,
            // so there is no prior 200-on-create convention to preserve.

            app.MapGet("/api/cron/agents", async (CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-list", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronListAgentsAsync(db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            app.MapPost("/api/cron/agents", async (HttpRequest req, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-create", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                CronCreateRequestDto? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync(
                        req.Body, ScopeJsonSerializerContext.Default.CronCreateRequestDto, ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.Problem("Request body is not valid JSON.", statusCode: 400, title: "InvalidParameter");
                }

                if (body == null)
                    return Results.Problem("Request body is required.", statusCode: 400, title: "MissingParameter");

                var result = await ScopeApp.CronCreateAgentAsync(
                    body.ToCreateRequest(), db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);

                return result.ExitCode == (int)ScopeExitCode.Success
                    ? Results.Text(result.Stdout ?? "", "application/json", statusCode: 201)
                    : ToHttpResult(result);
            });

            app.MapGet("/api/cron/agents/{label}", async (string label, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-get", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronGetAgentAsync(label, db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            // Update (T-2026-06-17-05). PUT /api/cron/agents/{label} is a FULL REPLACE of the editable fields.
            // The label in the path is the immutable identity/URL key and is never changed by the body. Reuses
            // the SAME validation path as create (CronService.ValidateEditableFields) so create/edit cannot drift.
            // 200 + updated CronAgentDto on success; 404 when the label is unknown or soft-deleted; 400 on a
            // validation rejection (bad schedule/harness, token_budget<=0, max_runtime<=0). enabled is NOT
            // editable here (it stays on enable/disable) so an edit never changes the free-tier enabled count.
            app.MapPut("/api/cron/agents/{label}", async (string label, HttpRequest req, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-update", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                CronUpdateRequestDto? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync(
                        req.Body, ScopeJsonSerializerContext.Default.CronUpdateRequestDto, ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.Problem("Request body is not valid JSON.", statusCode: 400, title: "InvalidParameter");
                }

                if (body == null)
                    return Results.Problem("Request body is required.", statusCode: 400, title: "MissingParameter");

                var result = await ScopeApp.CronUpdateAgentAsync(
                    label, body.ToUpdateRequest(), db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);

                return ToHttpResult(result);
            });

            app.MapDelete("/api/cron/agents/{label}", async (string label, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-remove", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronRemoveAgentAsync(label, db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            app.MapGet("/api/cron/agents/{label}/runs", async (string label, int? limit, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-runs", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronListRunsAsync(label, limit, db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            // Run-now (T-2026-06-12-30, async non-blocking). Starts the cron run and returns 202 immediately
            // with the initial run row (status running, skipped, or failed). The harness launch and
            // finalization continue on a SERVER-OWNED tracked background task registered with cronTasks
            // (never fire-and-forget). The client polls GET /api/cron/agents/{label}/runs/{runId} until
            // the row reaches a terminal state. 404 when the label is unknown.
            //
            // R7-backend: Concurrency guard. When the agent's block_concurrent_runs flag is true (default):
            //   - A second run-now with no/non-matching Idempotency-Key returns 409 Conflict (no new row).
            //   - A second run-now with a matching Idempotency-Key (same key that started the in-flight run)
            //     returns 202 with the existing in-flight run (idempotent replay).
            // When block_concurrent_runs is false, a second concurrent run-now inserts a 'skipped' row (202).
            app.MapPost("/api/cron/agents/{label}/run", async (string label, HttpContext httpCtx, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-run", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                // Read the registry from DI so tests can override it via ConfigureTestServices without
                // touching Program.cs logic. The singleton is always non-null: the default registration in this
                // file falls back to CronLauncherRegistry.CreateDefault() when the "cron-harness=fake"
                // seam is not active; tests replace the singleton via ConfigureTestServices.
                var registry = httpCtx.RequestServices.GetRequiredService<CronLauncherRegistry>();

                // R7-backend: read the Idempotency-Key header (null when absent or whitespace-only).
                string? idempotencyKey = null;
                if (httpCtx.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues))
                {
                    var trimmed = keyValues.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        idempotencyKey = trimmed;
                }

                var (result, completion) = await ScopeApp.CronStartRunAsync(
                    label, db.Path!, searchAllParents: false, jsonErrors: true,
                    registry: registry, idempotencyKey: idempotencyKey, ct, ctx: ctx).ConfigureAwait(false);

                // 409 Conflict: guard ON, in-flight run, no matching idempotency key.
                // Stdout carries the in-flight run JSON so the caller can deep-link to it.
                if (result.ExitCode == (int)ScopeExitCode.ConcurrentRunConflict)
                {
                    // Parse the in-flight run JSON defensively; if it fails, omit the extension.
                    Dictionary<string, object?>? extensions = null;
                    if (result.Stdout is not null)
                    {
                        try
                        {
                            var inflight = JsonSerializer.Deserialize<JsonElement>(result.Stdout);
                            extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["inflight_run"] = inflight };
                        }
                        catch { /* non-fatal: omit inflight_run extension on parse failure */ }
                    }
                    return Results.Problem(
                        detail: $"A run for '{label}' is already in progress. " +
                                "Poll the in-flight run or wait for it to complete. " +
                                "This agent blocks concurrent runs (block_concurrent_runs is set when the agent is created).",
                        statusCode: 409,
                        title: "Concurrent run in progress",
                        extensions: extensions);
                }

                if (result.ExitCode != (int)ScopeExitCode.Success)
                    return ToHttpResult(result);

                // Register the continuation Task (may be completed for skip/early-fail paths or idempotent replay).
                // Deserialize the initial run row to get the run id for tracking.
                string? runId = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result.Stdout ?? "{}");
                    runId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                }
                catch { /* non-fatal: tracking uses a fallback key */ }

                cronTasks.Track(runId ?? Guid.NewGuid().ToString("N"), completion!);

                return Results.Text(result.Stdout ?? "", "application/json", statusCode: 202);
            });

            // Poll endpoint (T-2026-06-12-30). The client polls this until the status is terminal
            // (completed, failed, or skipped). 200 + CronRunDto body; 404 when label or runId not found.
            app.MapGet("/api/cron/agents/{label}/runs/{runId}", async (string label, string runId, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-get-run", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronGetRunAsync(label, runId, db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            // Enable/disable (CR-M2). POST /api/cron/agents/{label}/enable and /disable.
            // Returns 200 with the updated CronAgentDto; 404 when the label is not found; 400 when enabling
            // would exceed the local free-tier 3-enabled cap. All cap enforcement lives in CronService.
            app.MapPost("/api/cron/agents/{label}/enable", async (string label, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-enable", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronSetEnabledAsync(label, enabled: true, db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            app.MapPost("/api/cron/agents/{label}/disable", async (string label, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-disable", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronSetEnabledAsync(label, enabled: false, db.Path!, searchAllParents: false, jsonErrors: true, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            // Launcher capabilities (CR-M1C). Reports each supported harness id with its capability status and
            // detail so the frontend can render which harnesses are runnable. Always 200; an unavailable or
            // unimplemented harness (e.g. the Claude Code seam stub) is reported honestly, not as an error.
            app.MapGet("/api/cron/capabilities", async (HttpContext httpCtx, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-capabilities", dispatchId: ObservabilityContext.ResolveDispatchId());
                var registry = httpCtx.RequestServices.GetRequiredService<CronLauncherRegistry>();
                var result = await ScopeApp.CronCapabilitiesAsync(registry: registry, ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            // scope-trace-v1 endpoints (trace-ux-v2 ST-12). Read-only, localhost, GET-only.
            // All contract assembly (envelope wrapping, tier selection, projection, paging) lives
            // in Scope.Core.Telemetry.Trace.TraceDocuments; this adapter only resolves the db, parses
            // hostile query params cleanly (no stack-trace leaks), and streams the source-gen JSON.
            // Spec: internal design notes

            // Entitlement provider: constructed once per request (stateless read from disk cache).
            // Server constructs the provider here so Scope.Core stays free of ASP.NET dependencies.
            // The injected provider resolves to Free on any failure (never-crash guarantee).
            // Pagination (T-2026-06-17-03): accepts page/pageSize as an alternative to limit/offset.
            // page is 1-based; pageSize defaults to 10 (matches FE default). limit/offset are the raw
            // equivalents for back-compat. When page/pageSize are provided they take precedence.
            app.MapGet("/api/trace/sessions", async (HttpRequest req, CancellationToken ct) =>
            {
                var pageSize = ClampLimit(QueryInt(req, "pageSize", 10), 1, 100);
                var page = Math.Max(1, QueryInt(req, "page", 1));
                var offset = (page - 1) * pageSize;
                // Legacy limit/offset: only used when page/pageSize are not in the query.
                var hasPage = req.Query.ContainsKey("page") || req.Query.ContainsKey("pageSize");
                var limit = hasPage ? pageSize : ClampLimit(QueryInt(req, "limit", 100), 1, 1000);
                var finalOffset = hasPage ? offset : 0;
                return await ServeTraceAsync(req, ct, docs => docs.Sessions(
                    limit, TraceTier(req),
                    entitlementProvider: new DefaultEntitlementProvider(),
                    from: QueryStr(req, "from"), to: QueryStr(req, "to"),
                    includeDeleted: QueryBool(req, "includeDeleted", false),
                    offset: finalOffset)).ConfigureAwait(false);
            });

            // Soft-delete a session (T-2026-06-15-16/ST-01). Sets deleted_at only -- NO cascade; the session's
            // events/spans/requests/costs are untouched and restorable. 200 {session_key, deleted_at} on success,
            // 404 when the key is unknown. The key is URL-decoded by the route binder.
            app.MapDelete("/api/trace/sessions/{key}", async (string key, HttpRequest req, CancellationToken ct) =>
                await ServeTraceMutationAsync(req, ct, docs =>
                {
                    var deletedAt = docs.SoftDeleteSession(key);
                    return deletedAt != null
                        ? Results.Text(TraceJson.Serialize(new TraceSessionDeleteResult(key, deletedAt)), "application/json")
                        : TraceProblem("No session found for key: " + key, 404, "Session not found");
                }).ConfigureAwait(false));

            // Restore a soft-deleted session (T-2026-06-15-16/ST-01). Clears deleted_at. 200
            // {session_key, deleted_at:null} on success, 404 when the key is unknown.
            app.MapPost("/api/trace/sessions/{key}/restore", async (string key, HttpRequest req, CancellationToken ct) =>
                await ServeTraceMutationAsync(req, ct, docs =>
                    docs.RestoreSession(key)
                        ? Results.Text(TraceJson.Serialize(new TraceSessionDeleteResult(key, null)), "application/json")
                        : TraceProblem("No session found for key: " + key, 404, "Session not found")).ConfigureAwait(false));

            // Cross-agent cron run listing for the /trace page Cron tab (T-2026-06-15-16/ST-02). Reads scope.db
            // cron_runs JOIN cron_agents (read-only), keyed by session_key for deep-linking. Placed under /api/trace
            // to keep the trace page's two data sources adjacent. Optional limit (clamped), from, to (ISO-8601 UTC).
            app.MapGet("/api/trace/cron-sessions", async (HttpRequest req, CancellationToken ct) =>
            {
                var ctx = new ObservabilityContext(CallerSurface.Server, "cron-sessions", dispatchId: ObservabilityContext.ResolveDispatchId());
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var result = await ScopeApp.CronListSessionsAsync(
                    ClampLimit(QueryInt(req, "limit", 100), 1, 500),
                    QueryStr(req, "from"), QueryStr(req, "to"),
                    db.Path!, searchAllParents: false, jsonErrors: true,
                    includeDeleted: QueryBool(req, "includeDeleted", false),
                    ct, ctx: ctx).ConfigureAwait(false);
                return ToHttpResult(result);
            });

            app.MapGet("/api/trace/graph", async (HttpRequest req, CancellationToken ct) =>
            {
                var session = req.Query["session"].ToString();
                if (string.IsNullOrWhiteSpace(session))
                    return TraceProblem("Query parameter 'session' is required.", 400, "Missing query parameter");
                return await ServeTraceAsync(req, ct, docs => docs.SessionGraph(session, TraceTier(req),
                    entitlementProvider: new DefaultEntitlementProvider())).ConfigureAwait(false);
            });

            app.MapGet("/api/trace/full", async (HttpRequest req, CancellationToken ct) =>
            {
                var session = req.Query["session"].ToString();
                if (string.IsNullOrWhiteSpace(session))
                    return TraceProblem("Query parameter 'session' is required.", 400, "Missing query parameter");
                var cursor = req.Query["cursor"].ToString();
                // T-2026-06-17-03: page-based pagination mode (page + pageSize params).
                // When page/pageSize present, compute offset and use that for the page slice; cursor is ignored.
                // Back-compat: when absent, fall back to cursor/limit mode (legacy behavior).
                var hasPage = req.Query.ContainsKey("page") || req.Query.ContainsKey("pageSize");
                int pageSize = ClampLimit(QueryInt(req, "pageSize", 10), 1, 500);
                int page = Math.Max(1, QueryInt(req, "page", 1));
                int offset = hasPage ? (page - 1) * pageSize : 0;
                int limit = hasPage ? pageSize : ClampLimit(QueryInt(req, "limit", 200), 1, 2000);
                // assumed_model: optional user-selected model peg for the scope_estimated cost fallback
                // (T-2026-06-20-03, OBS-10). MUST mirror /api/trace/rollup exactly so the per-row attributed
                // timeline cost reconciles to the Accounting total under a non-default peg. Validated against
                // the pricing table; an unrecognized model falls back to the Sonnet default (deny-bad-input).
                var rawAssumedModel = req.Query["assumed_model"].ToString();
                if (string.IsNullOrWhiteSpace(rawAssumedModel)) rawAssumedModel = req.Query["assumedModel"].ToString();
                var assumedModel = !string.IsNullOrWhiteSpace(rawAssumedModel)
                    && Scope.Core.CostEstimation.ModelIdNormalizer.Lookup(rawAssumedModel, null) != null
                    ? rawAssumedModel
                    : null;
                return await ServeTraceAsync(req, ct, docs => docs.Trace(
                    session, TraceTier(req), limit,
                    hasPage ? null : (string.IsNullOrEmpty(cursor) ? null : cursor),
                    entitlementProvider: new DefaultEntitlementProvider(),
                    offset: offset, assumedModel: assumedModel)).ConfigureAwait(false);
            });

            app.MapGet("/api/trace/rollup", async (HttpRequest req, CancellationToken ct) =>
            {
                var session = req.Query["session"].ToString();
                if (string.IsNullOrWhiteSpace(session))
                    return TraceProblem("Query parameter 'session' is required.", 400, "Missing query parameter");
                // assumedModel: optional user-selected model peg for scope_estimated cost fallback.
                // Validated: if the model does not resolve in the pricing table, fall back to the Sonnet default.
                // Never throws, never returns 500 on an unrecognized model string (deny-bad-input).
                var rawAssumedModel = req.Query["assumedModel"].ToString();
                var assumedModel = !string.IsNullOrWhiteSpace(rawAssumedModel)
                    && Scope.Core.CostEstimation.ModelIdNormalizer.Lookup(rawAssumedModel, null) != null
                    ? rawAssumedModel
                    : null;
                return await ServeTraceAsync(req, ct, docs => docs.Rollup(session, TraceTier(req),
                    entitlementProvider: new DefaultEntitlementProvider(), assumedModel: assumedModel)).ConfigureAwait(false);
            });

            app.MapGet("/api/trace/top-actions", async (HttpRequest req, CancellationToken ct) =>
            {
                var session = req.Query["session"].ToString();
                if (string.IsNullOrWhiteSpace(session))
                    return TraceProblem("Query parameter 'session' is required.", 400, "Missing query parameter");
                return await ServeTraceAsync(req, ct, docs => docs.TopActions(
                    session, TraceTier(req), ClampLimit(QueryInt(req, "limit", 20), 1, 200),
                    entitlementProvider: new DefaultEntitlementProvider())).ConfigureAwait(false);
            });

            app.MapGet("/api/trace/wire", async (HttpRequest req, CancellationToken ct) =>
                await ServeTraceAsync(req, ct, docs => docs.Wire(
                    ClampLimit(QueryInt(req, "limit", 500), 1, 5000), TraceTier(req),
                    entitlementProvider: new DefaultEntitlementProvider())).ConfigureAwait(false));

            // ST-08: conversation-level rollup (additive, reader-gap-safe).
            app.MapGet("/api/trace/conversation-rollup", async (HttpRequest req, CancellationToken ct) =>
            {
                var conversationId = req.Query["conversation_id"].ToString();
                if (string.IsNullOrWhiteSpace(conversationId))
                    return TraceProblem("Query parameter 'conversation_id' is required.", 400, "Missing query parameter");
                return await ServeTraceAsync(req, ct, docs =>
                    docs.ConversationRollup(conversationId, TraceTier(req))).ConfigureAwait(false);
            });

            // ST-08: CLI-session rollup by base_session_key (additive, reader-gap-safe).
            app.MapGet("/api/trace/cli-session-rollup", async (HttpRequest req, CancellationToken ct) =>
            {
                var baseKey = req.Query["base_session_key"].ToString();
                if (string.IsNullOrWhiteSpace(baseKey))
                    return TraceProblem("Query parameter 'base_session_key' is required.", 400, "Missing query parameter");
                return await ServeTraceAsync(req, ct, docs =>
                    docs.CliSessionRollup(baseKey, TraceTier(req))).ConfigureAwait(false);
            });

            // SPA deep links fall back to index.html; mapped /api endpoints take precedence.
            if (spaProvider != null)
                app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaProvider });

            // XP7b: the SPA is served from an EMBEDDED manifest (ManifestEmbeddedFileProvider;
            // EmbeddedResource wwwroot/**) compiled into THIS assembly at build time. Scope.Server.dll
            // MUST be rebuilt AFTER build-frontend.ps1 stages the SPA into wwwroot/ for the fresh
            // assets to appear here. See scripts/install.ps1 and scripts/publish.ps1 for the correct
            // build order: build-frontend -> dotnet build (embeds SPA) -> pack/publish.
            await ((IHost)app).RunAsync(cancellationToken).ConfigureAwait(false);

            // ---- local functions that close over `app` or other RunAsync locals ----

            (string? Path, IResult? Problem) ResolveDbOrProblem()
            {
                var resolved = ScopeApp.FindDbPath(explicitPath: app.Configuration["db"]);
                return resolved != null
                    ? (resolved, null)
                    : (null, Results.Problem("No .scope/scope.db found.", statusCode: 500));
            }

            // Opens telemetry.db read-only, builds TraceDocuments, runs the projection callback, and
            // streams the JSON. An absent telemetry.db or observability schema yields an empty document of
            // the same kind (the projection methods naturally return empty bodies over empty tables). Any
            // projection failure is returned as a clean problem-details JSON, never a stack trace.
            async Task<IResult> ServeTraceAsync(HttpRequest req, CancellationToken ct, Func<TraceDocuments, string> project)
            {
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var scopeDir = Path.GetDirectoryName(db.Path!)!;
                var telemetryDbPath = Path.Combine(scopeDir, "telemetry.db");
                var identity = TraceIdentity.Resolve(scopeDir);
                var version = typeof(ScopeServerHost).Assembly.GetName().Version?.ToString() ?? "dev";

                // Resolve the pricing config once (ST-04 PricingResolver: SCOPE_PRICING_PATH /
                // .scope/pricing.json local override over the hourly-refreshed cache). Null when no
                // pricing is configured -> cost blocks stay unstamped and the SPA shows cost unavailable.
                // Never throws: any resolution failure simply yields null.
                Scope.Core.CostEstimation.ModelPricingConfig? resolvedPricing = null;
                string? pricesAsOf = null;
                try
                {
                    resolvedPricing = PricingResolver.TryResolve(scopeDir, Environment.GetEnvironmentVariable("SCOPE_PRICING_PATH"));
                    pricesAsOf = resolvedPricing?.PricesAsOf;
                }
                catch (Exception ex)
                {
                    Log.Debug("Pricing resolution for trace pricing_version failed; serving unstamped: {Message}", ex.Message);
                }

                try
                {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
                    await using var conn = await SQLite.OpenDbAsync(telemetryDbPath, ct).ConfigureAwait(false);
#pragma warning restore MA0004
                    // Ensure the obs schema is current before reading (T-2026-06-15-16). telemetry.db is migrated
                    // lazily by writers (the global scope hook), which deploy independently of this server; a reader
                    // that references a not-yet-migrated column (e.g. deleted_at) would 500. Ensuring here is
                    // idempotent (version-table check + ALTER only when behind) and forward-safe: a stale v7 writer
                    // tolerates a v8 db (explicit-column inserts, deleted_at defaults NULL).
                    new ObservabilitySchemaManager(conn).EnsureSchema();
                    var auditDb = new ObservabilityAuditDb(conn);
                    // Pass resolvedPricing as the estimationPricing override; when null the seed table
                    // is used as fallback so cron runs / traces with known models still show $ values.
                    var docs = new TraceDocuments(auditDb, identity, version, pricesAsOf, resolvedPricing);
                    var json = project(docs);
                    return Results.Text(json, "application/json");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    return TraceProblem("Telemetry database unavailable: " + ex.Message, 500, "Telemetry unavailable");
                }
            }

            // Opens telemetry.db read-write, builds TraceDocuments, and runs a mutation callback that returns its
            // own IResult (so the handler can choose 200/404). Used by the soft-delete and restore endpoints
            // (T-2026-06-15-16/ST-01). Mirrors ServeTraceAsync's db resolution and clean-error discipline; no pricing
            // resolution is needed because mutations never project cost. Any Sqlite failure is a clean problem.
            async Task<IResult> ServeTraceMutationAsync(HttpRequest req, CancellationToken ct, Func<TraceDocuments, IResult> mutate)
            {
                var db = ResolveDbOrProblem();
                if (db.Problem != null) return db.Problem;

                var scopeDir = Path.GetDirectoryName(db.Path!)!;
                var telemetryDbPath = Path.Combine(scopeDir, "telemetry.db");
                var identity = TraceIdentity.Resolve(scopeDir);
                var version = typeof(ScopeServerHost).Assembly.GetName().Version?.ToString() ?? "dev";

                try
                {
#pragma warning disable MA0004 // await using SqliteConnection: DisposeAsync is sync-safe; inner await already has ConfigureAwait(false)
                    await using var conn = await SQLite.OpenDbAsync(telemetryDbPath, ct).ConfigureAwait(false);
#pragma warning restore MA0004
                    // Ensure the obs schema is current before mutating (T-2026-06-15-16): soft-delete/restore touch
                    // deleted_at, which a lazily-migrated telemetry.db may not have yet. Idempotent + forward-safe.
                    new ObservabilitySchemaManager(conn).EnsureSchema();
                    var auditDb = new ObservabilityAuditDb(conn);
                    var docs = new TraceDocuments(auditDb, identity, version);
                    return mutate(docs);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    return TraceProblem("Telemetry database unavailable: " + ex.Message, 500, "Telemetry unavailable");
                }
            }
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    // ---- private static helpers (no outer-variable captures) ----

    // SPA provider selection seam (driven by the "spa" configuration value):
    //   "off"            -> null (API-only mode; deterministic seam used by endpoint tests so the
    //                       SPA fallback never shadows /api routes under assertion).
    //   <directory path> -> PhysicalFileProvider over that directory when it contains index.html.
    //                       Lets integration tests exercise the FULL SPA middleware pipeline
    //                       (default file, static files, deep-link fallback, /api precedence)
    //                       deterministically without compile-time embedding, and serves as a dev
    //                       convenience to serve frontend/dist without rebuilding Scope.Server.
    //   null/empty/other -> the EMBEDDED manifest baked into this assembly at build time.
    // Returns null when nothing resolves (API-only mode). Returns IFileProvider because the two
    // concrete providers (PhysicalFileProvider, ManifestEmbeddedFileProvider) share only the
    // interface; the SPA middleware is provider-agnostic.
    private static IFileProvider? ResolveSpaFileProvider(string? spaSetting)
    {
        if (string.Equals(spaSetting, "off", StringComparison.Ordinal))
            return null;

        if (!string.IsNullOrWhiteSpace(spaSetting) && Directory.Exists(spaSetting))
        {
            var physical = new PhysicalFileProvider(Path.GetFullPath(spaSetting));
            return physical.GetFileInfo("index.html").Exists ? physical : null;
        }

        return TryCreateSpaFileProvider();
    }

    // XP7b: SPA is served from an EMBEDDED manifest compiled into this assembly at build time.
    // TryCreateSpaFileProvider uses typeof(ScopeServerHost).Assembly so the manifest is found
    // whether launched as standalone Scope.Server or in-proc via scope serve (Scope.Cli).
    private static ManifestEmbeddedFileProvider? TryCreateSpaFileProvider()
    {
        try
        {
            var provider = new ManifestEmbeddedFileProvider(typeof(ScopeServerHost).Assembly, "wwwroot");
            return provider.GetFileInfo("index.html").Exists ? provider : null;
        }
        catch (Exception)
        {
            // No embedded manifest or no wwwroot root in it: API-only mode.
            return null;
        }
    }

    // Cron launcher registry resolution. Reads the "cron-harness" seam setting from configuration.
    // Production: null -> callers fall back to CronLauncherRegistry.CreateDefault().
    // Test seam "fake": returns a deterministic registry so endpoint tests never spawn processes.
    // This static helper is used both at DI registration time (builder.Services.AddSingleton) and
    // directly where needed. The DI-registered singleton is the primary path; endpoint handlers read
    // it from HttpContext.RequestServices so tests can override it via ConfigureTestServices.
    private static CronLauncherRegistry? ResolveCronRegistryFromConfig(ConfigurationManager config)
        => string.Equals(config["cron-harness"], "fake", StringComparison.Ordinal)
            ? new CronLauncherRegistry(
            [
                new ServerFakeHarnessLauncher(CronHarnesses.ClaudeCode, HarnessCapability.UnsupportedCli("Claude Code launch is not yet implemented.")),
            new ServerFakeHarnessLauncher(CronHarnesses.Codex, HarnessCapability.Available("Codex (fake test harness).")),
            ])
            : null;

    // ---- scope-trace-v1 endpoint helpers (ST-12) ----------------------------------

    // Parses a query-string integer WITHOUT model binding, so a hostile value (e.g. ?limit=abc or
    // ?limit=99999999999999999999) returns the default instead of leaking an int-binding stack trace.
    private static int QueryInt(HttpRequest req, string name, int fallback)
    {
        var raw = req.Query[name].ToString();
        return int.TryParse(raw, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    // Returns a trimmed query-string value, or null when absent/blank. Used for the optional ISO-8601
    // from/to date filters (T-2026-06-15-16/ST-01); a blank value is treated as "no bound".
    private static string? QueryStr(HttpRequest req, string name)
    {
        var raw = req.Query[name].ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    // Parses a query-string boolean leniently ("true"/"1" -> true, "false"/"0" -> false), falling back
    // to the default on any other value. No model binding so a hostile value never leaks a stack trace.
    private static bool QueryBool(HttpRequest req, string name, bool fallback)
    {
        var raw = req.Query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (bool.TryParse(raw, out var b)) return b;
        return raw switch { "1" => true, "0" => false, _ => fallback };
    }

    private static int ClampLimit(int value, int min, int max) => Math.Clamp(value, min, max);

    private static string TraceTier(HttpRequest req)
        => TraceDocuments.ResolveTier(req.Query["tier"].ToString());

    private static IResult TraceProblem(string detail, int status, string title)
        => Results.Problem(detail: detail, statusCode: status, title: title);

    private static IResult ToHttpResult(ScopeCommandResult result)
    {
        if (result.ExitCode == (int)ScopeExitCode.Success)
            return Results.Text(result.Stdout ?? "", "application/json");

        var error = TryReadError(result.Stderr);
        if (error == null)
            return Results.Problem(result.Stderr ?? "Scope command failed.", statusCode: 500);

        return Results.Problem(
            detail: error.Message,
            statusCode: StatusCodeFor(error.ExitCode),
            title: error.Code,
            extensions: error.Details.HasContent
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["details"] = error.Details, ["exit_code"] = error.ExitCode }
                : new Dictionary<string, object?>(StringComparer.Ordinal) { ["exit_code"] = error.ExitCode });
    }

    private static string? FindDbArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--db", StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    private static ScopeAppError? TryReadError(string? stderr)
    {
        if (stderr.IsNullOrWhitespace)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(stderr);
            var root = doc.RootElement;
            return new ScopeAppError(
                root.GetProperty("code").GetString() ?? "Unknown",
                root.GetProperty("exit_code").GetInt32(),
                root.GetProperty("message").GetString() ?? "Scope command failed.",
                root.TryGetProperty("details", out var details) ? details.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    private static int StatusCodeFor(int exitCode) => (ScopeExitCode)exitCode switch
    {
        ScopeExitCode.Success => 200,
        ScopeExitCode.NoSymbolFound => 404,
        ScopeExitCode.AmbiguousSymbol => 409,
        ScopeExitCode.InvalidTaskType => 400,
        ScopeExitCode.InvalidOutputFormat => 400,
        ScopeExitCode.MissingParameter => 400,
        ScopeExitCode.InvalidParameter => 400,
        ScopeExitCode.FileNotFound => 404,
        ScopeExitCode.OverBudget => 400,
        ScopeExitCode.StaleIndex => 409,
        ScopeExitCode.ConcurrentRunConflict => 409,
        _ => 500
    };
}

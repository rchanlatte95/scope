namespace Scope.Server;

using Serilog;

/// <summary>
/// Server-side registry for background cron run tasks. The server starts a cron run (202) and
/// immediately registers the continuation Task here so it is neither fire-and-forget nor GC'd
/// before completion.
///
/// Design:
/// - Track is called from the 202 handler; the Task is already started (Task.Run is used in
///   CronRunner.BeginRunAsync). Track wraps it with a continuation that logs failures and
///   removes the entry, preserving the must-never-crash posture.
/// - DrainAsync is called on server shutdown (lifetime.ApplicationStopping) to await all
///   in-flight tasks up to the given deadline.
/// - Thread-safe via a ConcurrentDictionary; the tracked continuations are never re-awaited and
///   never cancelled by this type (the harness owns its own cancellation).
/// </summary>
public sealed class CronBackgroundTaskService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);

    /// <summary>Number of in-flight cron background runs currently tracked.</summary>
    public int ActiveCount => _tasks.Count;

    /// <summary>
    /// Registers a cron continuation Task. The Task must already be started. If it is
    /// <see cref="Task.CompletedTask"/> (skip/early-fail path) the entry is dropped immediately
    /// rather than stored.
    /// </summary>
    public void Track(string runId, Task completion)
    {
        if (completion.IsCompleted) return;

        // Wrap so: (a) failures are logged and (b) the entry is removed when done.
        var wrapped = completion.ContinueWith(t =>
        {
            _tasks.TryRemove(runId, out _);
            if (t.IsFaulted && t.Exception is { } ex)
            {
                Log.Warning("Cron background run {RunId} faulted (non-fatal): {Message}",
                    runId, ex.GetBaseException().Message);
            }
        }, TaskScheduler.Default);

        _tasks[runId] = wrapped;
    }

    /// <summary>
    /// Awaits all tracked tasks up to <paramref name="deadline"/>. Called on graceful shutdown.
    /// Any task that does not finish within the deadline is logged as a warning and abandoned;
    /// this method never throws.
    /// </summary>
    public async Task DrainAsync(TimeSpan deadline)
    {
        var snapshot = _tasks.Values.ToArray();
        if (snapshot.Length == 0) return;

        Log.Information("Cron background drain: waiting for {Count} in-flight run(s).", snapshot.Length);
        try
        {
            var all = Task.WhenAll(snapshot);
            using var cts = new CancellationTokenSource(deadline);
            await all.WaitAsync(cts.Token).ConfigureAwait(false);
            Log.Information("Cron background drain: all tasks completed.");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Cron background drain: {Count} task(s) did not finish within the deadline.",
                _tasks.Count);
        }
        catch (Exception ex)
        {
            Log.Warning("Cron background drain: unexpected error (non-fatal): {Message}", ex.Message);
        }
    }
}

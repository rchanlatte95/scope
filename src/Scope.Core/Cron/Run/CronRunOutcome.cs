namespace Scope.Core.Cron.Run;

/// <summary>
/// Result of a cron run-now attempt. Always carries the persisted <see cref="CronRunRecord"/> (the
/// row written for this attempt, whether it launched, failed, or was skipped) so adapters can report
/// the run id, dispatch id, session key, status, and skip/failure detail without a second query.
/// </summary>
/// <param name="Run">The persisted run row in its final state for this attempt.</param>
/// <param name="Launched">
/// True when a harness process was actually launched (even if it then exited non-zero). False for a
/// concurrency skip or a capability-unavailable run that never started a process.
/// </param>
/// <param name="CapabilityDetail">
/// The harness capability detail string when the run did not start because the harness was not
/// available; null otherwise. Surfaced verbatim so a degraded harness is never a silent no-op.
/// </param>
public sealed record CronRunOutcome(
    CronRunRecord Run,
    bool Launched,
    string? CapabilityDetail);

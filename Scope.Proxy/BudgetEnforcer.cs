namespace Scope.Proxy;

using System.Collections.Concurrent;

/// <summary>
/// Per-session token-budget gate (T-2026-06-17-06): the proxy's FIRST enforcement behavior. The
/// proxy is the platform-independent enforcement point because it sees every model call and the
/// running per-session token total. This is a LOCAL gate, not an uplink and not a gateway: it never
/// mutates a forwarded body, never adds latency to an allowed call, and is content-free (it counts
/// only the input+output+cache token numbers the meter already parses, keyed by an opaque session id).
///
/// Mechanics. A budgeted run carries its session id + budget in the proxy URL path
/// (<c>/t/{token}/sb/{session}/{budget}/v1/messages</c>), so every request the harness makes self-declares
/// the session and cap without Scope having to inspect the body. Before forwarding a NEW
/// <c>/v1/messages</c> call the forwarder asks <see cref="ShouldRefuse"/>; if the session's cumulative
/// wire tokens already meet or exceed the budget, the call is refused with a TERMINAL, non-retryable
/// response (decision 1) so the harness stops instead of looping against the wall. After each metered
/// call completes, <see cref="AddTokens"/> folds that call's tokens into the session tally.
///
/// Threshold semantics (proof-correct). The refusal fires at <c>cumulative &gt;= budget</c>: the FIRST
/// request whose start finds the budget already met/exceeded is refused. This caps NEW spend; the call
/// that crossed the budget is allowed to complete (the proxy cannot un-send an in-flight response), and
/// its tokens are still counted so the recorded total is honest. The supervisor's hard force-kill at
/// budget + grace is the load-bearing backstop when a harness ignores this refusal.
/// </summary>
internal sealed class BudgetEnforcer
{
    private readonly ConcurrentDictionary<string, long> _cumulativeBySession = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the session's current cumulative wire-token total (0 when never seen). Used for tests
    /// and diagnostics; the forwarder uses <see cref="ShouldRefuse"/> and <see cref="AddTokens"/>.
    /// </summary>
    public long CumulativeTokens(string sessionId)
        => _cumulativeBySession.TryGetValue(sessionId, out var v) ? v : 0;

    /// <summary>
    /// True when a new metered call for this session must be refused because the session's cumulative
    /// wire tokens already meet or exceed its budget. A non-positive budget disables the gate (treated
    /// as "no budget"), so an unbudgeted scope (<see cref="BudgetScope"/> absent) never refuses.
    /// </summary>
    public bool ShouldRefuse(BudgetScope scope)
        => scope.Budget > 0 && CumulativeTokens(scope.SessionId) >= scope.Budget;

    /// <summary>
    /// Folds a completed metered call's total wire tokens (input + output + cache_creation + cache_read)
    /// into the session tally. Negative or zero contributions are ignored. Thread-safe.
    /// </summary>
    public void AddTokens(string sessionId, long tokens)
    {
        if (tokens <= 0) return;
        _cumulativeBySession.AddOrUpdate(sessionId, tokens, (_, prev) => prev + tokens);
    }
}

/// <summary>
/// Parsed budget scope from a proxy request path: the opaque session id and the enforced total-token
/// budget. The forwarder extracts this from the optional <c>/sb/{session}/{budget}</c> segment that a
/// budgeted launch embeds in <c>ANTHROPIC_BASE_URL</c>. Absent for un-budgeted callers.
/// </summary>
internal readonly record struct BudgetScope(string SessionId, long Budget);

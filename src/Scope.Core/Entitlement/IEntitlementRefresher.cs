namespace Scope.Core.Entitlement;

/// <summary>
/// Contract for the control-plane entitlement refresh client.
///
/// Implementations fetch a fresh signed token envelope from the control plane, verify it,
/// and persist it via <see cref="EntitlementCache"/>. The refresh is intended to run
/// periodically and silently in the background.
///
/// Failure semantics that every implementation MUST honour:
/// <list type="bullet">
///   <item>Control plane unreachable: the existing cached token governs (with grace window
///   applied). Do not clear or overwrite the cache on a network failure.</item>
///   <item>No cached token and control plane unreachable: free tier is fully functional;
///   paid features are unavailable until a successful refresh.</item>
///   <item>Refresh must never throw to callers and must never block core read commands.
///   Wrap all I/O in try/catch; log failures at Debug or Warning.</item>
///   <item>A successfully fetched but invalid signature must NOT be persisted; log the
///   failure and retain the last-known-good cached token.</item>
/// </list>
///
/// Implementation is blocked on the activation-UX decision (device-code vs license-key vs OAuth).
/// The no-op stub is the only implementation shipping in this lane.
/// </summary>
public interface IEntitlementRefresher
{
    /// <summary>
    /// Attempts to fetch a fresh entitlement token from the control plane and update the
    /// local cache. Returns true if the cache was successfully updated; false if the
    /// refresh was skipped or failed (callers should continue with the cached state).
    /// Never throws.
    /// </summary>
    Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op entitlement refresher stub. Always reports that refresh was skipped.
///
/// This stub is the production implementation for this lane. A real refresh client will
/// replace it once the activation-UX decision is made. Callers that hold an
/// <see cref="IEntitlementRefresher"/> will transparently pick up the real client when it
/// is wired in behind this interface.
/// </summary>
public sealed class NoOpEntitlementRefresher : IEntitlementRefresher
{
    /// <inheritdoc />
    public Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

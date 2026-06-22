namespace Scope.Core.Entitlement;

using Serilog;

/// <summary>
/// Seam for reading the current machine-level entitlement state.
///
/// Callers (e.g. <c>CronService</c>) use this to gate feature access without
/// hard-wiring the cache/crypto pipeline. Tests inject a fake implementation.
///
/// Implementations MUST never throw; any failure must degrade to
/// <see cref="EntitlementState.FreeState"/>.
/// </summary>
public interface IEntitlementProvider
{
    /// <summary>
    /// Returns the current resolved entitlement state for this machine.
    /// Never throws; returns <see cref="EntitlementState.FreeState"/> on any failure.
    /// </summary>
    EntitlementState GetState();
}

/// <summary>
/// Default implementation of <see cref="IEntitlementProvider"/> that composes the disk
/// cache, offline token verifier, and state resolver.
///
/// Resolution pipeline:
/// <list type="number">
///   <item>Read the signed envelope from <c>%APPDATA%/.scope/entitlement.json</c> via <see cref="EntitlementCache"/>.</item>
///   <item>Verify the envelope offline via <see cref="EntitlementTokenVerifier"/> against the pinned production key set.</item>
///   <item>Resolve the verified token at <see cref="DateTime.UtcNow"/> via <see cref="EntitlementStateResolver"/>.</item>
/// </list>
///
/// Any exception at any step degrades to <see cref="EntitlementState.FreeState"/>.
/// A token that does not verify against any pinned key (or no/expired token, or no network)
/// also degrades to Free - a missing/forged signature can only ever DENY entitlement, never grant it.
/// </summary>
public sealed class DefaultEntitlementProvider : IEntitlementProvider
{
    private readonly EntitlementTokenVerifier _verifier;

    /// <summary>
    /// Creates a provider using the pinned production key set (the runtime default).
    /// Tokens that verify against a pinned key resolve to their tier; anything else degrades to Free.
    /// </summary>
    public DefaultEntitlementProvider() : this(new EntitlementTokenVerifier()) { }

    /// <summary>
    /// Creates a provider using the supplied <paramref name="verifier"/>.
    /// Intended for testing with an injected keypair.
    /// </summary>
    public DefaultEntitlementProvider(EntitlementTokenVerifier verifier)
    {
        _verifier = verifier;
    }

    /// <inheritdoc />
    public EntitlementState GetState()
    {
        try
        {
            var envelope = EntitlementCache.TryReadEnvelope();
            if (envelope is null)
                return EntitlementState.FreeState;

            var result = _verifier.Verify(envelope);
            if (!result.IsValid)
            {
                Log.Debug("DefaultEntitlementProvider: token verification failed ({Reason}); resolving to Free", result.FailureReason);
                return EntitlementState.FreeState;
            }

            return EntitlementStateResolver.Resolve(result.Token, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // Must-never-throw posture: degrade to Free on any unexpected failure.
            Log.Warning("DefaultEntitlementProvider.GetState threw unexpectedly ({Message}); resolving to Free", ex.Message);
            return EntitlementState.FreeState;
        }
    }
}

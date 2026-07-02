namespace Scope.DevTools.EntitlementIssuer;

using Scope.Core.Entitlement;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Claims for a dev/test entitlement token. Every field maps 1:1 onto the production
/// <see cref="EntitlementToken"/> contract; this is not a parallel token format.
/// </summary>
public sealed record EntitlementIssueOptions
{
    /// <summary>
    /// Path to a file containing a base64-encoded PKCS8 ECDSA P-256 private key (the same
    /// encoding <c>ECDsa.ExportPkcs8PrivateKey()</c> + <c>Convert.ToBase64String</c> produces --
    /// see the keypair-generation snippet in docs/entitlement-key-rotation.md). The file MUST
    /// live outside this repo; this tool never writes, copies, or logs the key material, only
    /// reads it into memory for the duration of signing.
    /// </summary>
    public required string PrivateKeyFilePath { get; init; }

    /// <summary>Entitlement tier claim: "free", "team", or "enterprise" (see <see cref="EntitlementTiers"/>).</summary>
    public string Tier { get; init; } = EntitlementTiers.Team;

    public string? OrgId { get; init; } = "dev-issued";
    public string? SeatId { get; init; } = "dev-seat";

    /// <summary>Explicit expiry. When null, computed as now + <see cref="ExpiresInDays"/>.</summary>
    public DateTime? ExpiresAtUtc { get; init; }

    /// <summary>Days from now until expiry, used only when <see cref="ExpiresAtUtc"/> is null.</summary>
    public double ExpiresInDays { get; init; } = 30;

    /// <summary>Trial end timestamp; null means the token is not a trial.</summary>
    public DateTime? TrialEndsAtUtc { get; init; }

    /// <summary>Optional per-token grace-window override in hours (see EntitlementToken.GraceHours).</summary>
    public int? GraceHours { get; init; }

    /// <summary>Null means unlimited (paid default); a value sets an explicit cap.</summary>
    public int? MaxEnabledCrons { get; init; }

    /// <summary>Null means unlimited (paid default); a value sets an explicit cap.</summary>
    public int? ObservabilityRetentionDays { get; init; }

    /// <summary>Null means unlimited (paid default); a value sets an explicit cap.</summary>
    public int? MaxDashboardSessions { get; init; }

    public bool AuditExportAllowed { get; init; } = true;
}

/// <summary>
/// DEV-ONLY offline entitlement-token issuer. Signs with a runtime-supplied private key file
/// (never a key baked into this repo).
///
/// This class deliberately reuses, rather than reimplements, the production signing/contract
/// surface:
/// <list type="bullet">
///   <item><see cref="EntitlementTokenVerifier.BuildSignedEnvelope"/> -- the exact signer the
///     verifier's own unit tests use (test/Scope.Tests/Entitlement/EntitlementTokenVerifierTests.cs,
///     EntitlementActivationTests.cs).</item>
///   <item><see cref="EntitlementToken"/> / <see cref="EntitlementLimits"/> / <see cref="EntitlementJsonContext"/>
///     -- the exact production model types and source-generated JSON contract, so the emitted
///     payload can never drift from what <see cref="EntitlementTokenVerifier.Verify"/> expects.</item>
/// </list>
///
/// This class never reads, references, or constructs anything from
/// <see cref="EntitlementTokenVerifier.ProductionPublicKeysBase64"/>; it only signs with a
/// caller-supplied key. It cannot grant entitlement against the real pinned production keys
/// without the matching production PRIVATE key, which never exists in this repo or on a dev
/// machine (control plane / secret manager only).
/// </summary>
public static class TokenIssuer
{
    /// <summary>Signs and returns a full envelope: base64url(payload).base64url(signature).</summary>
    public static string Issue(EntitlementIssueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var privateKey = LoadPrivateKey(options.PrivateKeyFilePath);
        var token = BuildToken(options);
        var payloadJson = JsonSerializer.Serialize(token, EntitlementJsonContext.Default.EntitlementToken);
        return EntitlementTokenVerifier.BuildSignedEnvelope(payloadJson, privateKey);
    }

    /// <summary>
    /// Loads an ECDSA P-256 private key from a file holding base64 PKCS8 DER. The caller owns
    /// disposal of the returned key. Throws on a missing file or malformed/non-P-256 key; this
    /// tool never silently falls back to a different key.
    /// </summary>
    public static ECDsa LoadPrivateKey(string privateKeyFilePath)
    {
        if (string.IsNullOrWhiteSpace(privateKeyFilePath))
            throw new ArgumentException("A private key file path is required.", nameof(privateKeyFilePath));
        if (!File.Exists(privateKeyFilePath))
            throw new FileNotFoundException($"Private key file not found: {privateKeyFilePath}", privateKeyFilePath);

        var base64 = File.ReadAllText(privateKeyFilePath).Trim();
        var der = Convert.FromBase64String(base64);

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(der, out _);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
        return ecdsa;
    }

    private static EntitlementToken BuildToken(EntitlementIssueOptions options)
    {
        var now = DateTime.UtcNow;
        var expiresAt = options.ExpiresAtUtc ?? now.AddDays(options.ExpiresInDays);

        return new EntitlementToken
        {
            TokenVersion = 1,
            Tier = options.Tier,
            OrgId = options.OrgId,
            SeatId = options.SeatId,
            Limits = new EntitlementLimits
            {
                MaxEnabledCrons = options.MaxEnabledCrons,
                ObservabilityRetentionDays = options.ObservabilityRetentionDays,
                MaxDashboardSessions = options.MaxDashboardSessions,
                AuditExportAllowed = options.AuditExportAllowed,
            },
            IssuedAtUtc = now.ToString("o", CultureInfo.InvariantCulture),
            ExpiresAtUtc = expiresAt.ToString("o", CultureInfo.InvariantCulture),
            TrialEndsAtUtc = options.TrialEndsAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            GraceHours = options.GraceHours,
        };
    }
}

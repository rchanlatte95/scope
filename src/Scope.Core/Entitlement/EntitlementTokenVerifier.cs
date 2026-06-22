namespace Scope.Core.Entitlement;

using Serilog;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Outcome of token verification. Callers check <see cref="IsValid"/> and, on success,
/// read <see cref="Token"/>; on failure, read <see cref="FailureReason"/> for diagnostics.
/// </summary>
public sealed record EntitlementVerifyResult
{
    public bool IsValid { get; init; }
    public EntitlementToken? Token { get; init; }
    public string? FailureReason { get; init; }

    public static EntitlementVerifyResult Valid(EntitlementToken token) => new() { IsValid = true, Token = token };
    public static EntitlementVerifyResult Fail(string reason) => new() { IsValid = false, FailureReason = reason };
}

/// <summary>
/// Offline verifier for signed entitlement token envelopes.
///
/// Envelope format: base64url(payloadJson) "." base64url(signature)
///
/// Signing algorithm: ECDSA P-256 + SHA-256 (System.Security.Cryptography.ECDsa -- built-in,
/// no NuGet dependency, AOT-compatible).
///
/// Accepts ECDSA P-256 / SHA-256 signatures in EITHER IEEE-P1363 (raw 64-byte r||s) or
/// DER/ASN.1 (RFC 3279) encoding; the control plane may emit either. Both encodings are
/// tried; the token is accepted if either matches.
///
/// The verifying public keys are a build-time PINNED SET (see <see cref="ProductionPublicKeysBase64"/>).
/// A token is accepted if its signature matches ANY key in the set; this enables key rotation
/// without an outage. The matching private keys live only in the control plane and NEVER exist
/// in this repo.
///
/// Verification is always offline and NEVER throws; any failure returns a typed failure
/// result with a diagnostic reason string.
/// </summary>
public sealed class EntitlementTokenVerifier
{
    // ---------------------------------------------------------------------------
    // Pinned production public keys (rotation set)
    //
    // These are PUBLIC keys (safe to commit) pinned at BUILD time. They are intentionally
    // NOT overridable at runtime (no env var / no config): a runtime override would let an
    // attacker substitute their own key and self-issue entitlement tokens to unlock paid
    // features. Rotation is therefore a release, not a config change.
    //
    // To ROTATE: add the new public key to the array below and ship. During the overlap
    // window both old- and new-signed tokens validate (the verifier accepts ANY key in the
    // set). Once all live tokens have been re-issued under the new key, drop the old key in a
    // later release. The matching PRIVATE keys exist ONLY in the control plane.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Pinned production entitlement-signing PUBLIC keys (P-256 SubjectPublicKeyInfo / DER, base64).
    /// A token is accepted if it verifies against ANY entry. Immutable + build-time pinned: see
    /// the security note above. Add a key here to rotate; never make this runtime-overridable.
    /// </summary>
    public static readonly ImmutableArray<string> ProductionPublicKeysBase64 =
    [
        // key-1 (added 2026-06-14): primary production signing key.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEorbxQQ8PzxmclVjqnSTJdktEgFppIkqdfvxR/RRJR2pOqg7OYjX6qSMyVYXSQZELUA4CuGH4kRL7fKf2sjyjPA==",
    ];

    private readonly IReadOnlyList<ECDsa> _keys;

    /// <summary>
    /// Creates a verifier using the embedded pinned production key set
    /// (<see cref="ProductionPublicKeysBase64"/>). This is the runtime default. A key that
    /// fails to import is skipped; if none import, the verifier rejects all tokens (degrade to Free).
    /// </summary>
    public EntitlementTokenVerifier() : this(LoadProductionKeys()) { }

    /// <summary>
    /// Creates a verifier using a single supplied ECDsa public key. The caller owns the
    /// key's lifetime; the verifier does not dispose it.
    /// </summary>
    public EntitlementTokenVerifier(ECDsa publicKey) : this([publicKey]) { }

    /// <summary>
    /// Creates a verifier using a SET of public keys. A token is accepted if it verifies
    /// against any of them (rotation set). The caller owns the keys' lifetimes.
    /// </summary>
    public EntitlementTokenVerifier(IEnumerable<ECDsa> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        _keys = publicKeys is IReadOnlyList<ECDsa> list ? list : publicKeys.ToList();
    }

    /// <summary>
    /// Creates a verifier by importing a SubjectPublicKeyInfo DER blob (base64-encoded).
    /// INTENTIONALLY internal, not public: the production verification key is the build-time
    /// pinned set (<see cref="ProductionPublicKeysBase64"/>) and must NOT be constructable from
    /// runtime/attacker-controlled input, or entitlement could be self-granted. For tests/tooling only.
    /// </summary>
    internal static EntitlementTokenVerifier FromBase64SubjectPublicKeyInfo(string base64Der)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64Der), out _);
        return new EntitlementTokenVerifier(ecdsa);
    }

    /// <summary>
    /// Verifies a signed entitlement token envelope.
    ///
    /// Envelope format: {base64url(payloadJson)}.{base64url(signature)}
    ///
    /// Returns a typed result; never throws. Any exception (malformed input, crypto failure,
    /// JSON parse error) is caught and returned as a failure result with a reason string.
    /// </summary>
    public EntitlementVerifyResult Verify(string envelope)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(envelope))
                return EntitlementVerifyResult.Fail("envelope is null or empty");

            var dotIndex = envelope.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex < 0)
                return EntitlementVerifyResult.Fail("envelope missing separator '.'");

            var payloadPart = envelope[..dotIndex];
            var signaturePart = envelope[(dotIndex + 1)..];

            if (payloadPart.Length == 0 || signaturePart.Length == 0)
                return EntitlementVerifyResult.Fail("envelope has empty payload or signature segment");

            byte[] payloadBytes;
            byte[] signatureBytes;
            try
            {
                payloadBytes = DecodeBase64Url(payloadPart);
                signatureBytes = DecodeBase64Url(signaturePart);
            }
            catch (Exception ex)
            {
                return EntitlementVerifyResult.Fail($"base64url decode failed: {ex.Message}");
            }

            // Verify the ECDSA signature over the raw payload bytes (not re-encoded) against the
            // pinned key SET: the token is accepted if it verifies under ANY key (rotation), trying
            // both IEEE-P1363 (raw 64-byte r||s) and DER/ASN.1 (RFC 3279) encodings for each key.
            // A CryptographicException from parsing a signature in the wrong format for a given key
            // is treated as a non-match, not an error.
            bool signatureValid = false;
            foreach (var key in _keys)
            {
                if (TryVerifySignature(key, payloadBytes, signatureBytes))
                {
                    signatureValid = true;
                    break;
                }
            }

            if (!signatureValid)
                return EntitlementVerifyResult.Fail("signature is invalid (payload tampered or no pinned key matched; tried P1363 and DER/ASN.1 encodings across all keys)");

            // Deserialize the payload JSON.
            EntitlementToken? token;
            try
            {
                var payloadJson = Encoding.UTF8.GetString(payloadBytes);
                token = JsonSerializer.Deserialize(payloadJson, EntitlementJsonContext.Default.EntitlementToken);
            }
            catch (Exception ex)
            {
                return EntitlementVerifyResult.Fail($"payload JSON deserialization failed: {ex.Message}");
            }

            if (token is null)
                return EntitlementVerifyResult.Fail("payload deserialized to null");

            return EntitlementVerifyResult.Valid(token);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders: must never throw to callers.
            Log.Debug("EntitlementTokenVerifier.Verify threw unexpectedly: {Message}", ex.Message);
            return EntitlementVerifyResult.Fail($"unexpected verifier error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Tries to verify a signature against a single key, accepting either the IEEE-P1363 or the
    /// DER/ASN.1 encoding. A <see cref="CryptographicException"/> (wrong format/length for this
    /// encoding) is treated as a non-match. Never throws.
    /// </summary>
    private static bool TryVerifySignature(ECDsa key, byte[] payloadBytes, byte[] signatureBytes)
    {
        try
        {
            if (key.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return true;
        }
        catch (CryptographicException) { /* wrong encoding/length for P1363; try DER */ }

        try
        {
            if (key.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                return true;
        }
        catch (CryptographicException) { /* DER parse also failed; non-match */ }

        return false;
    }

    /// <summary>
    /// Imports the pinned production public keys. A key that fails to import is logged and skipped;
    /// if none import, the returned set is empty and the verifier rejects all tokens (degrade to
    /// Free). Never throws. The "no key matches -&gt; Free" posture means a missing/broken key can
    /// only ever DENY entitlement, never grant it.
    /// </summary>
    private static List<ECDsa> LoadProductionKeys()
    {
        var keys = new List<ECDsa>(ProductionPublicKeysBase64.Length);
        foreach (var base64 in ProductionPublicKeysBase64)
        {
            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
                keys.Add(ecdsa);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    "EntitlementTokenVerifier: a pinned production key failed to import ({Message}); " +
                    "skipping it. Tokens signed by that key will be rejected.",
                    ex.Message);
            }
        }

        if (keys.Count == 0)
        {
            Log.Warning(
                "EntitlementTokenVerifier: no production keys loaded; ALL tokens will be rejected " +
                "(degrade to Free). This indicates a build/key-configuration problem.");
        }

        return keys;
    }

    /// <summary>
    /// Decodes a base64url (RFC 4648 §5) string to bytes.
    /// Handles missing padding; throws on invalid characters.
    /// </summary>
    private static byte[] DecodeBase64Url(string input)
    {
        // Replace base64url chars with standard base64 chars.
        var s = input.Replace('-', '+').Replace('_', '/');

        // Re-add padding if needed.
        var padding = s.Length % 4;
        if (padding == 2) s += "==";
        else if (padding == 3) s += "=";

        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Encodes bytes to a base64url (RFC 4648 §5) string without padding.
    /// Used by tests to build signed envelopes for the test key.
    /// </summary>
    public static string EncodeBase64Url(byte[] input)
        => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Builds a signed envelope for a given payload JSON and ECDsa private key.
    /// Intended for test helpers and the control-plane token issuer; not used at runtime.
    /// </summary>
    public static string BuildSignedEnvelope(string payloadJson, ECDsa signingKey)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = signingKey.SignData(payloadBytes, HashAlgorithmName.SHA256);
        return $"{EncodeBase64Url(payloadBytes)}.{EncodeBase64Url(signature)}";
    }
}

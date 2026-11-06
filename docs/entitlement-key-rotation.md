# Entitlement Signing Key Rotation

Status: ACTIVE policy. Decided 2026-06-14 (owner). This document is checked into the repo on
purpose so every agent and human knows the entitlement system is built for key rotation and how
to perform one. It contains only PUBLIC information. No secret ever belongs in this repo.

## The decision

Entitlement (Free vs Paid/Trial) is gated by **signed entitlement tokens**. Signing is
**ECDSA P-256 + SHA-256**. The Scope client verifies tokens **offline** using a **pinned SET of
production PUBLIC keys**, not a single key. A token is accepted if its signature matches **any**
key in the set. The set exists so we can **rotate signing keys with zero downtime**.

Authoritative code: `src/Scope.Core/Entitlement/EntitlementTokenVerifier.cs`
(`ProductionPublicKeysBase64`). Provider wiring: `src/Scope.Core/Entitlement/IEntitlementProvider.cs`.

## Security invariants (do not violate)

1. **Private keys NEVER live in this repo.** They exist only in the control plane / a secret
   manager (ideally an HSM). Only PUBLIC keys are committed. A public key is not a secret.
2. **The verification key is build-time pinned and NOT runtime-overridable.** There is no env var
   or config that changes which key verifies tokens. If a user could supply the verification key,
   they could sign their own tokens and self-grant Paid. Rotation is therefore a code release, by
   design, not a config change.
3. **Fail closed, toward Free.** A missing, expired, forged, or unverifiable token always degrades
   to Free. A missing or broken key can only ever DENY entitlement, never grant it. There is no
   code path where a verification failure resolves to anything but Free.
4. **Never crash callers.** `Verify(...)` never throws; any failure returns a typed failure result.

## Control-plane issuer contract

The signing service MUST emit **explicit numeric limits** in every paid token. A `limits` object
that omits a numeric field deserializes that field to `null`, which the client treats as
**unlimited** for that dimension. (A token with no `limits` object at all falls back to free-tier
defaults; a present-but-partial `limits` object does not.) This only matters on the trusted
issuer side: a client cannot exploit it without a valid control-plane signature, but a sloppy
issuer could silently grant unlimited. Always serialize all four limit fields explicitly.

All token timestamps (`issued_at_utc`, `expires_at_utc`, `trial_ends_at_utc`) are UTC and SHOULD
carry a trailing `Z`. The client parses them UTC-strict (a naive value is assumed UTC), so a
missing `Z` will not skew expiry, but emit `Z` anyway for clarity.

## Current state

- **key-1** (added 2026-06-14): primary production signing key. This is the initial seed, not an
  in-flight rotation. The previous build-time *placeholder* key (whose private half was discarded)
  has been removed entirely.

## How to rotate (runbook)

1. **Generate a new P-256 keypair** on the control-plane / signing host (see snippet below).
2. **Store the new PRIVATE key** in the secret manager. Never paste it anywhere, never commit it.
3. **Add the new PUBLIC key** as a new entry in `ProductionPublicKeysBase64` (keep the old one).
   Date the comment. Ship the release.
4. **Overlap window:** both old-signed (already-cached) and new-signed tokens now verify. Begin
   issuing new tokens with the new private key from the control plane.
5. **Once all live tokens have been re-issued** under the new key (i.e. past the max token TTL),
   **remove the old PUBLIC key** in a later release. Rotation complete.

Emergency revocation (suspected private-key compromise): remove the compromised public key
immediately and ship; all tokens signed by it stop verifying (their holders fall back to Free).

## Generating a keypair (PowerShell, matches the .NET verifier format)

```powershell
$ec   = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$pub  = [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo())   # <- add THIS to ProductionPublicKeysBase64
$priv = [Convert]::ToBase64String($ec.ExportPkcs8PrivateKey())        # <- store in secret manager, NEVER commit
"PUBLIC:";  $pub
"PRIVATE:"; $priv
```

The public string is base64 `SubjectPublicKeyInfo` (SPKI/DER), which the verifier imports via
`ImportSubjectPublicKeyInfo`.

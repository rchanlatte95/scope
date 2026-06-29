namespace Scope.Core.Entitlement;

using System.Text.Json.Serialization;

/// <summary>
/// Source-generated System.Text.Json serializer context for entitlement types.
/// AOT-compatible; no reflection. Used by <see cref="EntitlementTokenVerifier"/>,
/// <see cref="EntitlementCache"/>, <see cref="EntitlementCredentialStore"/>,
/// and <see cref="ControlPlaneEntitlementRefresher"/>.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EntitlementToken))]
[JsonSerializable(typeof(EntitlementLimits))]
[JsonSerializable(typeof(EntitlementCacheEnvelope))]
[JsonSerializable(typeof(EntitlementRefreshCredential))]
[JsonSerializable(typeof(EntitlementRefreshState))]
[JsonSerializable(typeof(EntitlementValidateRequest))]
[JsonSerializable(typeof(EntitlementValidateResponse))]
public partial class EntitlementJsonContext : JsonSerializerContext;

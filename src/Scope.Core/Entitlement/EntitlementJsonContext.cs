namespace Scope.Core.Entitlement;

using System.Text.Json.Serialization;

/// <summary>
/// Source-generated System.Text.Json serializer context for entitlement types.
/// AOT-compatible; no reflection. Used by <see cref="EntitlementTokenVerifier"/> and
/// <see cref="EntitlementCache"/>.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EntitlementToken))]
[JsonSerializable(typeof(EntitlementLimits))]
[JsonSerializable(typeof(EntitlementCacheEnvelope))]
public partial class EntitlementJsonContext : JsonSerializerContext;

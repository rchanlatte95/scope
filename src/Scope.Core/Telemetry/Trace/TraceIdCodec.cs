namespace Scope.Core.Telemetry.Trace;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Encodes canonical observability ids into the scope-trace-v1 display id (7-char uppercase
/// BASE36 of the low 32 bits) and computes the SHA-256 surrogate for non-UUID session keys.
/// See design-scope-trace-v1.xml idScheme. This is a pure, deterministic projection over
/// stored data: no randomness, no render-order input.
/// </summary>
public static class TraceIdCodec
{
    private const string Base36Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Computes the 7-char uppercase BASE36 display id for a canonical id. UUID-shaped keys
    /// (32 hex chars after dash removal) encode the low 32 bits (last 8 hex chars) directly;
    /// other keys are hashed with SHA-256 and the first 4 bytes encoded. Always 7 chars.
    /// </summary>
    public static string DisplayId(string? canonicalId)
    {
        if (string.IsNullOrEmpty(canonicalId)) return "0000000";

        var normalized = canonicalId.Replace("-", "");
        uint value;
        if (normalized.Length == 32 && IsHex(normalized))
        {
            // Low 32 bits = last 8 hex chars, parsed big-endian.
            value = uint.Parse(normalized.AsSpan(normalized.Length - 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            // Surrogate: first 4 bytes of SHA-256(UTF-8 key), big-endian.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId));
            value = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        }

        return Encode(value);
    }

    /// <summary>
    /// Returns true when the key is a 32-hex-char UUID-shaped value (dashes allowed), the
    /// condition under which the raw key is packet-safe and no surrogate is needed.
    /// </summary>
    public static bool IsUuidShaped(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var normalized = key.Replace("-", "");
        return normalized.Length == 32 && IsHex(normalized);
    }

    /// <summary>
    /// Computes the full 64-hex SHA-256 surrogate id for a non-UUID session key, the packet
    /// canonical identifier that replaces the raw key (NEVER-list item 11). Returns null when
    /// the key is UUID-shaped (no surrogate needed).
    /// </summary>
    public static string? Surrogate(string? key)
    {
        if (string.IsNullOrEmpty(key) || IsUuidShaped(key)) return null;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// Maps a session id to its packet identifier: the raw key when UUID-shaped, otherwise the
    /// SHA-256 surrogate (NEVER-list item 11 forbids raw non-UUID keys in the packet tier).
    /// </summary>
    public static string? PacketSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        return IsUuidShaped(sessionId) ? sessionId : Surrogate(sessionId);
    }

    private static string Encode(uint value)
    {
        Span<char> buffer = stackalloc char[7];
        for (var i = 6; i >= 0; i--)
        {
            buffer[i] = Base36Alphabet[(int)(value % 36)];
            value /= 36;
        }
        return new string(buffer);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return false;
        }
        return true;
    }
}

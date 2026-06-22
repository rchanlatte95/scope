namespace Scope.Core.Telemetry;

using Scope.Core.Utilities;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// State file contract shared between the wire-metering proxy (writer) and the
/// scope CLI launcher (reader). Lives at .scope/proxy.json next to telemetry.db.
/// The token is local-machine, per-start state; the file never leaves the machine
/// (.scope/ is gitignored).
/// </summary>
public record ProxyState(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("started_at")] string StartedAt,
    [property: JsonPropertyName("telemetry_db")] string TelemetryDb)
{
    /// <summary>Base URL clients must use, including the per-start token path prefix.</summary>
    [JsonIgnore]
    public string BaseUrl => $"http://127.0.0.1:{Port}/t/{Token}";

    /// <summary>Serializes this state via the source-generated context.</summary>
    public string ToJsonString() =>
        JsonSerializer.Serialize(this, ScopeJsonSerializerContext.Default.ProxyState);

    /// <summary>Writes the state file, creating the parent directory when missing.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir.HasContent) Directory.CreateDirectory(dir!);
        File.WriteAllText(path, ToJsonString());
    }

    /// <summary>
    /// Reads a state file, returning null when missing or unparseable.
    /// </summary>
    public static ProxyState? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(
                File.ReadAllText(path), ScopeJsonSerializerContext.Default.ProxyState);
        }
        catch
        {
            return null;
        }
    }
}

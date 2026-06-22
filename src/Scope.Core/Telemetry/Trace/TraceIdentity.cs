namespace Scope.Core.Telemetry.Trace;

/// <summary>
/// Envelope identity placeholders (design identity section). machine_id is a locally-minted
/// random UUIDv7 stored under %APPDATA%/.scope/machine-id; workspace_id is stored at
/// .scope/workspace-id beside scope.db; org_id is always null in v1 local operation.
///
/// ST-12 is read-only: this helper READS the id files when present and returns null otherwise
/// (the contract explicitly allows null "until first minted"). Minting on write is a separate
/// concern owned by the index/proxy paths and is intentionally not performed here.
/// </summary>
public sealed record TraceIdentity(string? MachineId, string? WorkspaceId, string? OrgId)
{
    /// <summary>An all-null identity, used when no id files exist.</summary>
    public static TraceIdentity Empty { get; } = new(null, null, null);

    /// <summary>
    /// Resolves identity for a workspace given its .scope directory. Reads machine-id from the
    /// user app-data .scope root and workspace-id from the workspace .scope directory. Never throws;
    /// missing or unreadable files yield null. Values are validated as 32-hex UUID-shaped before use.
    /// </summary>
    public static TraceIdentity Resolve(string? scopeDir)
    {
        var machineId = ReadId(MachineIdPath());
        var workspaceId = scopeDir != null ? ReadId(Path.Combine(scopeDir, "workspace-id")) : null;
        return new TraceIdentity(machineId, workspaceId, OrgId: null);
    }

    /// <summary>The machine-id file path: %APPDATA%/.scope/machine-id (analogous user-profile path elsewhere).</summary>
    public static string MachineIdPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, ".scope", "machine-id");
    }

    /// <summary>
    /// Returns the stable machine id, minting and persisting it if the id file is absent or
    /// unreadable. Uses <see cref="Guid.CreateVersion7()"/> (the same UUIDv7 source the
    /// observability writer uses for span ids) for uniqueness and sortability. If the file cannot
    /// be written due to an IO error, a freshly-minted in-memory id is returned WITHOUT persisting
    /// so the caller always gets a non-null value. Never throws; observability must not crash callers.
    /// </summary>
    public static string EnsureMachineId()
    {
        try
        {
            var path = MachineIdPath();
            var existing = ReadId(path);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            // Mint a new UUIDv7 using the same source the writer uses for span ids.
            var minted = Guid.CreateVersion7().ToString("N");
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(dir);
                // Write atomically via a temp file in the same directory, then move.
                var tmp = path + ".tmp." + Guid.CreateVersion7().ToString("N");
                File.WriteAllText(tmp, minted);
                File.Move(tmp, path, overwrite: true);
                // Validate the persisted value matches what we wrote.
                var persisted = ReadId(path);
                return !string.IsNullOrEmpty(persisted) ? persisted : minted;
            }
            catch (IOException) { return minted; /* persist failed; return in-memory id */ }
            catch (UnauthorizedAccessException) { return minted; }
        }
        catch
        {
            // Absolute last resort: mint in-memory only. Observability never crashes callers.
            return Guid.CreateVersion7().ToString("N");
        }
    }

    private static string? ReadId(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path).Trim();
            return TraceIdCodec.IsUuidShaped(raw) ? raw.Replace("-", "") : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

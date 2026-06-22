namespace Scope.Core.Cron.Launch;

using System.Text;

/// <summary>
/// How a resolved executable must be launched. Win32 <c>CreateProcess</c> (used by
/// <see cref="System.Diagnostics.Process"/> with <c>UseShellExecute=false</c>) can only start
/// real executable images (.exe/.com); script shims such as the npm-installed <c>codex.cmd</c>
/// must be launched through <c>cmd.exe</c>.
/// </summary>
public enum ResolvedExecutableKind
{
    /// <summary>No matching file was found anywhere on PATH.</summary>
    NotFound,

    /// <summary>A directly-launchable executable image (.exe/.com, or any file on a non-Windows OS).</summary>
    DirectExecutable,

    /// <summary>A Windows command script shim (.cmd/.bat) that must be launched via cmd.exe.</summary>
    CommandScript,

    /// <summary>A file was found whose only forms cannot be launched by Scope (for example a .ps1-only install).</summary>
    Unlaunchable,
}

/// <summary>
/// A logical executable name resolved against PATH, describing how to launch it. The Codex adapter
/// uses this to detect and run npm <c>.cmd</c> shims on Windows while keeping that platform
/// knowledge out of the runner and API layers. The instance carries an honest diagnostic when the
/// name could not be resolved to a launchable form.
/// </summary>
public sealed record ResolvedExecutable
{
    private ResolvedExecutable(ResolvedExecutableKind kind, string? resolvedPath, string? diagnostic)
    {
        Kind = kind;
        ResolvedPath = resolvedPath;
        Diagnostic = diagnostic;
    }

    /// <summary>How (or whether) the resolved file can be launched.</summary>
    public ResolvedExecutableKind Kind { get; }

    /// <summary>Absolute path to the resolved file, or null when nothing launchable was found.</summary>
    public string? ResolvedPath { get; }

    /// <summary>Human-readable diagnostic for non-launchable outcomes; null when launchable.</summary>
    public string? Diagnostic { get; }

    /// <summary>True when the resolved name can be launched (directly or via cmd.exe).</summary>
    public bool IsLaunchable => Kind is ResolvedExecutableKind.DirectExecutable or ResolvedExecutableKind.CommandScript;

    internal static ResolvedExecutable Direct(string path)
        => new(ResolvedExecutableKind.DirectExecutable, path, null);

    internal static ResolvedExecutable Script(string path)
        => new(ResolvedExecutableKind.CommandScript, path, null);

    internal static ResolvedExecutable NotFoundResult(string diagnostic)
        => new(ResolvedExecutableKind.NotFound, null, diagnostic);

    internal static ResolvedExecutable UnlaunchableResult(string path, string diagnostic)
        => new(ResolvedExecutableKind.Unlaunchable, path, diagnostic);

    /// <summary>
    /// Produces the concrete <see cref="ProcessRunRequest.FileName"/> for this resolution. For a
    /// command-script shim on Windows this is <c>cmd.exe</c>; for a direct executable it is the
    /// resolved path itself. Throws if the executable is not launchable (callers must check
    /// <see cref="IsLaunchable"/> first).
    /// </summary>
    public string LaunchFileName => Kind switch
    {
        ResolvedExecutableKind.DirectExecutable => ResolvedPath!,
        ResolvedExecutableKind.CommandScript => CmdExePath(),
        _ => throw new InvalidOperationException($"Cannot launch a {Kind} executable."),
    };

    /// <summary>
    /// Characters that cmd.exe treats specially and that cannot be neutralized losslessly once a
    /// token reaches the <c>cmd.exe /c</c> command line. Quote-wrapping does NOT make a token safe:
    /// <list type="bullet">
    ///   <item>cmd.exe re-parses the command line for metacharacters (<c>&amp; | &lt; &gt; ^ ( )</c>)
    ///   based on quote state, but its quote-state machine is fragile, and the very wrapping we use
    ///   (<c>/c "&lt;cmd&gt;"</c>) begins with a doubled quote that can desync that machine, letting
    ///   later metacharacters execute. So we cannot rely on quoting to suppress them.</item>
    ///   <item><c>%VAR%</c> environment expansion happens during command-line parsing and there is NO
    ///   quote context in cmd.exe that suppresses it; <c>%</c> simply cannot be escaped inside an
    ///   argument that cmd.exe parses (the only escape, <c>%%</c>, works in batch files, not on the
    ///   <c>/c</c> line). A leaked <c>%CODEX_API_KEY%</c> would expand into argv.</item>
    ///   <item><c>!</c> triggers delayed expansion when it is enabled; we never enable it, but a host
    ///   AutoRun could, so we refuse it too.</item>
    ///   <item>Newlines / carriage returns can terminate or chain commands.</item>
    /// </list>
    /// Because neutralization is not achievable (especially for <c>%</c>), the only sound strategy is
    /// to REJECT any token bound for a <c>.cmd</c> shim that contains one of these characters. Plain
    /// paths and model ids - including paths with spaces - pass; injection vectors are refused before
    /// launch with an honest, actionable diagnostic.
    /// </summary>
    internal static readonly char[] CmdUnsafeCharacters =
        ['"', '&', '|', '<', '>', '^', '%', '!', '(', ')', '\r', '\n'];

    /// <summary>
    /// Builds the raw argument string that <see cref="SystemProcessRunner"/> passes to <c>cmd.exe</c>
    /// verbatim for a command-script shim, or null for a direct executable (which uses
    /// <see cref="ProcessRunRequest.Arguments"/> via <c>ArgumentList</c>). cmd.exe is invoked as
    /// <c>/d /s /c "&lt;shim&gt; &lt;args&gt;"</c>: <c>/d</c> skips AutoRun and <c>/s</c> strips the
    /// outer quote pair.
    /// <para>
    /// SECURITY: <c>cmd.exe</c> parsing cannot be made injection-safe by quoting alone - it re-parses
    /// for <c>&amp; | &lt; &gt; ^ ( )</c> metacharacters and unconditionally expands <c>%VAR%</c>
    /// regardless of quotes. Every field that reaches this command line (the resolved shim path, the
    /// repo path behind <c>--cd</c>, the artifact path behind <c>--output-last-message</c>, and the
    /// model id behind <c>-m</c>) is therefore validated here and the whole launch is REJECTED (via
    /// <see cref="CmdShimArgumentException"/>) if any token contains a cmd metacharacter. The caller
    /// turns that into an honest capability/failure diagnostic. Tokens with spaces but no
    /// metacharacters are still accepted and double-quoted so cmd.exe does not word-split them.
    /// </para>
    /// </summary>
    /// <param name="arguments">The logical argument list to pass to the underlying script.</param>
    /// <exception cref="CmdShimArgumentException">
    /// Thrown when the shim path or any argument contains a character unsafe for a <c>cmd.exe</c>
    /// command line, so no injection or env-var-expansion vector can ever reach the spawned shell.
    /// </exception>
    public string? BuildCmdRawArguments(IReadOnlyList<string> arguments)
    {
        if (Kind != ResolvedExecutableKind.CommandScript)
            return null;

        // The shim path itself comes from ExecutableResolver (a File.Exists-verified PATH entry), but a
        // PATH directory could still contain a cmd metacharacter; apply the same rejection rule to it.
        var shimPath = ResolvedPath!;
        RejectIfUnsafe(shimPath, isShimPath: true);

        var inner = new StringBuilder();
        inner.Append(QuoteForCmd(shimPath));
        foreach (var argument in arguments)
        {
            RejectIfUnsafe(argument, isShimPath: false);
            inner.Append(' ');
            inner.Append(QuoteForCmd(argument));
        }

        // Outer "" wrap the whole command so cmd /s strips exactly this pair. Every inner token has
        // already been proven free of metacharacters, so the desync-the-quote-machine attack is moot.
        return $"/d /s /c \"{inner}\"";
    }

    private static void RejectIfUnsafe(string token, bool isShimPath)
    {
        var badIndex = token.IndexOfAny(CmdUnsafeCharacters);
        if (badIndex < 0)
            return;

        var bad = token[badIndex] switch
        {
            '\r' => "carriage return",
            '\n' => "newline",
            var c => $"'{c}'",
        };

        var subject = isShimPath
            ? $"The resolved Codex shim path ('{token}')"
            : $"A Codex launch argument ('{token}')";

        throw new CmdShimArgumentException(
            $"{subject} contains the character {bad}, which is unsafe for the npm .cmd shim launched " +
            "through cmd.exe (cmd.exe cannot neutralize shell metacharacters or %VAR% expansion in an " +
            "argument). Install codex.exe, rename the path, or use a model id without shell " +
            "metacharacters (allowed: letters, digits, and . _ : / -).");
    }

    private static string CmdExePath()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrWhiteSpace(comspec) ? "cmd.exe" : comspec;
    }

    /// <summary>
    /// Wraps a single (already-validated, metacharacter-free) token in double quotes so cmd.exe does
    /// not word-split a path containing spaces. No escaping of inner characters is needed because
    /// <see cref="RejectIfUnsafe"/> has already refused any token containing a quote or other cmd
    /// metacharacter; this method is never reached with an unsafe token.
    /// </summary>
    private static string QuoteForCmd(string token) => $"\"{token}\"";
}

/// <summary>
/// Raised when a token bound for an npm <c>.cmd</c> shim launched through <c>cmd.exe</c> contains a
/// character that cmd.exe would interpret as a shell metacharacter or environment-variable expansion.
/// Such tokens are rejected before launch (rather than escaped, which is impossible to do losslessly
/// for <c>%VAR%</c>) so no command injection or secret-leaking expansion can reach the shell. The
/// message is an actionable, secret-free diagnostic safe to surface to the user.
/// </summary>
public sealed class CmdShimArgumentException(string message) : Exception(message);

/// <summary>
/// Resolves a logical executable name (for example <c>codex</c>) to a concrete file on PATH,
/// respecting Windows PATHEXT precedence and distinguishing a directly-launchable image from an
/// npm <c>.cmd</c> shim and from a found-but-unlaunchable install (such as a <c>.ps1</c>-only
/// shape). This host-independent utility keeps Windows shim knowledge in the process-runner seam
/// rather than the runner or API layers.
/// </summary>
public static class ExecutableResolver
{
    // Extensions Win32 CreateProcess can start as an image without a shell wrapper.
    private static readonly string[] DirectExecutableExtensions = [".exe", ".com"];

    // Extensions that are launchable only through cmd.exe.
    private static readonly string[] CommandScriptExtensions = [".cmd", ".bat"];

    /// <summary>
    /// Resolves <paramref name="logicalName"/> against the given PATH and PATHEXT. On non-Windows
    /// platforms an absolute or PATH-resident file is returned as a direct executable. On Windows,
    /// candidates are tried in PATHEXT order with directly-launchable images (.exe/.com) preferred
    /// over command-script shims (.cmd/.bat); a name that resolves only to a non-launchable form
    /// (for example .ps1) is reported as <see cref="ResolvedExecutableKind.Unlaunchable"/> with an
    /// honest diagnostic, and a name found nowhere is reported as
    /// <see cref="ResolvedExecutableKind.NotFound"/>.
    /// </summary>
    /// <param name="logicalName">The bare executable name (no extension), for example <c>codex</c>.</param>
    /// <param name="pathValue">The PATH variable value (directories joined by the OS separator). Null reads the process PATH.</param>
    /// <param name="pathExtValue">The PATHEXT variable value (Windows only). Null reads the process PATHEXT.</param>
    public static ResolvedExecutable Resolve(string logicalName, string? pathValue = null, string? pathExtValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var isWindows = OperatingSystem.IsWindows();
        var path = pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!isWindows)
            return ResolvePosix(logicalName, directories);

        var pathExt = pathExtValue ?? Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty;
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Track the best launchable hit and any non-launchable hit so we can give an honest
        // "found-but-unlaunchable" diagnostic when no launchable form exists.
        string? scriptHit = null;
        string? unlaunchableHit = null;
        string? unlaunchableExt = null;

        foreach (var directory in directories)
        {
            foreach (var rawExt in extensions)
            {
                // PATHEXT entries are conventionally uppercase (.EXE); normalize to lowercase so the
                // resolved path and diagnostics read naturally. Windows file matching is case-insensitive.
                var ext = rawExt.ToLowerInvariant();
                var candidate = Path.Combine(directory, logicalName + ext);
                if (!File.Exists(candidate))
                    continue;

                if (MatchesAny(ext, DirectExecutableExtensions))
                    return ResolvedExecutable.Direct(candidate); // .exe/.com win outright (PATHEXT places .EXE before .CMD).

                if (MatchesAny(ext, CommandScriptExtensions))
                {
                    // Remember the first .cmd/.bat but keep scanning in case an .exe appears later
                    // in a subsequent directory; PATHEXT precedence already favors .exe within a dir.
                    scriptHit ??= candidate;
                    continue;
                }

                unlaunchableHit ??= candidate;
                unlaunchableExt ??= ext;
            }
        }

        if (scriptHit is not null)
            return ResolvedExecutable.Script(scriptHit);

        if (unlaunchableHit is not null)
        {
            return ResolvedExecutable.UnlaunchableResult(
                unlaunchableHit,
                $"'{logicalName}' was found on PATH only as '{Path.GetFileName(unlaunchableHit)}' " +
                $"({unlaunchableExt}), which Scope cannot launch. Install a '.cmd' shim or a '.exe' " +
                "(the standard npm install provides a .cmd) to enable this harness.");
        }

        return ResolvedExecutable.NotFoundResult(
            $"'{logicalName}' was not found on PATH (no {logicalName}.exe or {logicalName}.cmd).");
    }

    private static ResolvedExecutable ResolvePosix(string logicalName, IReadOnlyList<string> directories)
    {
        if (Path.IsPathRooted(logicalName) && File.Exists(logicalName))
            return ResolvedExecutable.Direct(logicalName);

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, logicalName);
            if (File.Exists(candidate))
                return ResolvedExecutable.Direct(candidate);
        }

        return ResolvedExecutable.NotFoundResult($"'{logicalName}' was not found on PATH.");
    }

    private static bool MatchesAny(string ext, string[] set)
    {
        foreach (var candidate in set)
        {
            if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

namespace Scope.DevTools.EntitlementIssuer;

using System.Globalization;

/// <summary>
/// Thin CLI argument parser + runner for the dev-only entitlement issuer. Kept dependency-free
/// (no System.CommandLine reference) since this tool exists purely for local/offline use.
///
/// <see cref="Run"/> is the single entry point Program.Main calls; it is also called directly
/// (in-process, no subprocess) by the end-to-end dry-run test
/// (test/Scope.Tests/Entitlement/EntitlementDryRunIssuanceEndToEndTests.cs) so the test exercises
/// the exact same code path a developer invokes from the command line.
/// </summary>
public static class CliRunner
{
    private const string Usage =
        """
        DEV-ONLY entitlement token issuer. Signs a token envelope with a runtime-supplied
        private key file. Never use a key committed to this repo; never commit the output
        of --private-key-file.

        Required:
          --private-key-file <path>       Base64 PKCS8 ECDSA P-256 private key file (outside the repo)

        Optional claims:
          --tier <free|team|enterprise>   Default: team
          --org-id <id>                   Default: dev-issued
          --seat-id <id>                  Default: dev-seat
          --expires-at-utc <iso8601>      Explicit expiry (overrides --expires-in-days)
          --expires-in-days <days>        Default: 30 (ignored if --expires-at-utc is set)
          --trial-ends-at-utc <iso8601>   Omit for a non-trial token
          --grace-hours <hours>           Omit to use the global 72h default
          --max-enabled-crons <n>         Omit for unlimited
          --observability-retention-days <n>  Omit for unlimited
          --max-dashboard-sessions <n>    Omit for unlimited
          --audit-export-allowed <true|false>  Default: true

        Output:
          --out <path>                    Write the envelope to a file instead of stdout
        """;

    /// <summary>
    /// Parses <paramref name="args"/>, issues a token, and writes the envelope to
    /// <paramref name="stdout"/> (or the file named by --out). Returns a process exit code.
    /// Never throws for user-input errors: those are reported on <paramref name="stderr"/>
    /// with a non-zero exit code.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            stderr.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        Dictionary<string, string> flags;
        try
        {
            flags = ParseFlags(args);
        }
        catch (FormatException ex)
        {
            stderr.WriteLine("Argument error: " + ex.Message);
            stderr.WriteLine(Usage);
            return 2;
        }

        if (!flags.TryGetValue("--private-key-file", out var privateKeyFile) || string.IsNullOrWhiteSpace(privateKeyFile))
        {
            stderr.WriteLine("Argument error: --private-key-file is required.");
            stderr.WriteLine(Usage);
            return 2;
        }

        EntitlementIssueOptions options;
        try
        {
            options = BuildOptions(privateKeyFile, flags);
        }
        catch (FormatException ex)
        {
            stderr.WriteLine("Argument error: " + ex.Message);
            return 2;
        }

        stderr.WriteLine(
            "DEV-ONLY TOOL: signing with a caller-supplied key. This does NOT grant entitlement " +
            "against the pinned production key set (EntitlementTokenVerifier.ProductionPublicKeysBase64) " +
            "unless --private-key-file is the real control-plane private key. Never commit key material.");

        string envelope;
        try
        {
            envelope = TokenIssuer.Issue(options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                         or System.Security.Cryptography.CryptographicException)
        {
            stderr.WriteLine("Issuance failed: " + ex.Message);
            return 1;
        }

        if (flags.TryGetValue("--out", out var outPath) && !string.IsNullOrWhiteSpace(outPath))
        {
            File.WriteAllText(outPath, envelope);
            stderr.WriteLine("Envelope written to: " + outPath);
        }
        else
        {
            stdout.WriteLine(envelope);
        }

        return 0;
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < args.Length)
        {
            var flag = args[i];
            if (!flag.StartsWith("--", StringComparison.Ordinal))
                throw new FormatException($"Unexpected token '{flag}'; expected a --flag.");
            if (i + 1 >= args.Length)
                throw new FormatException($"Flag '{flag}' requires a value.");
            flags[flag] = args[i + 1];
            i += 2;
        }
        return flags;
    }

    private static EntitlementIssueOptions BuildOptions(string privateKeyFile, Dictionary<string, string> flags)
    {
        var options = new EntitlementIssueOptions { PrivateKeyFilePath = privateKeyFile };

        if (flags.TryGetValue("--tier", out var tier) && !string.IsNullOrWhiteSpace(tier))
            options = options with { Tier = tier };

        if (flags.TryGetValue("--org-id", out var orgId))
            options = options with { OrgId = orgId };

        if (flags.TryGetValue("--seat-id", out var seatId))
            options = options with { SeatId = seatId };

        if (flags.TryGetValue("--expires-at-utc", out var expiresAtRaw))
            options = options with { ExpiresAtUtc = ParseUtc(expiresAtRaw, "--expires-at-utc") };

        if (flags.TryGetValue("--expires-in-days", out var expiresInDaysRaw))
        {
            if (!double.TryParse(expiresInDaysRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var days))
                throw new FormatException($"--expires-in-days value '{expiresInDaysRaw}' is not a number.");
            options = options with { ExpiresInDays = days };
        }

        if (flags.TryGetValue("--trial-ends-at-utc", out var trialRaw))
            options = options with { TrialEndsAtUtc = ParseUtc(trialRaw, "--trial-ends-at-utc") };

        if (flags.TryGetValue("--grace-hours", out var graceRaw))
            options = options with { GraceHours = ParseInt(graceRaw, "--grace-hours") };

        if (flags.TryGetValue("--max-enabled-crons", out var maxCronsRaw))
            options = options with { MaxEnabledCrons = ParseInt(maxCronsRaw, "--max-enabled-crons") };

        if (flags.TryGetValue("--observability-retention-days", out var retentionRaw))
            options = options with { ObservabilityRetentionDays = ParseInt(retentionRaw, "--observability-retention-days") };

        if (flags.TryGetValue("--max-dashboard-sessions", out var maxSessionsRaw))
            options = options with { MaxDashboardSessions = ParseInt(maxSessionsRaw, "--max-dashboard-sessions") };

        if (flags.TryGetValue("--audit-export-allowed", out var auditRaw))
        {
            if (!bool.TryParse(auditRaw, out var audit))
                throw new FormatException($"--audit-export-allowed value '{auditRaw}' is not true/false.");
            options = options with { AuditExportAllowed = audit };
        }

        return options;
    }

    private static DateTime ParseUtc(string raw, string flagName)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, styles, out var value))
            throw new FormatException($"{flagName} value '{raw}' is not a valid ISO-8601 timestamp.");
        return value;
    }

    private static int ParseInt(string raw, string flagName)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"{flagName} value '{raw}' is not an integer.");
        return value;
    }
}

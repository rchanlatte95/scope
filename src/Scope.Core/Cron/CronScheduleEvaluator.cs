namespace Scope.Core.Cron;

using System.Globalization;

/// <summary>
/// Computes the next UTC fire time for a validated 5-field cron expression or @hourly/@daily sugar.
/// The evaluator is pure and deterministic; all time input is injected (no wall-clock access here).
///
/// Supported field grammar (identical to what <see cref="CronScheduleValidator"/> accepts):
///   minute      0-59
///   hour        0-23
///   day-of-month (dom) 1-31
///   month       1-12
///   day-of-week (dow) 0-7 (both 0 and 7 mean Sunday)
///
/// Per-field syntax:
///   *           any value
///   N           literal value
///   N-M         inclusive range
///   A,B[,C...]  comma-separated list of any of the above
///   */N or B-E/N step — every Nth value in the base range (* or B-E)
///
/// Day-of-month/day-of-week OR semantics (standard cron):
///   When BOTH dom and dow are unrestricted (*), a day matches only when its date satisfies the
///   month field. When BOTH dom and dow are restricted (not *), a day matches when it satisfies
///   EITHER the dom condition OR the dow condition (OR semantics, not AND). When only one of dom
///   or dow is restricted, only that restriction applies.
///
/// The evaluator advances one minute past the supplied <paramref name="from"/> instant
/// (no backfill: the "next" fire is strictly after now), then scans minute-by-minute up to
/// an 8-year lookahead. Eight years covers the worst-case legal gap, which is "Feb 29" across a
/// non-leap century boundary (e.g. 2096 -> 2104, since 2100 is not a leap year); a 4-year ceiling
/// would spuriously return null for that schedule near 2100.
/// </summary>
public static class CronScheduleEvaluator
{
    private const int MaxScanMinutes = 366 * 8 * 24 * 60; // 8-year ceiling (covers the non-leap century Feb-29 gap)

    // Pre-expanded field ranges for quick reference.
    private static readonly (int Min, int Max)[] FieldRanges =
    [
        (0, 59),  // minute
        (0, 23),  // hour
        (1, 31),  // dom
        (1, 12),  // month
        (0, 6),   // dow (internally 0=Sun, 6=Sat; validator allows 0-7, 7 is normalised to 0)
    ];

    /// <summary>
    /// Returns the next UTC fire time for <paramref name="schedule"/> strictly after
    /// <paramref name="from"/>, at minute resolution (seconds/sub-second are stripped).
    /// Returns <see langword="null"/> when no fire time exists within the 8-year lookahead.
    /// </summary>
    /// <param name="schedule">A validated schedule string: "MIN HR DOM MON DOW" or @hourly/@daily.</param>
    /// <param name="from">The UTC instant to start searching from (exclusive).</param>
    public static DateTime? NextFireAfter(string schedule, DateTime from)
    {
        // Normalise the from instant to minute resolution (strip seconds/sub-second).
        var baseMinute = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, DateTimeKind.Utc);

        // Expand sugar before parsing.
        var canonical = ExpandSugar(schedule);

        var fields = canonical.Split(' ');
        var minuteSet = ExpandField(fields[0], FieldRanges[0].Min, FieldRanges[0].Max);
        var hourSet = ExpandField(fields[1], FieldRanges[1].Min, FieldRanges[1].Max);
        var domSet = ExpandField(fields[2], FieldRanges[2].Min, FieldRanges[2].Max);
        var monthSet = ExpandField(fields[3], FieldRanges[3].Min, FieldRanges[3].Max);
        var dowSet = NormaliseDow(ExpandField(fields[4], 0, 7));

        // dom-restricted vs dow-restricted flags for OR semantics.
        bool domRestricted = !(string.Equals(fields[2], "*", StringComparison.Ordinal));
        bool dowRestricted = !(string.Equals(fields[4], "*", StringComparison.Ordinal));

        // Scan strictly after the from instant.
        var candidate = baseMinute.AddMinutes(1);
        var deadline = baseMinute.AddMinutes(MaxScanMinutes);

        while (candidate <= deadline)
        {
            if (!monthSet.Contains(candidate.Month))
            {
                // Skip to the first day of the next matching month.
                candidate = AdvanceToNextMonth(candidate, monthSet);
                continue;
            }

            int dow = (int)candidate.DayOfWeek; // DayOfWeek: 0=Sun, 6=Sat

            bool domMatch = domSet.Contains(candidate.Day);
            bool dowMatch = dowSet.Contains(dow);

            bool dayMatch;
            if (domRestricted && dowRestricted)
                dayMatch = domMatch || dowMatch;   // OR semantics
            else if (domRestricted)
                dayMatch = domMatch;
            else if (dowRestricted)
                dayMatch = dowMatch;
            else
                dayMatch = true; // both are unrestricted (*)

            if (!dayMatch)
            {
                // Advance to midnight of the next day.
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!hourSet.Contains(candidate.Hour))
            {
                // Advance to the next hour boundary.
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day,
                    candidate.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
                continue;
            }

            if (!minuteSet.Contains(candidate.Minute))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return candidate;
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    /// <summary>Translates sugar to a canonical 5-field expression before parsing.</summary>
    private static string ExpandSugar(string schedule)
        => schedule.Trim() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" => "0 0 * * *",
            var s => s,
        };

    /// <summary>
    /// Advances <paramref name="current"/> to the first minute of the first day of the next month
    /// that is in <paramref name="monthSet"/>.
    /// </summary>
    private static DateTime AdvanceToNextMonth(DateTime current, HashSet<int> monthSet)
    {
        var year = current.Year;
        var month = current.Month + 1;

        for (var attempts = 0; attempts < 13; attempts++, month++)
        {
            if (month > 12) { month = 1; year++; }
            if (monthSet.Contains(month))
                return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        // Should never happen for a non-empty month set, but return far future to stop scan.
        return DateTime.MaxValue;
    }

    /// <summary>
    /// Normalises a dow set so that 7 (Sunday alias) becomes 0, keeping only values 0-6.
    /// </summary>
    private static HashSet<int> NormaliseDow(HashSet<int> raw)
    {
        var result = new HashSet<int>();
        foreach (var v in raw)
            result.Add(v == 7 ? 0 : v);
        return result;
    }

    /// <summary>
    /// Expands a single cron field string into the set of integer values it matches.
    /// Handles: *, N, N-M, A,B, */N, N-M/N (and comma-separated combinations).
    /// </summary>
    internal static HashSet<int> ExpandField(string field, int min, int max)
    {
        var result = new HashSet<int>();
        foreach (var part in field.Split(','))
            ExpandPart(part.Trim(), min, max, result);
        return result;
    }

    private static void ExpandPart(string part, int min, int max, HashSet<int> target)
    {
        // Split on '/' to detect step.
        var slashIdx = part.IndexOf('/');
        int step = 1;
        string basePart = part;

        if (slashIdx >= 0)
        {
            step = int.Parse(part[(slashIdx + 1)..], CultureInfo.InvariantCulture);
            basePart = part[..slashIdx];
        }

        int rangeStart, rangeEnd;
        if (string.Equals(basePart, "*", StringComparison.Ordinal))
        {
            rangeStart = min;
            rangeEnd = max;
        }
        else
        {
            var dashIdx = basePart.IndexOf('-');
            if (dashIdx > 0)
            {
                rangeStart = int.Parse(basePart[..dashIdx], CultureInfo.InvariantCulture);
                rangeEnd = int.Parse(basePart[(dashIdx + 1)..], CultureInfo.InvariantCulture);
            }
            else
            {
                rangeStart = int.Parse(basePart, CultureInfo.InvariantCulture);
                rangeEnd = slashIdx >= 0 ? max : rangeStart; // "N/step" means start at N, run to max
            }
        }

        for (var v = rangeStart; v <= rangeEnd; v += step)
            target.Add(v);
    }
}

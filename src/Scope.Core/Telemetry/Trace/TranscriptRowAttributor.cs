namespace Scope.Core.Telemetry.Trace;

using System.Globalization;

/// <summary>
/// Pure, deterministic attributor that partitions transcript wire rows among the sessions of one
/// conversation. No DB, no clock, no randomness — referentially transparent.
/// </summary>
/// <remarks>
/// Contract: internal design notes, signature#attributor.
/// </remarks>
public static class TranscriptRowAttributor
{
    /// <summary>
    /// Assigns each transcript wire row to exactly one session, or places it in the remainder.
    /// </summary>
    /// <param name="rows">
    /// All transcript-source wire rows for a single conversation, supplied in transcript order
    /// (i.e. ascending <c>started_at</c>).
    /// </param>
    /// <param name="toolUseOwners">
    /// Map from <c>tool_use_id</c> to the session that executed it (machine-scoped).
    /// </param>
    /// <param name="windows">
    /// One <see cref="SessionWindow"/> per session in the conversation, giving the bounded
    /// temporal extent on a specific machine.
    /// </param>
    /// <returns>
    /// A <see cref="TranscriptOwnerAssignment"/> whose <c>OwnerByWireRowId</c> and
    /// <c>RemainderRowIds</c> form a total partition over all input <paramref name="rows"/>.
    /// </returns>
    public static TranscriptOwnerAssignment Assign(
        IReadOnlyList<TranscriptRow> rows,
        IReadOnlyDictionary<string, ToolUseOwner> toolUseOwners,
        IReadOnlyList<SessionWindow> windows)
    {
        // --- Phase 1: Tier-1 pass over every row ---
        // For each row collect the DISTINCT owner SessionKeys across its ToolUseIds where the
        // owner's MachineId matches the row's MachineId. Machine-scope is checked here so that
        // the same tool_use_id on a different machine is never cross-attributed (case M4).

        var owner = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);   // WireRowId -> SessionKey
        var remainder = new List<string>();
        var anomalies = new List<string>();

        // Track which indices resolved via Tier-1 (true = Tier-1 owned or Tier-1 ambiguous).
        // Tier-1 ambiguous rows are remainder but not eligible for Tier-2 bracketing.
        var tier1Resolved = new bool[rows.Count]; // default false

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.ToolUseIds.Count == 0)
            {
                // No tool ids -> cannot do Tier-1; leave for Tier-2.
                continue;
            }

            // Collect distinct owner SessionKeys that are machine-matched.
            var sessionKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in row.ToolUseIds)
            {
                if (toolUseOwners.TryGetValue(id, out var o) &&
                    string.Equals(o.MachineId, row.MachineId, StringComparison.Ordinal))
                {
                    sessionKeys.Add(o.SessionKey);
                }
            }

            tier1Resolved[i] = true; // row had tool ids; regardless of outcome, it settles here

            if (sessionKeys.Count == 1)
            {
                // Exactly one session -> owned.
                owner[row.WireRowId] = sessionKeys.Single();
            }
            else if (sessionKeys.Count >= 2)
            {
                // Ambiguous: multiple sessions claimed this turn -> remainder + anomaly.
                remainder.Add(row.WireRowId);
                anomalies.Add(
                    $"Row {row.WireRowId}: tool_use_ids resolve to {sessionKeys.Count} distinct sessions " +
                    $"({string.Join(", ", sessionKeys.OrderBy(s => s, StringComparer.Ordinal))}) — ambiguous, sent to remainder.");
            }
            else
            {
                // All tool ids either missing from the map or on a different machine.
                // Treat as "no Tier-1 resolution": do NOT mark tier1Resolved so we can fall to Tier-2.
                tier1Resolved[i] = false;
            }
        }

        // --- Phase 2: Tier-2 pass over rows that had no Tier-1 resolution ---
        // These are: rows with empty ToolUseIds, and rows whose ToolUseIds all resolved to a
        // different machine (or were absent from the map entirely).

        // Pre-parse all session window timestamps once.
        // Key: SessionKey -> (MachineId, Start, End). Skip windows with unparseable timestamps.
        var parsedWindows = new List<(string SessionKey, string MachineId, DateTimeOffset Start, DateTimeOffset End)>(windows.Count);
        foreach (var w in windows)
        {
            if (TryParseIso(w.StartIso, out var start) && TryParseIso(w.EndIso, out var end))
            {
                parsedWindows.Add((w.SessionKey, w.MachineId, start, end));
            }
            // Window with unparseable timestamps is silently skipped (cannot bracket anything).
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (tier1Resolved[i] || owner.ContainsKey(rows[i].WireRowId))
            {
                // Already settled by Tier-1 (either owned or Tier-1 ambiguous remainder).
                continue;
            }

            var row = rows[i];

            // Attempt to parse this row's timestamp.
            if (!TryParseIso(row.StartedAtIso, out var rowTime))
            {
                // Unparseable timestamp -> remainder.
                remainder.Add(row.WireRowId);
                continue;
            }

            // Collect windows on the SAME machine where Start <= rowTime <= End.
            // Never compare windows across different machine_ids (cross-machine non-overlap rule).
            var matchingWindows = new List<(string SessionKey, string MachineId, DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var pw in parsedWindows)
            {
                if (!string.Equals(pw.MachineId, row.MachineId, StringComparison.Ordinal))
                {
                    continue; // different machine — skip
                }
                if (pw.Start <= rowTime && rowTime <= pw.End)
                {
                    matchingWindows.Add(pw);
                }
            }

            if (matchingWindows.Count != 1)
            {
                // Zero matches (outside every window) or 2+ overlapping windows -> remainder.
                remainder.Add(row.WireRowId);
                continue;
            }

            var candidateSession = matchingWindows[0].SessionKey;

            // Bracketing check: find the nearest Tier-1-owned predecessor and successor in list
            // order that are themselves Tier-1-resolved (i.e. not in this Tier-2 pass).
            // Both must agree on candidateSession; otherwise -> remainder.
            string? predecessorSession = null;
            string? successorSession = null;

            // Scan backward for nearest Tier-1 owned predecessor.
            for (var j = i - 1; j >= 0; j--)
            {
                var prevRow = rows[j];
                if (tier1Resolved[j] && owner.TryGetValue(prevRow.WireRowId, out var prevSession))
                {
                    predecessorSession = prevSession;
                    break;
                }
            }

            // Scan forward for nearest Tier-1 owned successor.
            for (var j = i + 1; j < rows.Count; j++)
            {
                var nextRow = rows[j];
                if (tier1Resolved[j] && owner.TryGetValue(nextRow.WireRowId, out var nextSession))
                {
                    successorSession = nextSession;
                    break;
                }
            }

            // Bracketing rule: both must agree with the candidate, or both must be absent.
            // "Agree" means the non-null neighbor(s) point to the same session as the candidate.
            var predecessorAgrees = predecessorSession is null ||
                                    string.Equals(predecessorSession, candidateSession, StringComparison.Ordinal);
            var successorAgrees = successorSession is null ||
                                  string.Equals(successorSession, candidateSession, StringComparison.Ordinal);

            // We need at least one Tier-1 neighbor to exist and agree; if there are NO Tier-1
            // neighbors at all, we cannot confirm via bracketing -> remainder.
            // (If both are null it means there are no Tier-1 owned rows at all in the list, so
            //  there is no bracket to speak of.)
            var hasBracket = predecessorSession is not null || successorSession is not null;

            if (hasBracket && predecessorAgrees && successorAgrees)
            {
                owner[row.WireRowId] = candidateSession;
            }
            else
            {
                remainder.Add(row.WireRowId);
            }
        }

        // --- Phase 3: Assert the partition invariant ---
        // OwnerByWireRowId.Keys ∩ RemainderRowIds == ∅
        // OwnerByWireRowId.Keys ∪ RemainderRowIds == { all input WireRowIds }
        var allInputIds = new HashSet<string>(rows.Select(r => r.WireRowId), StringComparer.Ordinal);
        var ownedIds = new HashSet<string>(owner.Keys, StringComparer.Ordinal);
        var remainderSet = new HashSet<string>(remainder, StringComparer.Ordinal);

        // Disjoint check
        System.Diagnostics.Debug.Assert(
            !ownedIds.Overlaps(remainderSet),
            "PARTITION INVARIANT VIOLATED: a WireRowId appears in both OwnerByWireRowId and RemainderRowIds.");

        // Exhaustive check
        var union = new HashSet<string>(ownedIds, StringComparer.Ordinal);
        union.UnionWith(remainderSet);
        System.Diagnostics.Debug.Assert(
            union.SetEquals(allInputIds),
            "PARTITION INVARIANT VIOLATED: union of owned and remainder does not equal all input WireRowIds.");

        return new TranscriptOwnerAssignment(
            OwnerByWireRowId: owner,
            RemainderRowIds: remainder,
            Anomalies: anomalies);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static bool TryParseIso(string? s, out DateTimeOffset result)
    {
        if (s is not null &&
            DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out result))
        {
            return true;
        }
        result = default;
        return false;
    }
}

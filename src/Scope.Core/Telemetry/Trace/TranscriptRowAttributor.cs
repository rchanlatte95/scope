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
    /// (i.e. ascending <c>started_at</c>). Rows are deduplicated by <c>WireRowId</c> on entry,
    /// first-occurrence-wins: transcript rows are PK-unique at the DB level, so this is defensive
    /// totality for the pure function (it guarantees the output partition is well-defined even if a
    /// caller passes a duplicate id).
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
    /// <c>RemainderRowIds</c> form a total partition over the DISTINCT input <paramref name="rows"/>.
    /// The partition is disjoint and exhaustive BY CONSTRUCTION: remainder is built as exactly the
    /// distinct input WireRowIds that are not owner keys.
    /// </returns>
    public static TranscriptOwnerAssignment Assign(
        IReadOnlyList<TranscriptRow> rows,
        IReadOnlyDictionary<string, ToolUseOwner> toolUseOwners,
        IReadOnlyList<SessionWindow> windows)
    {
        // --- Phase 0: defensive dedup by WireRowId, first-occurrence-wins ---
        // Transcript rows are PK-unique at the DB level; this keeps the pure function total even if a
        // caller supplies a duplicate id (which would otherwise let one id land in two buckets and
        // break disjointness, or appear twice in remainder and double-count downstream).
        var seen = new HashSet<string>(rows.Count, StringComparer.Ordinal);
        var distinctRows = new List<TranscriptRow>(rows.Count);
        foreach (var r in rows)
        {
            if (seen.Add(r.WireRowId))
            {
                distinctRows.Add(r);
            }
        }

        // --- Phase 1: Tier-1 pass over every distinct row ---
        // For each row collect the DISTINCT owner SessionKeys across its ToolUseIds where the
        // owner's MachineId matches the row's MachineId. Machine-scope is checked here so that
        // the same tool_use_id on a different machine is never cross-attributed (case M4).

        var owner = new Dictionary<string, string>(distinctRows.Count, StringComparer.Ordinal); // WireRowId -> SessionKey
        var anomalies = new List<string>();

        // Tier-1 classification per index:
        //   Owned       = Tier-1 resolved to exactly one session (recorded in owner dict).
        //   Ambiguous   = Tier-1 saw 2+ sessions (remainder; NOT a Tier-2 candidate, NOT a bracket neighbor).
        //   (neither)   = no Tier-1 resolution (empty ids, or all ids missing / wrong machine) -> Tier-2.
        var isTier1Owned = new bool[distinctRows.Count];
        var isTier1Ambiguous = new bool[distinctRows.Count];

        for (var i = 0; i < distinctRows.Count; i++)
        {
            var row = distinctRows[i];
            if (row.ToolUseIds.Count == 0)
            {
                continue; // no tool ids -> Tier-2 candidate
            }

            var sessionKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in row.ToolUseIds)
            {
                if (toolUseOwners.TryGetValue(id, out var o) &&
                    string.Equals(o.MachineId, row.MachineId, StringComparison.Ordinal))
                {
                    sessionKeys.Add(o.SessionKey);
                }
            }

            if (sessionKeys.Count == 1)
            {
                owner[row.WireRowId] = sessionKeys.Single();
                isTier1Owned[i] = true;
            }
            else if (sessionKeys.Count >= 2)
            {
                isTier1Ambiguous[i] = true;
                anomalies.Add(
                    $"Row {row.WireRowId}: tool_use_ids resolve to {sessionKeys.Count} distinct sessions " +
                    $"({string.Join(", ", sessionKeys.OrderBy(s => s, StringComparer.Ordinal))}) — ambiguous, sent to remainder.");
            }
            // else sessionKeys.Count == 0: ids present but none machine-matched -> Tier-2 candidate.
        }

        // --- Phase 2: Tier-2 pass over rows with no Tier-1 resolution ---
        // Candidates are: empty-id rows, and rows whose ids all missed (absent / wrong machine).
        // Ambiguous Tier-1 rows are NOT candidates (they already belong to remainder).

        // Pre-parse all session window timestamps once. Skip windows with unparseable timestamps.
        var parsedWindows = new List<(string SessionKey, string MachineId, DateTimeOffset Start, DateTimeOffset End)>(windows.Count);
        foreach (var w in windows)
        {
            if (TryParseIso(w.StartIso, out var start) && TryParseIso(w.EndIso, out var end))
            {
                parsedWindows.Add((w.SessionKey, w.MachineId, start, end));
            }
        }

        for (var i = 0; i < distinctRows.Count; i++)
        {
            if (isTier1Owned[i] || isTier1Ambiguous[i])
            {
                continue; // already settled by Tier-1
            }

            var row = distinctRows[i];

            if (!TryParseIso(row.StartedAtIso, out var rowTime))
            {
                continue; // unparseable timestamp -> falls through to remainder (not an owner)
            }

            // Collect same-machine windows containing rowTime (closed interval). Never cross machines.
            var matchingWindows = new List<string>();
            foreach (var pw in parsedWindows)
            {
                if (!string.Equals(pw.MachineId, row.MachineId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (pw.Start <= rowTime && rowTime <= pw.End)
                {
                    matchingWindows.Add(pw.SessionKey);
                }
            }

            if (matchingWindows.Count == 0)
            {
                continue; // |W| == 0: gap between sessions -> remainder
            }

            if (matchingWindows.Count == 1)
            {
                // |W| == 1: time alone disambiguates. Own it — INCLUDING leading/trailing/isolated
                // text turns (a Tier-1 neighbor on only one side, or none). No two-sided bracketing.
                owner[row.WireRowId] = matchingWindows[0];
                continue;
            }

            // |W| >= 2: same-machine OVERLAP (concurrent resume) = ambiguous.
            // Own ONLY IF the nearest Tier-1-owned predecessor AND successor both exist and AGREE
            // on a single session that is in W; otherwise remainder. Bracketing is the overlap
            // tiebreaker, NOT a general requirement.
            var predecessorSession = NearestTier1OwnedNeighbor(distinctRows, owner, isTier1Owned, i, forward: false);
            var successorSession = NearestTier1OwnedNeighbor(distinctRows, owner, isTier1Owned, i, forward: true);

            if (predecessorSession is not null &&
                successorSession is not null &&
                string.Equals(predecessorSession, successorSession, StringComparison.Ordinal) &&
                matchingWindows.Contains(predecessorSession, StringComparer.Ordinal))
            {
                owner[row.WireRowId] = predecessorSession;
            }
            // else: one-sided, disagreeing, or agreed-session not in W -> remainder.
        }

        // --- Phase 3: build the partition STRUCTURALLY ---
        // RemainderRowIds = distinct input WireRowIds NOT present as an owner key. This makes the
        // partition disjoint + exhaustive by construction (no row can be in both buckets, and every
        // distinct input row is in exactly one). No Debug.Assert is load-bearing here.
        var remainder = new List<string>(distinctRows.Count);
        foreach (var r in distinctRows)
        {
            if (!owner.ContainsKey(r.WireRowId))
            {
                remainder.Add(r.WireRowId);
            }
        }

        return new TranscriptOwnerAssignment(
            OwnerByWireRowId: owner,
            RemainderRowIds: remainder,
            Anomalies: anomalies);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns the SessionKey of the nearest Tier-1-OWNED row in transcript (list) order, scanning
    /// outward from <paramref name="index"/> in the given direction; null if none exists. Only
    /// Tier-1-owned rows count as neighbors (ambiguous or Tier-2 rows are skipped).
    /// </summary>
    private static string? NearestTier1OwnedNeighbor(
        List<TranscriptRow> rows,
        Dictionary<string, string> owner,
        bool[] isTier1Owned,
        int index,
        bool forward)
    {
        if (forward)
        {
            for (var j = index + 1; j < rows.Count; j++)
            {
                if (isTier1Owned[j] && owner.TryGetValue(rows[j].WireRowId, out var session))
                {
                    return session;
                }
            }
        }
        else
        {
            for (var j = index - 1; j >= 0; j--)
            {
                if (isTier1Owned[j] && owner.TryGetValue(rows[j].WireRowId, out var session))
                {
                    return session;
                }
            }
        }

        return null;
    }

    private static bool TryParseIso(string? s, out DateTimeOffset result)
    {
        // RoundtripKind preserves an explicit offset; AssumeUniversal keeps an offset-less string
        // machine-independent (interpreted as UTC) so the function stays pure (no ambient timezone).
        if (s is not null &&
            DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out result))
        {
            return true;
        }
        result = default;
        return false;
    }
}

namespace Scope.Core.Telemetry.Trace;

using Scope.Core.CostEstimation;
using Scope.Core.Entitlement;
using static Scope.Core.Telemetry.Trace.TraceProjection;
using System.Globalization;

/// <summary>
/// Assembles complete scope-trace-v1 envelopes (both tiers) from an <see cref="ObservabilityAuditDb"/>.
/// This is the projection layer the thin server adapter delegates to: it owns ALL contract assembly
/// (envelope wrapping, tier selection, paging) so Program.cs holds no projection logic. Read-only.
/// </summary>
/// <param name="pricesAsOf">
/// The resolved "prices as of" stamp (<see cref="ModelPricingConfig.PricesAsOf"/>
/// via <see cref="PricingResolver"/>), or null when no pricing config is configured. Threaded onto
/// every cost Money/MoneyAggregate that carries a value so cost blocks surface the date the prices
/// were valid; null leaves cost blocks stamp-free. A date string is content-free (a timestamp, not
/// content), so it is safe in both the local-full and packet tiers.
/// </param>
/// <param name="estimationPricing">
/// Optional external pricing config (local pricing.json or LiteLLM cache) to use as a higher-priority
/// override for the provider_estimated cost fallback in rollup computation. When null, the built-in
/// seed table (<see cref="SeedPricingTable"/>) is used directly. The seed table is ALWAYS tried as
/// the last resort regardless of this parameter.
/// </param>
public sealed class TraceDocuments(ObservabilityAuditDb db, TraceIdentity identity, string generatorVersion, string? pricesAsOf = null, ModelPricingConfig? estimationPricing = null)
{
    private readonly TraceGenerator _generator = new("scope", generatorVersion);

    private TraceEnvelope<TBody> Wrap<TBody>(string kind, string tier, TBody body, TracePage? page)
        => new(
            TraceSchema.Schema, kind, tier,
            GeneratedAt: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
            Generator: _generator,
            MachineId: identity.MachineId,
            WorkspaceId: identity.WorkspaceId,
            OrgId: identity.OrgId,
            TaxonomyVersion: ToolTaxonomy.Version,
            Page: page,
            Body: body);

    private static bool IsPacket(string tier) => string.Equals(tier, TraceSchema.TierPacket, StringComparison.Ordinal);

    /// <summary>Resolves a tier string to a canonical value; unknown values default to local-full.</summary>
    public static string ResolveTier(string? tier)
        => string.Equals(tier, TraceSchema.TierPacket, StringComparison.Ordinal) ? TraceSchema.TierPacket : TraceSchema.TierLocalFull;

    /// <summary>
    /// True when the observability schema (obs_* tables) is present. When false, the database is a
    /// fresh/empty telemetry.db (or the file was just created by opening it) and every trace document
    /// is served as an honest empty body of the requested kind rather than failing.
    /// </summary>
    private bool HasData => db.HasObservabilitySchema();

    // -----------------------------------------------------------------------
    // sessions_list
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds and serializes the sessions_list envelope for the given tier (local-full or packet).
    ///
    /// When <paramref name="entitlementProvider"/> is supplied, the returned local-full envelope carries an
    /// additive <c>limits</c> object (T-2026-06-13-33 response signal). The session count is clamped to
    /// <c>min(limit, MaxDashboardSessions)</c> for free-tier callers, and a retention Since cutoff is
    /// injected when <c>ObservabilityRetentionDays</c> is non-null. Older rows are NEVER deleted; this is
    /// a read-time filter only.
    ///
    /// Pagination (T-2026-06-17-03): <paramref name="offset"/> is the 0-based row offset for the page.
    /// The response body carries <c>total_count</c> = analytically correct count over the active filter
    /// set (from/to/includeDeleted/retention) BEFORE the offset/limit slice. Callers MUST use
    /// total_count to compute page counts; do not derive counts from the returned list length.
    /// Back-compat: <paramref name="offset"/> = 0 is the legacy behavior (no offset, full limit).
    /// </summary>
    /// <param name="from">
    /// Optional ISO-8601 UTC lower bound (T-2026-06-15-16/ST-01). Sessions are filtered by overlap:
    /// last_seen_at &gt;= from. AND-combined with the retention Since cutoff.
    /// </param>
    /// <param name="to">
    /// Optional ISO-8601 UTC upper bound (T-2026-06-15-16/ST-01). Sessions are filtered by overlap:
    /// first_seen_at &lt;= to.
    /// </param>
    /// <param name="includeDeleted">
    /// When false (default) soft-deleted sessions are excluded; when true they are surfaced with
    /// their deleted_at timestamp for the trash view (T-2026-06-15-16/ST-01).
    /// </param>
    /// <param name="offset">
    /// 0-based row offset for pagination (T-2026-06-17-03). Default 0 = first page (legacy behavior).
    /// Applied AFTER entitlement cap and retention filter, before the limit slice.
    /// </param>
    public string Sessions(int limit, string tier, IEntitlementProvider? entitlementProvider = null,
        string? from = null, string? to = null, bool includeDeleted = false, int offset = 0)
    {
        var entState = ResolveEntitlement(entitlementProvider);
        var limits = entState.EffectiveLimits;

        // Retention window: inject Since = now - retention days, taking the more-recent of
        // any future caller-supplied Since (none here, but the merge rule is documented).
        string? retentionSince = null;
        bool retentionApplied = false;
        if (limits.ObservabilityRetentionDays is int retDays)
        {
            retentionSince = DateTime.UtcNow.AddDays(-retDays).ToString("o");
            retentionApplied = true;
        }

        // Total count BEFORE offset/limit slice (analytically correct over filter set).
        // Free-tier caps the total at MaxDashboardSessions so that FE page counts are consistent
        // with what the user can actually page through.
        long totalCount = 0;
        if (HasData)
        {
            var rawCount = db.CountSessionsWithEventCounts(retentionSince, from, to, includeDeleted);
            totalCount = limits.MaxDashboardSessions is int cap
                ? Math.Min(rawCount, cap)
                : rawCount;
        }

        // Session cap: fetch one extra row beyond the effective cap to detect whether the list
        // was truncated. The caller's requested limit is also applied when no cap applies.
        // With pagination, we fetch limit rows starting at offset within the (possibly capped) set.
        var fetchLimit = limits.MaxDashboardSessions is int fetchCap
            ? Math.Min(limit, fetchCap) + 1
            : limit;
        // Fetch from offset+0 to detect cap; then slice to limit.
        var rows = HasData
            ? db.GetSessionsWithEventCounts(fetchLimit + offset, retentionSince, from, to, includeDeleted)
            : [];

        bool sessionsCapped = false;
        if (limits.MaxDashboardSessions is int sessionCap)
        {
            var effectiveCap = Math.Min(limit + offset, sessionCap);
            sessionsCapped = rows.Count > effectiveCap;
            if (sessionsCapped)
                rows = rows.Take(effectiveCap).ToList();
        }

        // Apply offset slice (pagination).
        if (offset > 0)
            rows = rows.Skip(offset).ToList();

        var sessions = TraceProjection.Sessions(rows);

        if (IsPacket(tier))
        {
            // Packet tier carries no limits signal (content-free by construction).
            return TraceJson.Serialize(Wrap(TraceSchema.KindSessionsList, TraceSchema.TierPacket,
                new PacketSessionsListBody(TraceProjection.ToPacket(sessions)), null));
        }

        var limitsDto = new ObservabilityLimitsDto(
            Tier: entState.Tier,
            ObservabilityRetentionDays: limits.ObservabilityRetentionDays,
            MaxDashboardSessions: limits.MaxDashboardSessions,
            AuditExportAllowed: limits.AuditExportAllowed,
            SessionsCapped: sessionsCapped,
            RetentionWindowApplied: retentionApplied);

        return TraceJson.Serialize(Wrap(TraceSchema.KindSessionsList, TraceSchema.TierLocalFull,
            new SessionsListBodyWithLimits(sessions, limitsDto, TotalCount: totalCount), null));
    }

    /// <summary>
    /// Soft-deletes a session (sets deleted_at to now, UTC) without touching any of its events,
    /// spans, requests, or costs (T-2026-06-15-16/ST-01, NO cascade). Returns the new deleted_at
    /// stamp on success, or null when the session key is unknown (server maps null to 404). When the
    /// obs schema is absent the session cannot exist, so this is a null (not-found) result.
    /// </summary>
    public string? SoftDeleteSession(string sessionKey)
        => HasData ? db.SoftDeleteSession(sessionKey) : null;

    /// <summary>
    /// Restores a soft-deleted session (clears deleted_at) (T-2026-06-15-16/ST-01). Returns true
    /// when a session row matched the key, false when unknown (server maps false to 404). When the
    /// obs schema is absent the session cannot exist, so this is a false (not-found) result.
    /// </summary>
    public bool RestoreSession(string sessionKey)
        => HasData && db.RestoreSession(sessionKey);

    /// <summary>
    /// Resolves the entitlement state from the injected provider (or Free when none supplied).
    /// Never throws: any failure degrades to <see cref="EntitlementState.FreeState"/>.
    /// </summary>
    private static EntitlementState ResolveEntitlement(IEntitlementProvider? provider)
    {
        if (provider == null) return EntitlementState.FreeState;
        try { return provider.GetState(); }
        catch { return EntitlementState.FreeState; }
    }

    /// <summary>
    /// Computes the effective retention Since cutoff for the given limits, merging with an optional
    /// caller-supplied Since string. Takes the more-recent of the two (caller wins if narrower).
    /// Returns null when no retention window applies.
    /// </summary>
    internal static string? MergeRetentionSince(EntitlementLimits limits, string? callerSince)
    {
        if (limits.ObservabilityRetentionDays is not int retDays)
            return callerSince; // paid: pass caller's Since through unchanged

        var cutoff = DateTime.UtcNow.AddDays(-retDays);
        var cutoffStr = cutoff.ToString("o");

        if (callerSince == null)
            return cutoffStr;

        // Take the more-recent of the two (larger string = later ISO-8601 date).
        return string.Compare(callerSince, cutoffStr, StringComparison.Ordinal) >= 0
            ? callerSince
            : cutoffStr;
    }

    // -----------------------------------------------------------------------
    // session_graph
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds and serializes the session_graph envelope rooted at the given session key.
    ///
    /// When <paramref name="entitlementProvider"/> is supplied, the free-tier retention window is
    /// applied as a Since lower-bound on the GetSessionGraphData query (filter-only, never deletes rows).
    /// Paid (null retention) is unaffected.
    /// </summary>
    public string SessionGraph(string rootSessionKey, string tier, IEntitlementProvider? entitlementProvider = null)
    {
        var since = MergeRetentionSince(ResolveEntitlement(entitlementProvider).EffectiveLimits, callerSince: null);
        var graphRows = HasData ? db.GetSessionGraphData(rootSessionKey, since) : [];
        if (graphRows.Count > 0)
        {
            // Enrich with agent-span pseudo-nodes when no session-level children exist (hook case).
            var perSession = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
            db.EnrichGraphWithSpans(graphRows, perSession);
        }

        var body = TraceGraphProjection.Build(rootSessionKey, graphRows, pricesAsOf);
        return IsPacket(tier)
            ? TraceJson.Serialize(Wrap(TraceSchema.KindSessionGraph, TraceSchema.TierPacket, TraceGraphProjection.ToPacket(body), null))
            : TraceJson.Serialize(Wrap(TraceSchema.KindSessionGraph, TraceSchema.TierLocalFull, body, null));
    }

    // -----------------------------------------------------------------------
    // trace (paged)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the full trace envelope for a session, chronologically ordered with resolved parent
    /// annotations. Paged by a (created_at, id) keyset cursor encoded as an opaque base64url token.
    ///
    /// When <paramref name="entitlementProvider"/> is supplied, the free-tier retention window is
    /// applied as a Since lower-bound on the GetFullTrace query (filter-only, never deletes rows).
    /// The caller-supplied <paramref name="callerSince"/> is merged with the entitlement cutoff
    /// via <see cref="MergeRetentionSince"/> (more-recent wins). Paid (null retention) is unaffected.
    ///
    /// Pagination (T-2026-06-17-03): the body carries <c>total_count</c> = analytically correct
    /// count of all entries for this session matching the retention filter, BEFORE the cursor/limit
    /// slice. Use total_count to compute page counts on the client; never approximate from slice length.
    /// </summary>
    /// <param name="offset">
    /// 0-based row offset for page-based pagination (T-2026-06-17-03). Default 0 (legacy: no skip).
    /// When &gt; 0, the cursor parameter is ignored and a simple Skip/Take slice is used instead.
    /// </param>
    /// <param name="assumedModel">
    /// Optional user-selected model peg for the scope_estimated (Tier-3) cost fallback (T-2026-06-20-03,
    /// OBS-10). MUST be the SAME peg the Accounting rollup (<c>/api/trace/rollup</c>) uses, so the
    /// per-row attributed costs price each scope_estimated task identically to the Accounting total
    /// and the cross-screen reconciliation invariant (SUM(timeline rows) == Accounting total) holds.
    /// When null or unresolvable, the hard-coded Sonnet default is used (matching the rollup default).
    /// </param>
    public string Trace(string sessionKey, string tier, int limit, string? cursor,
        IEntitlementProvider? entitlementProvider = null, string? callerSince = null, int offset = 0,
        string? assumedModel = null)
    {
        var since = MergeRetentionSince(ResolveEntitlement(entitlementProvider).EffectiveLimits, callerSince);

        // Total entry count (analytically correct, retention-filtered) for the FE page count.
        var totalCount = HasData ? db.CountFullTraceEntries(sessionKey, since) : 0L;

        // GetFullTrace orders DESC internally then sorts ascending by created_at; we re-sort ascending
        // and apply keyset paging over (created_at, id) so cursors are stable and append-friendly.
        var all = HasData
            ? db.GetFullTrace(new AuditFilters(RunKey: sessionKey, FullPayloads: true, Limit: 100000, Since: since))
            : [];
        all.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(Str(a, "created_at") ?? "", Str(b, "created_at") ?? "");
            return c != 0 ? c : string.CompareOrdinal(Str(a, "id") ?? "", Str(b, "id") ?? "");
        });

        // Per-row attributed cost (T-2026-06-20-03, OBS-10): compute the canonical rollup over the
        // SAME merged rows, then partition each by_task cost down to the rows that produced it. The
        // attribution reconciles BY CONSTRUCTION (sum of per-row costs == this session's rollup total)
        // because it redistributes the rollup's own per-task numbers, never re-pricing. Costs are
        // attributed over the WHOLE session (not the page) for the same reason aliases are: the
        // partition is session-global. The subject/window/counts here only shape the rollup envelope;
        // the by_task cost partition the attribution reads is identical to what /api/trace/rollup serves.
        // The assumedModel peg is threaded through so a scope_estimated (Tier-3) task prices the same
        // here as it does in the Accounting rollup; otherwise the timeline would peg Sonnet while
        // Accounting pegged the user-selected model and the per-row SUM would diverge from the total.
        var rollupSubject = new TraceSubject("session", sessionKey, TraceIdCodec.DisplayId(sessionKey));
        var rollup = TraceRollup.Compute(rollupSubject, windowFrom: null, windowTo: null, all,
            requestCount: 0, eventCount: 0, pricesAsOf, estimationPricing, defaultEstimationModel: assumedModel);
        var costByEntryId = WireCostAttribution.Attribute(all, rollup);

        // Aliases must be computed over the WHOLE session (stored sort order), not the page, so
        // ordinals are stable regardless of paging. Project the full set, then page the projection.
        var entriesAll = TraceProjection.Entries(all, pricesAsOf, costByEntryId);

        TracePage page;
        List<TraceEntry> pageRows;
        if (offset > 0)
        {
            // Page-based mode: simple offset/limit slice (no cursor emitted — consumers use total_count + pageSize).
            pageRows = entriesAll.Skip(offset).Take(limit).ToList();
            page = new TracePage(Cursor: null, NextCursor: null, Limit: limit);
        }
        else
        {
            var (cursorPageRows, nextCursor) = PageByCursor(entriesAll, cursor, limit);
            pageRows = cursorPageRows;
            page = new TracePage(cursor, nextCursor, limit);
        }

        var subject = new TraceSubject(
            Kind: "session",
            Id: sessionKey,
            DisplayId: TraceIdCodec.DisplayId(sessionKey));

        return IsPacket(tier)
            ? TraceJson.Serialize(Wrap(TraceSchema.KindTrace, TraceSchema.TierPacket,
                new PacketTraceBody(subject, TraceProjection.ToPacketEntries(pageRows)), page))
            : TraceJson.Serialize(Wrap(TraceSchema.KindTrace, TraceSchema.TierLocalFull,
                new TraceBody(subject, pageRows, TotalCount: totalCount), page));
    }

    private static (List<TraceEntry> Page, string? NextCursor) PageByCursor(
        List<TraceEntry> entries, string? cursor, int limit)
    {
        var start = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            var decoded = DecodeCursor(cursor);
            if (decoded != null)
            {
                // First entry strictly after the cursor key (created_at, id).
                for (var i = 0; i < entries.Count; i++)
                {
                    var key = (entries[i].CreatedAt ?? "") + " " + entries[i].Id;
                    if (string.CompareOrdinal(key, decoded) > 0) { start = i; break; }
                    start = i + 1;
                }
            }
        }

        var page = new List<TraceEntry>();
        var end = Math.Min(start + limit, entries.Count);
        for (var i = start; i < end; i++) page.Add(entries[i]);

        string? next = null;
        if (end < entries.Count && page.Count > 0)
        {
            var last = page[^1];
            next = EncodeCursor((last.CreatedAt ?? "") + " " + last.Id);
        }
        return (page, next);
    }

    private static string EncodeCursor(string key)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? DecodeCursor(string cursor)
    {
        try
        {
            var s = cursor.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException) { return null; }
    }

    // -----------------------------------------------------------------------
    // rollup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds and serializes the accounting rollup envelope for a session (by_task partition + unattributed).
    ///
    /// When <paramref name="entitlementProvider"/> is supplied, the free-tier retention window is applied
    /// as a Since lower-bound (filter-only, never deletes rows). Paid (null retention) is unaffected.
    /// </summary>
    public string Rollup(string sessionKey, string tier, IEntitlementProvider? entitlementProvider = null, string? assumedModel = null)
    {
        var body = RollupBody(sessionKey, entitlementProvider, assumedModel);
        // Rollup body is content-free in both tiers; only the envelope tier label differs.
        return TraceJson.Serialize(Wrap(TraceSchema.KindRollup,
            IsPacket(tier) ? TraceSchema.TierPacket : TraceSchema.TierLocalFull, body, null));
    }

    /// <summary>
    /// Builds the rollup body (without serialization), the single source the served envelope and the
    /// coherence tests share. Exposes the partition so tests can assert totals == sum(by_task) +
    /// unattributed. An absent obs schema yields a coherent all-empty rollup, never a throw.
    ///
    /// When <paramref name="entitlementProvider"/> is supplied, the free-tier retention Since cutoff is
    /// injected into both the GetFullTrace query and the session-row lookup for coherence.
    /// </summary>
    public RollupBody RollupBody(string sessionKey, IEntitlementProvider? entitlementProvider = null, string? assumedModel = null)
    {
        var since = MergeRetentionSince(ResolveEntitlement(entitlementProvider).EffectiveLimits, callerSince: null);

        // Session window + counts from the session row. Single-session callers pay one targeted
        // session-row lookup; the ledger batch path supplies a pre-fetched row instead (see the
        // sessionRow overload) to avoid one full session-table scan per session.
        var sessionRow = HasData
            ? db.GetSessionsWithEventCounts(100000, since).Find(s => string.Equals(Str(s, "session_key"), sessionKey, StringComparison.Ordinal))
            : null;

        return RollupBody(sessionKey, sessionRow, since, assumedModel);
    }

    /// <summary>
    /// Builds the rollup body for a session using a PRE-FETCHED session-window row (the
    /// <see cref="ObservabilityAuditDb.GetSessionsWithEventCounts(int,string?,string?,string?,bool)"/>
    /// shape), eliminating the per-session full session-table scan. This is the seam the ledger
    /// reconciliation path (ScopeApp.Command) uses: it fetches the window's session list ONCE and
    /// then calls this overload per session. <paramref name="sessionRow"/> may be null (session has
    /// no obs_agent_sessions row), in which case window/counts default to null/0 — the rollup is
    /// still coherent. <paramref name="since"/> is the already-resolved retention cutoff applied to
    /// the per-session GetFullTrace query (the batch caller resolves it once).
    /// </summary>
#pragma warning disable MA0016 // public Scope.Core seam: Dictionary<string,object?> is the canonical loosely-typed DB-row shape shared with the ledger batch path; signature preserved (T-2026-06-17 sweep, owner)
    public RollupBody RollupBody(
        string sessionKey,
        Dictionary<string, object?>? sessionRow,
        string? since,
        string? assumedModel = null)
    {
#pragma warning restore MA0016
        return RollupWithRows(sessionKey, sessionRow, since, assumedModel).Rollup;
    }

    /// <summary>
    /// The merged trace rows for a session paired with the canonical <see cref="RollupBody"/> computed
    /// over the SAME rows. The two MUST stay coupled so a consumer (e.g. the canonical by_tool_type
    /// cost derivation) can call <see cref="WireCostAttribution.Attribute"/> with both, whose exact-sum
    /// partition guarantee only holds when the rows and rollup are the same row set.
    /// </summary>
    /// <param name="Rows">The <see cref="ObservabilityAuditDb.GetFullTrace"/> merged rows for the session.</param>
    /// <param name="Rollup">The canonical rollup computed over exactly <paramref name="Rows"/>.</param>
#pragma warning disable MA0016 // public Scope.Core seam: the canonical loosely-typed DB-row list is the shared row shape (matches RollupBody/SessionWindowRows); preserved by design
    public readonly record struct RollupAndRows(
        IReadOnlyList<Dictionary<string, object?>> Rows,
        RollupBody Rollup);
#pragma warning restore MA0016

    /// <summary>
    /// Builds the rollup body AND returns the merged trace rows it was computed from, for a PRE-FETCHED
    /// session-window row. This is the seam the canonical by_tool_type cost derivation
    /// (<c>ScopeApp.CommandLedger</c>) uses: it needs both the rows (to read each event's tool name) and
    /// the rollup (the canonical per-task cost) so <see cref="WireCostAttribution.Attribute"/> can
    /// partition the rollup's cost down to the per-tool-call rows and bucket each by taxonomy. Sharing
    /// the SAME fetched rows with the rollup is load-bearing: the attribution's exact-sum guarantee only
    /// holds when the rows and rollup are the same set. Never throws; degrades to empty rows + an empty
    /// rollup when the obs schema is absent.
    /// </summary>
#pragma warning disable MA0016 // public Scope.Core seam: Dictionary<string,object?> is the canonical loosely-typed DB-row shape shared with the ledger batch path
    public RollupAndRows RollupWithRows(
        string sessionKey,
        Dictionary<string, object?>? sessionRow,
        string? since,
        string? assumedModel = null)
    {
#pragma warning restore MA0016
        var rows = HasData
            ? db.GetFullTrace(new AuditFilters(RunKey: sessionKey, FullPayloads: true, Limit: 100000, Since: since))
            : [];

        var from = sessionRow != null ? Str(sessionRow, "first_seen_at") : null;
        var to = sessionRow != null ? Str(sessionRow, "last_seen_at") : null;
        var requestCount = sessionRow != null ? Long(sessionRow, "request_count") ?? 0 : 0;
        var eventCount = sessionRow != null ? Long(sessionRow, "event_count") ?? 0 : 0;

        var subject = new TraceSubject("session", sessionKey, TraceIdCodec.DisplayId(sessionKey));
        // Pass estimationPricing for the provider_estimated fallback when wire tokens + model are
        // known but obs_cost_estimates has no cost. When null, TraceRollup falls back to the built-in
        // SeedPricingTable directly via ModelIdNormalizer.Lookup.
        // Pass assumedModel for the scope_estimated default (user-selected peg). When null or
        // unresolvable, TraceRollup falls back to the hard-coded Sonnet default.
        var rollup = TraceRollup.Compute(subject, from, to, rows, requestCount, eventCount, pricesAsOf, estimationPricing, assumedModel);
        return new RollupAndRows(rows, rollup);
    }

    /// <summary>
    /// Fetches the window's session-window rows ONCE (overlap by first_seen_at/last_seen_at) for the
    /// ledger reconciliation batch. Returns an empty list when the obs schema is absent. The returned
    /// rows carry session_key, first_seen_at, last_seen_at, request_count, event_count, agent_type —
    /// everything the per-session <see cref="RollupBody(string,Dictionary{string,object?}?,string?,string?)"/>
    /// overload needs without a re-scan.
    /// </summary>
    /// <param name="from">ISO-8601 UTC lower bound (last_seen_at &gt;= from).</param>
    /// <param name="to">ISO-8601 UTC upper bound (first_seen_at &lt;= to).</param>
    /// <param name="entitlementProvider">When supplied, the free-tier retention cutoff is AND-combined.</param>
#pragma warning disable MA0016 // public Scope.Core seam: returns the canonical loosely-typed DB-row list consumed by the ledger reconciliation batch; signature preserved (T-2026-06-17 sweep, owner)
    public List<Dictionary<string, object?>> SessionWindowRows(
        string from, string to, IEntitlementProvider? entitlementProvider = null)
    {
#pragma warning restore MA0016
        if (!HasData) return [];
        var since = MergeRetentionSince(ResolveEntitlement(entitlementProvider).EffectiveLimits, callerSince: null);
        return db.GetSessionsWithEventCounts(100000, since, from, to);
    }

    // -----------------------------------------------------------------------
    // top_actions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds and serializes the top-actions envelope (ranked by count and by time) for a session.
    ///
    /// When <paramref name="entitlementProvider"/> is supplied, the free-tier retention window is applied
    /// as a Since lower-bound (filter-only, never deletes rows). Paid (null retention) is unaffected.
    /// </summary>
    public string TopActions(string sessionKey, string tier, int limit, IEntitlementProvider? entitlementProvider = null)
    {
        var since = MergeRetentionSince(ResolveEntitlement(entitlementProvider).EffectiveLimits, callerSince: null);
        var rows = HasData
            ? db.GetFullTrace(new AuditFilters(RunKey: sessionKey, FullPayloads: true, Limit: 100000, Since: since))
            : [];
        var body = TraceTopActions.Compute(rows, limit);
        return TraceJson.Serialize(Wrap(TraceSchema.KindTopActions,
            IsPacket(tier) ? TraceSchema.TierPacket : TraceSchema.TierLocalFull, body, null));
    }

    // -----------------------------------------------------------------------
    // wire
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds and serializes the wire-calls envelope. Honest empty section when no wire rows exist.
    ///
    /// When <paramref name="entitlementProvider"/> is supplied, the free-tier retention window is
    /// applied as a Since lower-bound on the wire_calls.started_at column (filter-only, never deletes rows).
    /// Paid (null retention) is unaffected.
    /// </summary>
    public string Wire(int limit, string tier, IEntitlementProvider? entitlementProvider = null)
    {
        var since = MergeRetentionSince(ResolveEntitlement(entitlementProvider).EffectiveLimits, callerSince: null);
        var tableExists = db.HasWireCallsTable();
        var rows = tableExists ? db.GetWireCalls(limit, since) : [];
        var body = TraceWireProjection.Build(tableExists, rows);
        return TraceJson.Serialize(Wrap(TraceSchema.KindWire,
            IsPacket(tier) ? TraceSchema.TierPacket : TraceSchema.TierLocalFull, body, null));
    }
}

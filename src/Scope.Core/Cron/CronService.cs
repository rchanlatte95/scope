using Scope.Core.Entitlement;
using Scope.Core.Utilities;

namespace Scope.Core.Cron;

/// <summary>
/// Shared cron-agent definition service used by CLI, server API, and supervisor adapters.
/// </summary>
public sealed class CronService
{
    /// <summary>
    /// The free-tier enabled-agent limit as defined by <see cref="EntitlementLimits.FreeTierDefaults"/>.
    /// Preserved as a named constant for documentation and test reference.
    /// </summary>
    public const int FreeTierEnabledAgentLimit = 3;

    private readonly CronRepository _repository;
    private readonly IEntitlementProvider _entitlement;

    /// <summary>
    /// Creates a <see cref="CronService"/> backed by the given repository.
    /// When <paramref name="entitlementProvider"/> is null, a <see cref="DefaultEntitlementProvider"/>
    /// is constructed automatically, so existing <c>new CronService(repo)</c> call sites
    /// continue to compile and get real entitlement resolution.
    /// </summary>
    public CronService(CronRepository repository, IEntitlementProvider? entitlementProvider = null)
    {
        _repository = repository;
        _entitlement = entitlementProvider ?? new DefaultEntitlementProvider();
    }

    /// <summary>
    /// Returns the effective enabled-agent cap for the current entitlement state.
    /// Null means no cap (unlimited). Reads from the injected provider on every call
    /// so that in-process state changes are reflected without restart.
    /// </summary>
    private int? EffectiveEnabledCap => _entitlement.GetState().EffectiveLimits.MaxEnabledCrons;

    public CronResult<CronAgentDefinition> Create(CronCreateRequest request)
    {
        _repository.EnsureSchema();

        // Label is create-only (immutable after creation); validate it here, then validate the
        // shared editable fields through the ONE validator that edit also uses (CF-validation).
        var label = TrimToNull(request.Label);
        if (label is null)
            return CronResult.Fail<CronAgentDefinition>("Label is required.");
        if (label.Length > 100)
            return CronResult.Fail<CronAgentDefinition>("Label must be 100 characters or fewer.");

        var validation = ValidateEditableFields(
            request.Schedule, request.Prompt, request.RepoPath, request.Harness,
            request.Model, request.TokenBudget, request.BlockConcurrentRuns, request.MaxRuntimeSeconds);
        if (!validation.Success)
            return CronResult.Fail<CronAgentDefinition>(validation.Error!);

        var fields = validation.Value!;
        if (_repository.GetAgentByLabel(label) is not null)
            return CronResult.Fail<CronAgentDefinition>($"Cron agent label already exists: {label}");

        if (request.Enabled && EffectiveEnabledCap is int createLimit && _repository.CountEnabledAgents() >= createLimit)
            return CronResult.Fail<CronAgentDefinition>($"Free tier allows at most {createLimit} enabled cron agents.");

        var now = DateTime.UtcNow.ToString("o");
        var agent = new CronAgentDefinition(
            Guid.GetGuid7String(),
            label,
            fields.Schedule,
            fields.Prompt,
            fields.RepoPath,
            fields.Harness,
            fields.Model,
            fields.TokenBudget,
            request.Enabled,
            now,
            now,
            BlockConcurrentRuns: fields.BlockConcurrentRuns,
            MaxRuntimeSeconds: fields.MaxRuntimeSeconds);

        _repository.InsertAgent(agent);
        return CronResult.Ok(agent);
    }

    /// <summary>
    /// Updates (FULL REPLACE) the editable fields of an existing cron-agent definition identified by
    /// its immutable <paramref name="label"/> (T-2026-06-17-05). Validation runs through the SAME
    /// <see cref="ValidateEditableFields"/> path as <see cref="Create"/> so create and edit cannot
    /// drift (CF-validation). The label is never changed; it is the API/URL key and identity.
    /// <list type="bullet">
    /// <item>404-style failure ("not found") when the label is missing or the agent is soft-deleted.</item>
    /// <item><c>enabled</c> is NOT touched (it stays on enable/disable), so the free-tier enabled
    ///   count is unchanged and no cap check is needed here.</item>
    /// <item><c>created_at</c> and <c>id</c> are preserved; <c>updated_at</c> is bumped to now.</item>
    /// <item>No cron_runs row is touched: history survives an edit verbatim (the FK is by agent id).</item>
    /// <item>An edit during an in-flight run is allowed; the new definition applies to the NEXT run,
    ///   never to a run already in flight (the runner snapshots the definition at launch).</item>
    /// </list>
    /// </summary>
    public CronResult<CronAgentDefinition> Update(string label, CronUpdateRequest request)
    {
        _repository.EnsureSchema();

        if (label.IsNullOrWhitespace)
            return CronResult.Fail<CronAgentDefinition>("Label is required.");

        var trimmedLabel = label.Trim();
        var existing = _repository.GetAgentByLabel(trimmedLabel);
        // 404 when missing OR soft-deleted: a deleted agent is not editable (mirrors the run-now guard).
        if (existing is null || existing.DeletedAt is not null)
            return CronResult.Fail<CronAgentDefinition>($"Cron agent not found: {trimmedLabel}");

        var validation = ValidateEditableFields(
            request.Schedule, request.Prompt, request.RepoPath, request.Harness,
            request.Model, request.TokenBudget, request.BlockConcurrentRuns, request.MaxRuntimeSeconds);
        if (!validation.Success)
            return CronResult.Fail<CronAgentDefinition>(validation.Error!);

        var fields = validation.Value!;
        var now = DateTime.UtcNow.ToString("o");

        // Full replace of editable fields. Identity (id, label), created_at, and enabled are
        // preserved verbatim; updated_at is bumped. Label is taken from the existing row so a
        // forged/renamed payload can never change identity.
        var updated = existing with
        {
            Schedule = fields.Schedule,
            Prompt = fields.Prompt,
            RepoPath = fields.RepoPath,
            Harness = fields.Harness,
            Model = fields.Model,
            TokenBudget = fields.TokenBudget,
            BlockConcurrentRuns = fields.BlockConcurrentRuns,
            MaxRuntimeSeconds = fields.MaxRuntimeSeconds,
            UpdatedAt = now,
        };

        if (!_repository.UpdateAgent(updated))
            return CronResult.Fail<CronAgentDefinition>($"Cron agent not found: {trimmedLabel}");

        return CronResult.Ok(updated);
    }

    /// <summary>
    /// Partial (FETCH-MERGE) edit of an existing cron-agent definition (T-2026-06-17). Fetches the
    /// agent by its immutable <paramref name="label"/>, overlays ONLY the fields the caller
    /// explicitly set on <paramref name="patch"/> (a null field keeps the current value), then runs
    /// the MERGED full object through <see cref="Update"/> so it hits the SAME
    /// <see cref="ValidateEditableFields"/> validation + write path as a full replace. This makes the
    /// CLI/MCP edit NON-destructive for omitted optional fields without weakening validation.
    /// <list type="bullet">
    /// <item>404-style failure ("not found") when the label is missing or the agent is soft-deleted
    ///   (identical to <see cref="Update"/>).</item>
    /// <item><see cref="CronUpdatePatch.BlockConcurrentRuns"/> is tri-state: null preserves the
    ///   current setting; true/false sets it.</item>
    /// <item>The merged state is still validated, so an edit that produces an invalid result (e.g. a
    ///   zero token budget, an unknown harness) is rejected with the same errors as create.</item>
    /// </list>
    /// </summary>
    public CronResult<CronAgentDefinition> UpdateMerged(string label, CronUpdatePatch patch)
    {
        _repository.EnsureSchema();

        if (label.IsNullOrWhitespace)
            return CronResult.Fail<CronAgentDefinition>("Label is required.");

        var trimmedLabel = label.Trim();
        var existing = _repository.GetAgentByLabel(trimmedLabel);
        // 404 when missing OR soft-deleted, identical to Update (a deleted agent is not editable).
        if (existing is null || existing.DeletedAt is not null)
            return CronResult.Fail<CronAgentDefinition>($"Cron agent not found: {trimmedLabel}");

        // Overlay ONLY explicitly-set patch fields on top of the current values. A null patch field
        // means "not provided" and preserves the existing value. The merged full object is then run
        // through the shared full-replace Update so validation cannot drift.
        var merged = new CronUpdateRequest(
            Schedule: patch.Schedule ?? existing.Schedule,
            Prompt: patch.Prompt ?? existing.Prompt,
            RepoPath: patch.RepoPath ?? existing.RepoPath,
            Harness: patch.Harness ?? existing.Harness,
            Model: patch.Model ?? existing.Model,
            TokenBudget: patch.TokenBudget ?? existing.TokenBudget,
            BlockConcurrentRuns: patch.BlockConcurrentRuns ?? existing.BlockConcurrentRuns,
            MaxRuntimeSeconds: patch.MaxRuntimeSeconds ?? existing.MaxRuntimeSeconds);

        return Update(trimmedLabel, merged);
    }

    public IReadOnlyList<CronAgentDefinition> List()
    {
        _repository.EnsureSchema();
        return _repository.ListAgents();
    }

    public CronResult<CronAgentDefinition> Get(string label)
    {
        _repository.EnsureSchema();
        if (label.IsNullOrWhitespace)
            return CronResult.Fail<CronAgentDefinition>("Label is required.");

        var trimmed = label.Trim();
        var agent = _repository.GetAgentByLabel(trimmed);
        return agent is null
            ? CronResult.Fail<CronAgentDefinition>($"Cron agent not found: {trimmed}")
            : CronResult.Ok(agent);
    }

    /// <summary>
    /// Enables or disables a cron-agent definition.
    /// <list type="bullet">
    /// <item>Returns 404-style failure when the label is not found.</item>
    /// <item>Idempotent: toggling to the current state succeeds without double-counting the cap.</item>
    /// <item>When enabling an agent that is not already enabled, the local free-tier enabled cap
    ///   (<see cref="FreeTierEnabledAgentLimit"/>) is enforced via <see cref="CronRepository.CountEnabledAgents"/>.</item>
    /// <item>Disable is always allowed.</item>
    /// </list>
    /// </summary>
    public CronResult<CronAgentDefinition> SetEnabled(string label, bool enabled)
    {
        _repository.EnsureSchema();
        if (label.IsNullOrWhitespace)
            return CronResult.Fail<CronAgentDefinition>("Label is required.");

        var trimmed = label.Trim();
        var agent = _repository.GetAgentByLabel(trimmed);
        if (agent is null)
            return CronResult.Fail<CronAgentDefinition>($"Cron agent not found: {trimmed}");

        // Idempotent: if already in the target state, return without cap check or DB write.
        if (agent.Enabled == enabled)
            return CronResult.Ok(agent);

        // Enforce the enabled cap when enabling (reads from entitlement; null = unlimited).
        if (enabled && EffectiveEnabledCap is int enableLimit && _repository.CountEnabledAgents() >= enableLimit)
            return CronResult.Fail<CronAgentDefinition>($"Free tier allows at most {enableLimit} enabled cron agents.");

        if (!_repository.SetAgentEnabled(trimmed, enabled))
            return CronResult.Fail<CronAgentDefinition>($"Cron agent not found: {trimmed}");

        var updated = _repository.GetAgentByLabel(trimmed);
        return updated is null
            ? CronResult.Fail<CronAgentDefinition>($"Cron agent not found: {trimmed}")
            : CronResult.Ok(updated);
    }

    /// <summary>
    /// Marks all 'running' rows older than <paramref name="maxRunningAge"/> as 'failed' with an
    /// honest error message. Returns the count of rows reaped. Never throws on an empty sweep.
    /// Default age is <see cref="DefaultMaxRunningAge"/> (~6h).
    /// </summary>
    public int ReapStaleRuns(TimeSpan maxRunningAge)
    {
        _repository.EnsureSchema();
        return _repository.ReapStaleRunningRows(maxRunningAge);
    }

    /// <summary>Default maximum age a run may stay in the 'running' state before being reaped.</summary>
    public static readonly TimeSpan DefaultMaxRunningAge = TimeSpan.FromHours(6);

    public CronResult<bool> Remove(string label)
    {
        _repository.EnsureSchema();
        if (label.IsNullOrWhitespace)
            return CronResult.Fail<bool>("Label is required.");

        return _repository.RemoveAgentByLabel(label.Trim())
            ? CronResult.Ok(true)
            : CronResult.Fail<bool>($"Cron agent not found: {label.Trim()}");
    }

    public CronResult<CronRunRecord> RecordRun(CronRunCreateRequest request)
    {
        _repository.EnsureSchema();
        if (request.CronAgentId.IsNullOrWhitespace)
            return CronResult.Fail<CronRunRecord>("Cron agent id is required.");

        if (!CronRunStatuses.IsValid(request.Status))
            return CronResult.Fail<CronRunRecord>($"Invalid cron run status: {request.Status}");

        if (request.TotalCostUsd is < 0)
            return CronResult.Fail<CronRunRecord>("Total cost must be zero or greater.");

        var run = new CronRunRecord(
            Guid.GetGuid7String(),
            request.CronAgentId.Trim(),
            TrimToNull(request.DispatchId),
            TrimToNull(request.SessionKey),
            DateTime.UtcNow.ToString("o"),
            TrimToNull(request.CompletedAt),
            request.Status,
            request.ExitCode,
            request.TotalCostUsd,
            TrimToNull(request.SkipReason),
            TrimToNull(request.ErrorMessage),
            TrimToNull(request.CostProvenance));

        _repository.InsertRun(run);
        return CronResult.Ok(run);
    }

    public IReadOnlyList<CronRunRecord> ListRuns(string cronAgentId, int limit = 50)
    {
        _repository.EnsureSchema();
        return _repository.ListRunsForAgent(cronAgentId, limit);
    }

    /// <summary>
    /// Returns the most recent in-flight ('running') run for a definition, or null when none is in
    /// flight. The run-now concurrency guard uses this to skip a concurrent launch.
    /// </summary>
    public CronRunRecord? FindRunningRun(string cronAgentId)
    {
        _repository.EnsureSchema();
        if (cronAgentId.IsNullOrWhitespace)
            return null;
        return _repository.GetRunningRunForAgent(cronAgentId.Trim());
    }

    /// <summary>
    /// Transitions a run row to a terminal state. Validates that the status is a terminal value
    /// (completed or failed) and that any cost is non-negative, mirroring <see cref="RecordRun"/>
    /// validation style. Returns the updated record on success.
    /// </summary>
    public CronResult<CronRunRecord> CompleteRun(
        string runId,
        string status,
        int? exitCode,
        double? totalCostUsd,
        string? errorMessage,
        string? costProvenance = null)
    {
        _repository.EnsureSchema();

        if (runId.IsNullOrWhitespace)
            return CronResult.Fail<CronRunRecord>("Run id is required.");

        if (status is not (CronRunStatuses.Completed or CronRunStatuses.Failed or CronRunStatuses.BudgetExhausted))
            return CronResult.Fail<CronRunRecord>(
                $"Terminal run status must be 'completed', 'failed', or 'budget_exhausted', got: {status}");

        if (totalCostUsd is < 0)
            return CronResult.Fail<CronRunRecord>("Total cost must be zero or greater.");

        var completedAt = DateTime.UtcNow.ToString("o");
        if (!_repository.UpdateRunCompletion(runId.Trim(), status, exitCode, completedAt, totalCostUsd,
                TrimToNull(errorMessage), TrimToNull(costProvenance)))
            return CronResult.Fail<CronRunRecord>($"Cron run not found: {runId.Trim()}");

        var updated = _repository.GetRunById(runId.Trim());
        return updated is null
            ? CronResult.Fail<CronRunRecord>($"Cron run not found: {runId.Trim()}")
            : CronResult.Ok(updated);
    }

    /// <summary>
    /// Returns a single cron run row by its id, or null when not found.
    /// </summary>
    public CronRunRecord? GetRunById(string runId)
    {
        _repository.EnsureSchema();
        if (runId.IsNullOrWhitespace)
            return null;
        return _repository.GetRunById(runId.Trim());
    }

    /// <summary>
    /// The ONE validator for the editable cron-agent fields, shared by <see cref="Create"/> and
    /// <see cref="Update"/> (CF-validation: create and edit cannot drift). Validates and normalizes
    /// schedule, prompt, repo path, harness, model, token budget, and max-runtime; returns the
    /// normalized fields. Label and enabled are handled by the callers (label is create-only and
    /// immutable; enabled stays on the enable/disable endpoints), so they are not parameters here.
    /// </summary>
    private static CronResult<CronEditableFields> ValidateEditableFields(
        string? schedule,
        string? prompt,
        string? repoPath,
        string? harness,
        string? model,
        int? tokenBudget,
        bool blockConcurrentRuns,
        int? maxRuntimeSeconds)
    {
        var scheduleResult = CronScheduleValidator.Validate(schedule);
        if (!scheduleResult.Success)
            return CronResult.Fail<CronEditableFields>(scheduleResult.Error!);

        var normalizedPrompt = TrimToNull(prompt);
        if (normalizedPrompt is null)
            return CronResult.Fail<CronEditableFields>("Prompt is required.");

        var repoPathResult = NormalizeRepoPath(repoPath);
        if (!repoPathResult.Success)
            return CronResult.Fail<CronEditableFields>(repoPathResult.Error!);

        var normalizedHarness = TrimToNull(harness)?.ToLowerInvariant();
        if (normalizedHarness is null)
            return CronResult.Fail<CronEditableFields>("Harness is required.");
        if (!CronHarnesses.IsSupported(normalizedHarness))
            return CronResult.Fail<CronEditableFields>("Harness must be 'claude-code' or 'codex'.");

        var normalizedModel = TrimToNull(model);
        if (normalizedModel is { Length: > 120 })
            return CronResult.Fail<CronEditableFields>("Model must be 120 characters or fewer.");

        if (tokenBudget is <= 0)
            return CronResult.Fail<CronEditableFields>("Token budget must be greater than zero.");

        // Max runtime is optional (NULL = global supervisor default). When set it must be positive.
        if (maxRuntimeSeconds is <= 0)
            return CronResult.Fail<CronEditableFields>("Max runtime seconds must be greater than zero.");

        return CronResult.Ok(new CronEditableFields(
            scheduleResult.Value!,
            normalizedPrompt,
            repoPathResult.Value!,
            normalizedHarness,
            normalizedModel,
            tokenBudget,
            blockConcurrentRuns,
            maxRuntimeSeconds));
    }

    private static CronResult<string> NormalizeRepoPath(string? repoPath)
    {
        var trimmed = TrimToNull(repoPath);
        if (trimmed is null)
            return CronResult.Fail<string>("Repo path is required.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CronResult.Fail<string>("Repo path is invalid.");
        }

        return Directory.Exists(fullPath)
            ? CronResult.Ok(fullPath)
            : CronResult.Fail<string>($"Repo path does not exist: {fullPath}");
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

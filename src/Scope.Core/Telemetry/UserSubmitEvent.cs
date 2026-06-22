namespace Scope.Core.Telemetry;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// A PRIVILEGED, first-class human-intervention event. Every user submit in a session
/// (the initiating prompt, follow-ups, interrupts, tool approvals/denials, redirections,
/// and the cron prompt) is recorded as an <c>obs_agent_events</c> row with
/// <c>event_type = 'user_submit'</c> and a ratified <see cref="UserSubmitSubtype"/> member
/// carried in the <c>event_subtype</c> column. Content stays local; it is never uplinked.
/// </summary>
/// <param name="SessionKey">The session this human intervention belongs to.</param>
/// <param name="Subtype">One of the seven ratified taxonomy members (see <see cref="UserSubmitSubtype"/>).</param>
/// <param name="Content">The raw submitted text (the human's words). Stays local.</param>
/// <param name="TurnIndex">
/// Monotonic, 0-based ordinal of this submit within the session as known by the capture path.
/// The initiating submit is turn 0. Part of the dedup identity. Null when the path cannot
/// determine an ordinal.
/// </param>
/// <param name="RequestId">Optional obs_requests correlation; null for cron/hook paths with no request row.</param>
/// <param name="ContentHash">
/// Stable hex SHA-256 over the normalized content; the primary dedup key together with
/// <see cref="SessionKey"/>. When null, <see cref="ObservabilityDb.InsertUserSubmit"/>
/// derives it from <see cref="Content"/>.
/// </param>
public sealed record UserSubmitEvent(
    string SessionKey,
    string Subtype,
    string Content,
    int? TurnIndex = null,
    string? RequestId = null,
    string? ContentHash = null)
{
    /// <summary>
    /// Computes the stable dedup hash for a submit's content: lowercase hex SHA-256 over the
    /// UTF-8 bytes of the content with surrounding whitespace trimmed. Deterministic and
    /// path-independent so the same human submit captured by two redundant paths (payload,
    /// hook, cron) collapses to one row. Empty/whitespace content hashes the empty string.
    /// </summary>
    public static string ComputeContentHash(string? content)
    {
        var normalized = (content ?? string.Empty).Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>The dedup hash for this event, using the explicit <see cref="ContentHash"/> when set.</summary>
    public string ResolveContentHash() => ContentHash ?? ComputeContentHash(Content);
}

/// <summary>
/// The ratified (owner, 2026-06-16, A8) seven-member taxonomy of human-in-the-loop submits.
/// The value strings are the persisted <c>event_subtype</c> tokens; do not rename without a
/// schema/contract decision. These are the ONLY valid subtypes for an <c>event_type =
/// 'user_submit'</c> row.
/// </summary>
public static class UserSubmitSubtype
{
    /// <summary>The strictly-first human prompt that starts an interactive session.</summary>
    public const string InitiatingPrompt = "initiating_prompt";

    /// <summary>A subsequent human prompt continuing the same session.</summary>
    public const string FollowUp = "follow_up";

    /// <summary>A human interrupt (e.g. escape/stop) mid-run.</summary>
    public const string Interrupt = "interrupt";

    /// <summary>A human approving a gated tool call.</summary>
    public const string ToolApproval = "tool_approval";

    /// <summary>A human denying a gated tool call.</summary>
    public const string ToolDenial = "tool_denial";

    /// <summary>A human redirecting the agent (course correction without a stop).</summary>
    public const string Redirection = "redirection";

    /// <summary>The cron definition's prompt surfaced as the cron session's initiating submit.</summary>
    public const string CronPrompt = "cron_prompt";

    /// <summary>All ratified members, for validation and enumeration.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        InitiatingPrompt, FollowUp, Interrupt, ToolApproval, ToolDenial, Redirection, CronPrompt,
    ];

    /// <summary>True when <paramref name="subtype"/> is one of the seven ratified members.</summary>
    public static bool IsValid(string? subtype) => subtype is not null && All.Contains(subtype, StringComparer.Ordinal);
}

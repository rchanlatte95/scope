namespace Scope.Core.Telemetry.Ingestion;

using System.Text.Json;
using Serilog;

/// <summary>
/// A single assistant turn distilled from a Claude Code session transcript JSONL file
/// (<c>~/.claude/projects/&lt;project&gt;/&lt;cc-session-id&gt;.jsonl</c>). One turn corresponds to
/// one distinct <c>message.id</c>: a Claude Code assistant message is written across MULTIPLE
/// JSONL lines (one per content block batch), all carrying the SAME <c>message.id</c> and a
/// byte-identical <c>usage</c> object, while the <c>tool_use</c> ids accumulate across those lines.
/// This record collapses that to a single, wire-call-shaped turn.
/// </summary>
/// <param name="MessageId">The assistant <c>message.id</c>; the stable dedup/grouping key for the turn.</param>
/// <param name="Model">The model id reported on the assistant message (e.g. a <c>claude-*</c> id); null if absent.</param>
/// <param name="InputTokens">Uncached input tokens from <c>usage.input_tokens</c>; null if absent.</param>
/// <param name="OutputTokens">Output tokens from <c>usage.output_tokens</c>; null if absent.</param>
/// <param name="CacheCreationTokens">Cache-write tokens from <c>usage.cache_creation_input_tokens</c>; null if absent.</param>
/// <param name="CacheReadTokens">Cache-read tokens from <c>usage.cache_read_input_tokens</c>; null if absent.</param>
/// <param name="ToolUseIds">The union of every <c>tool_use</c> block <c>id</c> emitted across the message's lines, in first-seen order.</param>
/// <param name="StartedAt">The earliest top-level <c>timestamp</c> seen for the message id; null if no line carried one.</param>
/// <param name="CcSessionId">The Claude Code session id (the transcript filename id), passed through by the caller.</param>
public sealed record TranscriptTurn(
    string MessageId,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    long? CacheCreationTokens,
    long? CacheReadTokens,
    IReadOnlyList<string> ToolUseIds,
    string? StartedAt,
    string CcSessionId);

/// <summary>
/// Scope-owned host adapter that parses a Claude Code session transcript JSONL into Scope-owned
/// <see cref="TranscriptTurn"/> records. ALL Claude-Code-transcript-shape knowledge is confined to
/// this file (platform-independence rule): a future host version that drops or renames a field
/// degrades to null/skip here, never an exception elsewhere.
///
/// Verified host shape (Claude Code 2.1.x): each line is a JSON object with a top-level
/// <c>type</c> ("assistant" | "user" | ...), <c>timestamp</c>, <c>sessionId</c>, <c>uuid</c>, and
/// (for assistant lines) a <c>message</c> object: <c>{ id, model, role:"assistant",
/// usage:{ input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens },
/// content:[ {type:"text",text:...} | {type:"tool_use", id:"toolu_...", ...} ] }</c>.
///
/// Grouping (verified): MULTIPLE assistant lines share one <c>message.id</c>. The <c>usage</c>
/// object is byte-identical across those lines, so usage is taken ONCE per message id (first line
/// that carries it). The <c>tool_use</c> ids ACCUMULATE across the lines, so they are UNIONed per
/// message id. One <see cref="TranscriptTurn"/> is emitted per distinct message id, in first-seen
/// order; <c>StartedAt</c> is the earliest <c>timestamp</c> observed for that id. <c>user</c> and
/// other line types carry no token data and are ignored for token purposes.
///
/// NEVER THROWS: this runs under the PostToolUse hook contract (capture must never crash callers).
/// A malformed line is skipped (warned once-per-call at debug volume); a missing / unreadable file
/// returns an empty list. Uses <see cref="JsonDocument"/> (no reflection) for AOT compatibility.
/// </summary>
public static class ClaudeCodeTranscriptReader
{
    /// <summary>
    /// Reads and groups the assistant turns from a Claude Code transcript file at
    /// <paramref name="transcriptPath"/>. Streams the file line by line (does not load the whole
    /// file into a single string) so multi-MB transcripts stay allocation-reasonable. A missing or
    /// unreadable file returns an empty list; never throws. <paramref name="ccSessionId"/> is
    /// stamped onto every emitted turn (the transcript filename id, used downstream for
    /// per-generation attribution).
    /// </summary>
    /// <param name="transcriptPath">Absolute path to the <c>.jsonl</c> transcript file.</param>
    /// <param name="ccSessionId">The Claude Code session id to stamp onto each turn.</param>
    /// <returns>One <see cref="TranscriptTurn"/> per distinct <c>message.id</c>, in first-seen order.</returns>
    public static IReadOnlyList<TranscriptTurn> ReadTurns(string transcriptPath, string ccSessionId)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath))
            return [];

        IEnumerable<string> lines;
        try
        {
            if (!File.Exists(transcriptPath))
                return [];
            // ReadLines streams lazily; the whole file is never materialized as one string.
            lines = File.ReadLines(transcriptPath);
        }
        catch (Exception ex)
        {
            // Missing / unreadable / locked file: capture must not crash the hook.
            Log.Warning("Transcript unreadable at {Path}: {Message}", transcriptPath, ex.Message);
            return [];
        }

        try
        {
            return ReadTurnsFromLines(lines, ccSessionId);
        }
        catch (Exception ex)
        {
            // Defensive: any enumeration-time IO error degrades to whatever could not be read.
            Log.Warning("Transcript read failed at {Path}: {Message}", transcriptPath, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Parses the turns from an in-memory JSONL <paramref name="content"/> string (one JSON object
    /// per line). Testability overload mirroring <see cref="ReadTurns(string,string)"/>; same
    /// grouping, never throws. An empty / whitespace string yields an empty list.
    /// </summary>
    /// <param name="content">The raw JSONL transcript content.</param>
    /// <param name="ccSessionId">The Claude Code session id to stamp onto each turn.</param>
    /// <returns>One <see cref="TranscriptTurn"/> per distinct <c>message.id</c>, in first-seen order.</returns>
    public static IReadOnlyList<TranscriptTurn> ParseTurns(string? content, string ccSessionId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        return ReadTurnsFromLines(SplitLines(content), ccSessionId);
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    private static List<TranscriptTurn> ReadTurnsFromLines(IEnumerable<string> lines, string ccSessionId)
    {
        // First-seen-order accumulator: one builder per distinct message id.
        var order = new List<string>();
        var builders = new Dictionary<string, TurnBuilder>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument? doc = ParseLine(line);
            if (doc is null)
                continue; // malformed line -> skip, never throw

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                // Only assistant lines carry token/message data; ignore user and other types.
                if (!string.Equals(GetString(root, "type"), "assistant", StringComparison.Ordinal))
                    continue;

                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                    continue;

                var messageId = GetString(message, "id");
                if (string.IsNullOrEmpty(messageId))
                    continue; // no grouping key -> cannot attribute the turn

                if (!builders.TryGetValue(messageId, out var builder))
                {
                    builder = new TurnBuilder(messageId);
                    builders[messageId] = builder;
                    order.Add(messageId);
                }

                // Earliest top-level timestamp wins for StartedAt.
                builder.ObserveTimestamp(GetString(root, "timestamp"));

                // Model + usage taken ONCE per message id (byte-identical across the message's lines).
                if (!builder.HasUsage)
                {
                    builder.SetModel(GetString(message, "model"));
                    if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        builder.SetUsage(
                            GetLong(usage, "input_tokens"),
                            GetLong(usage, "output_tokens"),
                            GetLong(usage, "cache_creation_input_tokens"),
                            GetLong(usage, "cache_read_input_tokens"));
                }
                else if (builder.Model is null)
                {
                    // Usage already taken but model was absent on the usage-bearing line; backfill if present.
                    builder.SetModel(GetString(message, "model"));
                }

                // tool_use ids ACCUMULATE across the message's lines -> union.
                AccumulateToolUseIds(message, builder);
            }
        }

        if (order.Count == 0)
            return [];

        var turns = new List<TranscriptTurn>(order.Count);
        foreach (var id in order)
            turns.Add(builders[id].Build(ccSessionId));
        return turns;
    }

    private static JsonDocument? ParseLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // One bad line must not abort the whole transcript (skip-not-throw).
            Log.Debug("Skipping malformed transcript line");
            return null;
        }
    }

    private static void AccumulateToolUseIds(JsonElement message, TurnBuilder builder)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            if (!string.Equals(GetString(block, "type"), "tool_use", StringComparison.Ordinal))
                continue;
            var id = GetString(block, "id");
            if (!string.IsNullOrEmpty(id))
                builder.AddToolUseId(id);
        }
    }

    /// <summary>Per-message-id mutable accumulator collapsing a message's many lines into one turn.</summary>
    private sealed class TurnBuilder(string messageId)
    {
        private readonly List<string> _toolUseIds = [];
        private readonly HashSet<string> _seenToolUseIds = new(StringComparer.Ordinal);

        public bool HasUsage { get; private set; }
        public string? Model { get; private set; }

        private long? _inputTokens;
        private long? _outputTokens;
        private long? _cacheCreationTokens;
        private long? _cacheReadTokens;
        private string? _startedAt;

        public void SetModel(string? model)
        {
            if (model is not null)
                Model = model;
        }

        public void SetUsage(long? input, long? output, long? cacheCreation, long? cacheRead)
        {
            _inputTokens = input;
            _outputTokens = output;
            _cacheCreationTokens = cacheCreation;
            _cacheReadTokens = cacheRead;
            HasUsage = true;
        }

        public void ObserveTimestamp(string? timestamp)
        {
            if (string.IsNullOrEmpty(timestamp))
                return;
            // ISO-8601 timestamps sort lexicographically by chronology; keep the earliest.
            if (_startedAt is null || string.CompareOrdinal(timestamp, _startedAt) < 0)
                _startedAt = timestamp;
        }

        public void AddToolUseId(string id)
        {
            if (_seenToolUseIds.Add(id))
                _toolUseIds.Add(id);
        }

        public TranscriptTurn Build(string ccSessionId)
            => new(
                messageId,
                Model,
                _inputTokens,
                _outputTokens,
                _cacheCreationTokens,
                _cacheReadTokens,
                _toolUseIds,
                _startedAt,
                ccSessionId);
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? GetLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return null;
    }
}

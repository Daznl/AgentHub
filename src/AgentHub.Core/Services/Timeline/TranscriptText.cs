using System.Collections.Concurrent;
using System.IO;
using AgentHub.Services.Context;
using AgentHub.Services.History;

namespace AgentHub.Services.Timeline;

/// <summary>What a session said, read from its transcript: the prompts the user typed and the session's last reply.</summary>
public sealed record TranscriptText(IReadOnlyList<HistoryEntry> Prompts, string? LastReply)
{
    public static readonly TranscriptText Empty = new([], null);
}

/// <summary>
/// Reads and caches the text of transcripts for the timeline (a transcript can be tens of MB, and every expanded card and
/// every review wants the same prompts). A cached entry is reused while the file's length is unchanged.
/// </summary>
public static class TranscriptTexts
{
    private static readonly ConcurrentDictionary<string, (long Length, TranscriptText Text)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The transcript's prompts and last reply. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it cannot be read.</summary>
    public static TranscriptText Read(string path, ContextSource agent)
    {
        var length = new FileInfo(path).Length;
        if (Cache.TryGetValue(path, out var cached) && cached.Length == length) return cached.Text;

        var entries = SessionHistoryReader.ReadEntries(path, agent).Entries;
        var prompts = entries.Where(e => e.Kind == HistoryKind.Prompt && !e.IsSubAgent).ToList();
        var lastReply = entries.LastOrDefault(e => e.Kind == HistoryKind.Reply && !e.IsSubAgent && e.Text.Trim().Length > 0)?.Text;
        var text = new TranscriptText(prompts, lastReply);
        Cache[path] = (length, text);
        return text;
    }

    /// <summary>Like <see cref="Read"/>, but <see cref="TranscriptText.Empty"/> when the file cannot be read.</summary>
    public static TranscriptText TryRead(string path, ContextSource agent)
    {
        try { return Read(path, agent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return TranscriptText.Empty; }
    }
}

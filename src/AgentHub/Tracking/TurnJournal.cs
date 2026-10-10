using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHub.Tracking;

/// <summary>
/// Per-repository log of finished turns at <c>.agenthub/journal.jsonl</c>: one JSON object per line,
/// shared by every session working in the repository. A turn that continues after it finished is
/// appended again with the same <c>turnId</c>, as is a turn once its summary and suggestion are ready; readers keep the last line for each id.
/// The file is kept out of Git through <c>.git/info/exclude</c>, which never changes the repository.
/// </summary>
public static class TurnJournal
{
    public const string RelativePath = ".agenthub/journal.jsonl";

    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>True when <paramref name="folder"/> is the root of a Git working tree.</summary>
    public static bool IsRepositoryRoot(string folder) =>
        Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"));

    public static string PathFor(string repositoryRoot) =>
        Path.Combine(repositoryRoot, ".agenthub", "journal.jsonl");

    public static void Append(string repositoryRoot, TranscriptTurn turn, string agent)
    {
        var path = PathFor(repositoryRoot);
        var entry = new JournalEntry
        {
            TurnId = turn.TurnId,
            SessionId = turn.SessionId,
            Agent = agent,
            Status = turn.Status,
            StartedAt = turn.StartedAt,
            EndedAt = turn.EndedAt,
            UserMessage = turn.UserMessage,
            FinalText = turn.FinalText,
            Summary = turn.Summary,
            Suggestion = turn.Suggestion,
            ToolCalls = turn.ToolCalls,
            FilesTouched = turn.FilesTouched.Count > 0 ? [.. turn.FilesTouched] : null
        };
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";

        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var isNew = !File.Exists(path);
            File.AppendAllText(path, line);
            if (isNew) ExcludeFromGit(repositoryRoot);
        }
    }

    /// <summary>Reads the journal, keeping only the latest entry for each turn, oldest turn first.</summary>
    public static List<JournalEntry> Read(string repositoryRoot)
    {
        var path = PathFor(repositoryRoot);
        if (!File.Exists(path)) return [];

        var latest = new Dictionary<string, JournalEntry>();
        var order = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JournalEntry? entry;
            try { entry = JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (entry?.TurnId is null) continue;
            if (!latest.ContainsKey(entry.TurnId)) order.Add(entry.TurnId);
            latest[entry.TurnId] = entry;
        }
        return order.Select(id => latest[id]).ToList();
    }

    // Adds the journal to .git/info/exclude so it never shows up as a change or gets committed by accident.
    private static void ExcludeFromGit(string repositoryRoot)
    {
        try
        {
            var gitDir = Path.Combine(repositoryRoot, ".git");
            if (!Directory.Exists(gitDir)) return; // worktrees and submodules: left alone

            var exclude = Path.Combine(gitDir, "info", "exclude");
            var pattern = "/" + RelativePath;
            if (File.Exists(exclude) && File.ReadAllLines(exclude).Any(l => l.Trim() == pattern)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            var prefix = File.Exists(exclude) && !File.ReadAllText(exclude).EndsWith('\n') && new FileInfo(exclude).Length > 0 ? "\n" : "";
            File.AppendAllText(exclude, $"{prefix}# AgentHub turn journal (local only)\n{pattern}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class JournalEntry
{
    public int V { get; set; } = 1;
    public string? TurnId { get; set; }
    public string? SessionId { get; set; }
    public string? Agent { get; set; }
    public TurnStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string UserMessage { get; set; } = "";
    public string FinalText { get; set; } = "";
    public string? Summary { get; set; }
    public string? Suggestion { get; set; }
    public int ToolCalls { get; set; }
    public List<string>? FilesTouched { get; set; }
}

using System.IO;
using System.Text;
using System.Text.Json;
using AgentHub.Models;

namespace AgentHub.Services.Ideas;

/// <summary>
/// Stores steering notes in steering.json next to ideas.json. Kept apart from <see cref="ProjectIdea"/> on
/// purpose: the orchestrator and the Ideas window each hold their own copy of an idea and save it whole,
/// so a note stored on the idea could be overwritten by the other side's next save.
/// </summary>
public sealed class SteeringService
{
    /// <summary>Heading in .agenthub/handoff.md under which notes are recorded for later sessions.</summary>
    public const string HandoffHeading = "## Steering notes";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storagePath;

    /// <summary>Raised after any add, delivery or removal, with the idea it concerned.</summary>
    public event Action<string>? Changed;

    public SteeringService(string storagePath) => _storagePath = storagePath;

    /// <summary>Uses steering.json in the same folder as the given ideas.json.</summary>
    public static SteeringService BesideIdeas(IdeaService ideas) =>
        new(Path.Combine(Path.GetDirectoryName(ideas.StoragePath) ?? ".", "steering.json"));

    public async Task<List<SteeringNote>> GetForIdeaAsync(string ideaId) =>
        (await LoadAsync()).Where(n => n.IdeaId == ideaId).OrderBy(n => n.CreatedAt).ToList();

    public async Task<List<SteeringNote>> GetPendingAsync(string ideaId) =>
        (await GetForIdeaAsync(ideaId)).Where(n => !n.IsDelivered).ToList();

    public async Task<SteeringNote> AddAsync(string ideaId, string text)
    {
        var note = new SteeringNote { IdeaId = ideaId, Text = text.Trim() };
        await MutateAsync(list => list.Add(note));
        Changed?.Invoke(ideaId);
        return note;
    }

    public async Task MarkDeliveredAsync(IEnumerable<SteeringNote> notes)
    {
        var ids = notes.Select(n => n.Id).ToHashSet();
        if (ids.Count == 0) return;
        var now = DateTimeOffset.Now;
        string? ideaId = null;
        await MutateAsync(list =>
        {
            foreach (var n in list.Where(n => ids.Contains(n.Id)))
            {
                n.DeliveredAt ??= now;
                ideaId = n.IdeaId;
            }
        });
        if (ideaId != null) Changed?.Invoke(ideaId);
    }

    /// <summary>Removes a note that has not been delivered yet. Returns false if it was already delivered or is gone.</summary>
    public async Task<bool> RemovePendingAsync(string noteId)
    {
        string? ideaId = null;
        await MutateAsync(list =>
        {
            var note = list.FirstOrDefault(n => n.Id == noteId && !n.IsDelivered);
            if (note == null) return;
            ideaId = note.IdeaId;
            list.Remove(note);
        });
        if (ideaId == null) return false;
        Changed?.Invoke(ideaId);
        return true;
    }

    /// <summary>
    /// Builds the message typed into the agent for one or more notes. One line, because injected prompts are
    /// flattened anyway, and it ends by sending the agent back to work.
    /// </summary>
    public static string FormatDelivery(IReadOnlyList<SteeringNote> notes)
    {
        var sb = new StringBuilder("[DIRECTION FROM THE USER]: ");
        if (notes.Count == 1)
        {
            sb.Append(notes[0].Text);
        }
        else
        {
            for (var i = 0; i < notes.Count; i++) sb.Append($"({i + 1}) {notes[i].Text} ");
        }
        sb.Append(" — Take this into account from now on (it is also recorded under \"")
          .Append(HandoffHeading.TrimStart('#', ' '))
          .Append("\" in .agenthub/handoff.md, keep it there), then carry on building.");
        return sb.ToString();
    }

    /// <summary>
    /// Returns the handoff text with the note added as a bullet under <see cref="HandoffHeading"/>, creating
    /// the section at the end if it is missing. Existing content is kept as it is.
    /// </summary>
    public static string AddToHandoff(string handoff, SteeringNote note)
    {
        var bullet = $"- {note.CreatedAt:yyyy-MM-dd HH:mm}: {note.Text.Replace("\r", "").Replace("\n", " ")}";
        var lines = handoff.Replace("\r\n", "\n").Split('\n').ToList();

        var headingIndex = lines.FindIndex(l => l.TrimEnd().Equals(HandoffHeading, StringComparison.OrdinalIgnoreCase));
        if (headingIndex < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add("");
            lines.Add(HandoffHeading);
            lines.Add("");
            lines.Add("Direction from the project owner, newest last. Follow it; later notes win over earlier ones.");
            lines.Add("");
            lines.Add(bullet);
            lines.Add("");
            return string.Join("\n", lines);
        }

        // Insert after the last line of this section (before the next heading of the same or higher level).
        var end = headingIndex + 1;
        while (end < lines.Count && !(lines[end].StartsWith("# ") || lines[end].StartsWith("## "))) end++;
        var insertAt = end;
        while (insertAt > headingIndex + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
        lines.Insert(insertAt, bullet);
        return string.Join("\n", lines);
    }

    private async Task<List<SteeringNote>> LoadAsync()
    {
        await _lock.WaitAsync();
        try { return await ReadAsync(); }
        finally { _lock.Release(); }
    }

    private async Task MutateAsync(Action<List<SteeringNote>> change)
    {
        await _lock.WaitAsync();
        try
        {
            var list = await ReadAsync();
            change(list);
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _storagePath + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, list, JsonOptions);
            }
            File.Move(temp, _storagePath, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    private async Task<List<SteeringNote>> ReadAsync()
    {
        if (!File.Exists(_storagePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_storagePath);
            return await JsonSerializer.DeserializeAsync<List<SteeringNote>>(stream, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

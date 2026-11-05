using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHub.Services.Orchestrator;

/// <summary>One worker session inside a run.</summary>
public sealed class WorkerSessionRecord
{
    public int Number { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    /// <summary>Kept local: the path only, never the worker's transcript itself.</summary>
    public string? TranscriptPath { get; set; }
}

/// <summary>run.json: the facts of one orchestrator run (one idea, from start to its end).</summary>
public sealed class RunRecord
{
    public int FormatVersion { get; set; } = 1;
    public string RunId { get; set; } = "";
    public string IdeaId { get; set; } = "";
    public string IdeaTitle { get; set; } = "";
    public string Spec { get; set; } = "";
    public string TargetDirectory { get; set; } = "";
    public string WorkerCommand { get; set; } = "";
    public string OrchestratorModel { get; set; } = "";
    public string Workflow { get; set; } = "Builder";
    public string WorkflowId { get; set; } = OrchestratorWorkflow.DefaultId;
    /// <summary>Hash of the workflow text as it ran (<see cref="OrchestratorWorkflow.Version"/>); workflow.md in the run folder is the text.</summary>
    public string? WorkflowVersion { get; set; }
    public string? CurrentStage { get; set; }
    public List<string> StagesDone { get; set; } = [];
    /// <summary>Times the run was picked up again after a pause or a closed AgentHub.</summary>
    public int Reopened { get; set; }
    public string OrchestratorSessionId { get; set; } = "";
    public string? AgentHubBuild { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>running, suspended (AgentHub closed; picked up again on the next build of the idea), completed, stopped, interrupted, switched.</summary>
    public string Outcome { get; set; } = "running";
    public string? OutcomeDetail { get; set; }
    public string? LastProgress { get; set; }
    public int OrchestratorCalls { get; set; }
    public int MessagesToWorker { get; set; }
    public int Steers { get; set; }
    public int Handoffs { get; set; }
    public int Flags { get; set; }
    public string? TargetHeadAtStart { get; set; }
    public string? TargetHeadAtEnd { get; set; }
    public List<string> WorkerCommits { get; set; } = [];
    public List<WorkerSessionRecord> WorkerSessions { get; set; } = [];

    [JsonIgnore]
    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.Now) - StartedAt;
}

/// <summary>One line of events.jsonl. <see cref="Kind"/> "turn" is one orchestrator call; the rest are notes.</summary>
public sealed class RunEvent
{
    public DateTimeOffset At { get; set; }
    /// <summary>turn, worker_session, forced_handoff, usage_wait, usage_resumed, steer, paused, resumed, error, note, end.</summary>
    public string Kind { get; set; } = "note";
    public string? Trigger { get; set; }
    public int? WorkerSession { get; set; }
    public string? Status { get; set; }
    public string? WorkerReply { get; set; }
    public List<string>? Steers { get; set; }
    public string? Action { get; set; }
    public string? ToWorker { get; set; }
    public string? SteerHandling { get; set; }
    public string? Reason { get; set; }
    public string? Progress { get; set; }
    public string? Stage { get; set; }
    public bool? StageDone { get; set; }
    public int? Attempts { get; set; }
    public double? Seconds { get; set; }
    /// <summary>What AgentHub actually typed into the worker (the orchestrator's message plus any AgentHub rules).</summary>
    public string? SentToWorker { get; set; }
    public string? Detail { get; set; }
    /// <summary>Marked ⚠ in the review: retries, forced handoffs, usage waits, pauses, errors.</summary>
    public bool Flag { get; set; }
}

/// <summary>
/// Writes a run live to its own folder (so a crash still leaves a record): run.json rewritten on every change,
/// events.jsonl appended. <see cref="FinishAsync"/> adds REVIEW.md and a copy of the orchestrator's transcript.
/// </summary>
public sealed class RunRecorder
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public RunRecord Record { get; }
    public string Folder { get; }
    public string RecordPath => Path.Combine(Folder, "run.json");
    public string EventsPath => Path.Combine(Folder, "events.jsonl");

    public RunRecorder(string folder, RunRecord record)
    {
        Folder = folder;
        Record = record;
        Directory.CreateDirectory(folder);
    }

    public static string NewRunId(string ideaTitle, DateTimeOffset at) =>
        $"{at:yyyy-MM-dd_HHmm}_{Slug(ideaTitle)}_{Guid.NewGuid().ToString("n")[..6]}";

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "idea" : slug;
    }

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try { await WriteRecordAsync(); }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(RunEvent e)
    {
        await _lock.WaitAsync();
        try
        {
            if (e.Flag) Record.Flags++;
            await File.AppendAllTextAsync(EventsPath, JsonSerializer.Serialize(e, JsonLine) + "\n");
            await WriteRecordAsync();
        }
        finally { _lock.Release(); }
    }

    private async Task WriteRecordAsync()
    {
        var temp = RecordPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Record, Json));
        File.Move(temp, RecordPath, overwrite: true);
    }

    public static List<RunEvent> ReadEvents(string eventsPath)
    {
        var list = new List<RunEvent>();
        if (!File.Exists(eventsPath)) return list;
        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<RunEvent>(line, Json) is { } e) list.Add(e); }
            catch (JsonException) { }
        }
        return list;
    }

    public static RunRecord? ReadRecord(string recordPath)
    {
        try { return File.Exists(recordPath) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(recordPath), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>Closes the run: outcome, REVIEW.md, and orchestrator.jsonl copied from Claude Code's session store.</summary>
    public async Task FinishAsync(string outcome, string? detail, string? claudeProjectsRoot = null)
    {
        await _lock.WaitAsync();
        try
        {
            Record.Outcome = outcome;
            Record.OutcomeDetail = detail;
            Record.EndedAt ??= DateTimeOffset.Now;
            var open = Record.WorkerSessions.LastOrDefault();
            if (open is { EndedAt: null }) { open.EndedAt = Record.EndedAt; open.EndReason ??= outcome; }
            await File.AppendAllTextAsync(EventsPath,
                JsonSerializer.Serialize(new RunEvent { At = Record.EndedAt.Value, Kind = "end", Action = outcome, Detail = detail }, JsonLine) + "\n");
            await WriteRecordAsync();

            var transcript = FindTranscript(Record.OrchestratorSessionId, claudeProjectsRoot);
            if (transcript is not null) File.Copy(transcript, Path.Combine(Folder, "orchestrator.jsonl"), overwrite: true);

            await File.WriteAllTextAsync(Path.Combine(Folder, "REVIEW.md"), RunReview.Render(Record, ReadEvents(EventsPath)));
        }
        finally { _lock.Release(); }
    }

    /// <summary>Claude Code keeps a session as ~/.claude/projects/&lt;folder&gt;/&lt;session id&gt;.jsonl.</summary>
    public static string? FindTranscript(string sessionId, string? claudeProjectsRoot = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        var root = claudeProjectsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>REVIEW.md and INDEX.md: the human-readable side of the run archive.</summary>
public static class RunReview
{
    public static string Render(RunRecord r, IReadOnlyList<RunEvent> events)
    {
        var sb = new StringBuilder();
        sb.Append("# Orchestrator run: ").Append(r.IdeaTitle).Append("\n\n");
        sb.Append("| | |\n|---|---|\n");
        Row(sb, "Outcome", OutcomeText(r));
        Row(sb, "Started", $"{r.StartedAt:yyyy-MM-dd HH:mm} · ran {Duration(r.Duration)}");
        Row(sb, "Workflow", $"{r.Workflow} (`{r.WorkflowId}`, version {r.WorkflowVersion ?? "?"}) · text in workflow.md");
        Row(sb, "Orchestrator", $"{r.OrchestratorModel} · {r.OrchestratorCalls} decisions · session `{r.OrchestratorSessionId}`" + (r.Reopened > 0 ? $" · picked up again {r.Reopened}×" : ""));
        if (r.StagesDone.Count > 0 || r.CurrentStage != null)
            Row(sb, "Stages", (r.StagesDone.Count > 0 ? "done: " + string.Join(", ", r.StagesDone) : "none done") + (r.CurrentStage is { } cs && !r.StagesDone.Contains(cs) ? $" · current: {cs}" : ""));
        Row(sb, "Worker", $"`{r.WorkerCommand}` in `{r.TargetDirectory}` · {r.WorkerSessions.Count} session(s) · {r.MessagesToWorker} messages sent");
        Row(sb, "Steers / handoffs / flags", $"{r.Steers} / {r.Handoffs} / {r.Flags}");
        if (!string.IsNullOrEmpty(r.AgentHubBuild)) Row(sb, "AgentHub build", r.AgentHubBuild);
        if (!string.IsNullOrEmpty(r.LastProgress)) Row(sb, "Last progress", r.LastProgress);
        sb.Append('\n');

        sb.Append("## Goal\n\n").Append(Quote(r.Spec)).Append("\n\n");

        if (r.WorkerCommits.Count > 0)
        {
            sb.Append("## Worker commits during the run (").Append(r.WorkerCommits.Count).Append(")\n\n");
            foreach (var c in r.WorkerCommits)
            {
                var space = c.IndexOf(' ');
                sb.Append("- `").Append(space > 0 ? c[..space] : c).Append("` ").Append(space > 0 ? c[(space + 1)..] : "").Append('\n');
            }
            sb.Append('\n');
        }

        var flagged = events.Where(e => e.Flag).ToList();
        if (flagged.Count > 0)
        {
            sb.Append("## ⚠ Flagged moments\n\n");
            foreach (var e in flagged) sb.Append("- ").Append(e.At.ToString("HH:mm")).Append(" · ").Append(e.Kind).Append(": ").Append(OneLine(e.Detail ?? e.Reason ?? "")).Append('\n');
            sb.Append('\n');
        }

        sb.Append("## Timeline\n\n");
        var turn = 0;
        foreach (var e in events)
        {
            if (e.Kind == "turn")
            {
                turn++;
                sb.Append("### ").Append(turn).Append(". ").Append(e.At.ToString("HH:mm:ss")).Append(" · ").Append(e.Trigger).Append(" → **").Append(e.Action).Append("**")
                  .Append(e.Flag ? " ⚠" : "").Append('\n').Append('\n');
                if (!string.IsNullOrEmpty(e.Status)) sb.Append("_Status: ").Append(OneLine(e.Status)).Append("_\n\n");
                if (e.WorkerReply is not null) sb.Append("**Worker reported:**\n\n").Append(Quote(OrchestratorMessages.Cut(e.WorkerReply, 1500, keepTail: true))).Append("\n\n");
                if (e.Steers is { Count: > 0 })
                {
                    sb.Append("**Steer from Daniel:**\n\n");
                    foreach (var s in e.Steers) sb.Append("> ").Append(OneLine(s)).Append('\n');
                    sb.Append("\n_Handling: ").Append(e.SteerHandling).Append("_\n\n");
                }
                sb.Append("**Decision:** ").Append(e.Action).Append(" · ").Append(OneLine(e.Reason ?? "")).Append('\n');
                if (!string.IsNullOrEmpty(e.Progress)) sb.Append("\n_Progress: ").Append(OneLine(e.Progress)).Append("_\n");
                if (!string.IsNullOrEmpty(e.Stage)) sb.Append("\n_Stage: ").Append(OneLine(e.Stage)).Append(e.StageDone == true ? " ✅ done" : "").Append("_\n");
                if (e.Attempts > 1) sb.Append("\n⚠ needed ").Append(e.Attempts).Append(" attempts to get valid JSON\n");
                if (!string.IsNullOrEmpty(e.ToWorker)) sb.Append("\n**Sent to worker:**\n\n").Append(Quote(e.ToWorker)).Append('\n');
                sb.Append('\n');
            }
            else
            {
                sb.Append("- ").Append(e.At.ToString("HH:mm:ss")).Append(" · ").Append(e.Flag ? "⚠ " : "").Append(e.Kind)
                  .Append(e.Action is null ? "" : $" ({e.Action})").Append(e.Detail is null ? "" : ": " + OneLine(e.Detail)).Append("\n\n");
            }
        }

        sb.Append("---\n\nFiles: `run.json` (facts), `workflow.md` (the workflow exactly as it ran), `events.jsonl` (everything AgentHub sent and did), `orchestrator.jsonl` (the orchestrator's raw Claude Code transcript). Worker transcripts stay on the machine that ran the build.\n");
        return sb.ToString();
    }

    public static string RenderIndex(IEnumerable<RunRecord> runs)
    {
        var sb = new StringBuilder("# Orchestrator runs\n\nNewest first. Open a run's `REVIEW.md` to read it.\n\n");
        sb.Append("| Started | Idea | Workflow | Outcome | Ran | Decisions | Sent | Steers | Handoffs | Flags | Commits | Model |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var r in runs.OrderByDescending(r => r.StartedAt))
        {
            sb.Append($"| {r.StartedAt:yyyy-MM-dd HH:mm} | [{Cell(r.IdeaTitle)}]({r.RunId}/REVIEW.md) | {Cell(r.Workflow)} {Cell(r.WorkflowVersion ?? "")} | {Cell(r.Outcome)} | {Duration(r.Duration)} | {r.OrchestratorCalls} | {r.MessagesToWorker} | {r.Steers} | {r.Handoffs} | {r.Flags} | {r.WorkerCommits.Count} | {Cell(r.OrchestratorModel)} |\n");
        }
        return sb.ToString();
    }

    private static string OutcomeText(RunRecord r) => r.OutcomeDetail is { Length: > 0 } d ? $"**{r.Outcome}** · {OneLine(d)}" : $"**{r.Outcome}**";

    private static void Row(StringBuilder sb, string name, string value) => sb.Append("| ").Append(name).Append(" | ").Append(Cell(value)).Append(" |\n");

    private static string Cell(string text) => OneLine(text).Replace("|", "\\|");

    private static string OneLine(string text) => text.Replace("\r", "").Replace('\n', ' ').Trim();

    private static string Quote(string text) =>
        string.Join("\n", text.Replace("\r", "").Trim().Split('\n').Select(l => "> " + l));

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(0, (int)t.TotalMinutes)}m";
}

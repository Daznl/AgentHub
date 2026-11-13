using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHub.Services.Orchestrator;

/// <summary>What it takes to pick a saved run up again: where it stood, and the orchestrator session that followed it.</summary>
public sealed record RunResume(RunSnapshot Snapshot, string? BrainSessionId, string? BrainFolder);

/// <summary>
/// One run of an advanced template as kept on disk (<c>run.json</c>): the template as the run follows it, how far it
/// has got, and what is needed to go on with it in a later AgentHub. A run that no pane is following is, whatever this
/// says, a paused one: nothing moves it until the owner resumes it.
/// </summary>
public sealed class RunRecord
{
    /// <summary>The run's folder name: when it started and the template's name.</summary>
    public string Id { get; set; } = "";
    /// <summary>The project (checkout) the run works in.</summary>
    public string Project { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    /// <summary>The template's file text as the run follows it, so the run does not change when the file does.</summary>
    public string Template { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public RunState State { get; set; }
    public PauseCause? Cause { get; set; }
    public string? Reason { get; set; }
    public string? CurrentStepId { get; set; }
    public Dictionary<string, StepState> Steps { get; set; } = [];
    /// <summary>"step 3 of 15 · Stage 1 · Build stage 1", for a list that need not read the template.</summary>
    public string Progress { get; set; } = "";
    public int StepCount { get; set; }
    public int DoneCount { get; set; }

    /// <summary>The pane it last ran on ("Terminal 2"), for the record.</summary>
    public string? Pane { get; set; }
    /// <summary>How the worker session was last started.</summary>
    public string? WorkerCommand { get; set; }
    public string? WorkerFolder { get; set; }
    /// <summary>The orchestrator's own Claude session, picked up again on resume while its transcript exists.</summary>
    public string? BrainSessionId { get; set; }
    public string? BrainFolder { get; set; }

    public bool IsFinished => State == RunState.Finished;

    public RunSnapshot Snapshot() => new(State, Cause, Reason, CurrentStepId, Steps);
    public RunResume Resume() => new(Snapshot(), BrainSessionId, BrainFolder);

    /// <summary>The template the run follows.</summary>
    /// <exception cref="FormatException">The saved text is not a template any more.</exception>
    public AdvancedTemplate ReadTemplate() => AdvancedTemplate.Parse(Template, TemplateId);

    /// <summary>Brings the record up to where the orchestrator's run stands now.</summary>
    public void Fill(PaneOrchestrator orchestrator, DateTimeOffset now)
    {
        if (orchestrator.Run is not { } run) return;
        var s = run.Snapshot();
        // The template the run follows now: the owner may have applied a changed one to it.
        TemplateName = run.Template.Name;
        Template = run.Template.Serialize();
        State = s.State;
        Cause = s.Cause;
        Reason = s.Reason;
        CurrentStepId = s.CurrentStepId;
        Steps = new Dictionary<string, StepState>(s.Steps);
        Progress = run.Progress;
        StepCount = run.Steps.Count;
        DoneCount = run.DoneCount;
        if (orchestrator.Launch is { } launch)
        {
            WorkerCommand = launch.CommandLine;
            WorkerFolder = launch.WorkingDirectory;
        }
        BrainSessionId = orchestrator.BrainSessionId ?? BrainSessionId;
        BrainFolder = orchestrator.BrainFolder ?? BrainFolder;
        UpdatedAt = now;
    }

    /// <summary>What changes when the run moves: a record is written again only when this does.</summary>
    public string Signature() => $"{State}|{Cause}|{Reason}|{CurrentStepId}|{DoneCount}|{string.Join(",", Steps.Select(s => $"{s.Key}={(int)s.Value}"))}|{BrainSessionId}|{WorkerCommand}|{WorkerFolder}|{Template}";
}

/// <summary>
/// The runs kept on this computer: <c>C:\data\agenthub\runs\&lt;id&gt;\run.json</c> (<c>AGENTHUB_RUNS_DIR</c> replaces the
/// folder). Outside any project: a run is this owner's work in progress, not part of the repository.
/// </summary>
public sealed class RunStore
{
    public const string DefaultRoot = @"C:\data\agenthub\runs";
    private const string FileName = "run.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    public string Root { get; }

    public RunStore(string? root = null) =>
        Root = !string.IsNullOrWhiteSpace(root) ? root
            : Environment.GetEnvironmentVariable("AGENTHUB_RUNS_DIR") is { Length: > 0 } custom ? custom
            : DefaultRoot;

    public string PathFor(string id) => Path.Combine(Root, id, FileName);

    /// <summary>A new run's id: when it starts and what it follows, unique among the kept runs.</summary>
    public string NewId(string templateName, DateTimeOffset now)
    {
        var id = $"{now:yyyyMMdd-HHmmss}-{OrchestratorMessages.Slug(templateName)}";
        for (var n = 2; Directory.Exists(Path.Combine(Root, id)); n++) id = $"{now:yyyyMMdd-HHmmss}-{OrchestratorMessages.Slug(templateName)}-{n}";
        return id;
    }

    /// <summary>Writes the record in one step, so a reader never sees half of it.</summary>
    public void Save(RunRecord record)
    {
        var path = PathFor(record.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        Hosting.SharedFile.Replace(temp, path);
    }

    /// <summary>Every kept run, the latest touched first. A record that cannot be read is left out (and left on disk).</summary>
    public IReadOnlyList<RunRecord> List()
    {
        if (!Directory.Exists(Root)) return [];
        var list = new List<RunRecord>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (Read(Path.Combine(folder, FileName)) is { } record) list.Add(record);
        }
        return list.OrderByDescending(r => r.UpdatedAt).ToList();
    }

    /// <summary>
    /// One record; null when there is none or it is not a record. A record is replaced on every move of its run, and
    /// Windows refuses a read for the moment that takes, so a refused read is tried again: a run must not drop out of
    /// the list because it is busy (2026-11-13: a test listing the runs while one was being written got none).
    /// </summary>
    private static RunRecord? Read(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<RunRecord>(stream, Json) is { Id.Length: > 0 } record ? record : null;
            }
            catch (JsonException) { return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 8) return null;
                Thread.Sleep(15 * attempt);
            }
        }
    }

    public RunRecord? Find(string id) => List().FirstOrDefault(r => r.Id == id);

    /// <summary>Forgets a run: its record and its folder, when nothing else was put there.</summary>
    public void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['\\', '/', ':']) >= 0 || id.Contains("..", StringComparison.Ordinal)) return; // only ever a folder directly under Root
        var folder = Path.Combine(Root, id);
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
}

/// <summary>
/// Keeps one run's record up to date while an orchestrator follows it: written when the run moves, not at every
/// message. When the orchestrator is taken off (or AgentHub closes), a run that was running is recorded as paused, since
/// nothing follows it any more.
/// </summary>
public sealed class RunRecorder : IDisposable
{
    private readonly object _lock = new();
    private readonly RunStore _store;
    private readonly PaneOrchestrator _orchestrator;
    private string _saved = "";
    private bool _closed;

    public RunRecord Record { get; }
    /// <summary>The last write failed (disk full, folder gone); null when the record is on disk as it should be.</summary>
    public string? Error { get; private set; }

    public RunRecorder(RunStore store, PaneOrchestrator orchestrator, RunRecord record)
    {
        _store = store;
        _orchestrator = orchestrator;
        Record = record;
        orchestrator.Changed += Save;
        Save();
    }

    private void Save()
    {
        lock (_lock)
        {
            if (_closed) return;
            Record.Fill(_orchestrator, DateTimeOffset.Now);
            Write();
        }
    }

    private void Write()
    {
        var signature = Record.Signature();
        if (signature == _saved) return;
        try
        {
            _store.Save(Record);
            _saved = signature;
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.Message; }
    }

    /// <summary>The orchestrator is going: the record is brought up to date one last time, a running run as paused with <paramref name="why"/>.</summary>
    public void Close(string why)
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            _orchestrator.Changed -= Save;
            Record.Fill(_orchestrator, DateTimeOffset.Now);
            if (Record.State == RunState.Running)
            {
                Record.State = RunState.Paused;
                Record.Cause = PauseCause.Owner;
                Record.Reason = why;
            }
            Write();
        }
    }

    public void Dispose() => Close("The orchestrator was taken off the session.");
}

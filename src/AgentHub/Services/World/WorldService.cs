using System.Collections.Concurrent;
using System.Text;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Timeline;

namespace AgentHub.Services.World;

/// <summary>What the main window knows that the world needs: its panes' sessions and the orchestrators over them.</summary>
public sealed record WorldUiState(IReadOnlyList<string> RegisteredRoots, IReadOnlyList<WorldPaneInfo> Panes, IReadOnlyList<WorldOrchestratorInfo> Orchestrators);

/// <summary>A Cockpit pane with a running coding session, and the transcript it is bound to.</summary>
public sealed record WorldPaneInfo(string Title, string? TranscriptPath, string Folder);

/// <summary>An orchestrator: the worker pane's transcript, its state and the step its run is on.</summary>
public sealed record WorldOrchestratorInfo(string Id, string Title, string? WorkerTranscript, string State, string? Step);

/// <summary>
/// Keeps the 3D world's two files up to date while the world is open: <c>world.json</c> (every project's git tree and
/// files, the live sessions, the orchestrators; rewritten when it changes) and the events file (each tool call, prompt,
/// reply and orchestrator instruction as it happens, tailed by Godot). Projects are the registered repositories plus
/// every repository a live session works in. Reads git, gh and transcripts; touches nothing.
/// </summary>
public sealed class WorldService : IDisposable
{
    /// <summary>A transcript written to this recently is a live session (its crab sleeps long before this).</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan EventTick = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan StructureEvery = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PullRequestsEvery = TimeSpan.FromMinutes(5);
    private const int MaxFilesPerProject = 2500;

    private readonly ProcessRunner _runner;
    private readonly Func<Task<WorldUiState>> _ui;
    private readonly string? _archiveRoot;
    private readonly string _dir;
    private readonly CancellationTokenSource _cts = new();
    private readonly TranscriptTail _tail = new();
    private readonly object _eventGate = new();
    private readonly ConcurrentDictionary<string, (TimelineScope Scope, DateTimeOffset At)> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (List<PullRequestFact>? List, DateTimeOffset At)> _prs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string? Branch, DateTimeOffset At)> _branches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _orchestratorSeen = new(StringComparer.Ordinal);
    private StreamWriter? _events;
    private long _seq;
    private List<WorldProject> _projects = [];
    private List<WorldLiveSession> _live = [];
    private List<WorldOrchestrator> _orchestrators = [];
    private string _lastLiveSignature = "";
    private volatile bool _structureWanted = true;
    private Task? _loop;

    public WorldService(ProcessRunner runner, Func<Task<WorldUiState>> ui, string? archiveRoot, string? dir = null)
    {
        _runner = runner;
        _ui = ui;
        _archiveRoot = archiveRoot;
        _dir = dir ?? WorldStore.Root;
        EventsFile = $"events-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson";
    }

    /// <summary>The events file's name inside the world folder; a new one each time the service starts.</summary>
    public string EventsFile { get; }
    public string WorldPath => WorldStore.WorldPath(_dir);
    public bool IsRunning => _loop is { IsCompleted: false };
    /// <summary>The last failure, for the status line; null while all is well.</summary>
    public string? Problem { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        Directory.CreateDirectory(_dir);
        PruneOldEvents();
        _events = new StreamWriter(new FileStream(Path.Combine(_dir, EventsFile), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false)) { AutoFlush = true };
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>The orchestrator's conversation grew: its new instructions to the worker and its notes become events.</summary>
    public void NoteOrchestrator(string id, string? workerTranscript, IReadOnlyList<(string Kind, string Text)> conversation)
    {
        int seen;
        lock (_orchestratorSeen)
        {
            seen = _orchestratorSeen.GetValueOrDefault(id, Math.Max(0, conversation.Count - 1));
            _orchestratorSeen[id] = conversation.Count;
        }
        for (var i = seen; i < conversation.Count; i++)
        {
            var (kind, text) = conversation[i];
            if (kind is not ("Sent" or "Read" or "Note" or "Question" or "Steer")) continue;
            Emit(workerTranscript ?? id, "orchestrate", null, null, $"{kind}: {WorldSnapshotBuilder.Shorten(text, 70)}", orchestrator: id);
        }
    }

    /// <summary>
    /// Every tick: new transcript lines become events. Every few ticks: which sessions are live. The structure (git trees
    /// and files) is read beside the loop, never in it, so a slow repository never holds the crabs up.
    /// </summary>
    private async Task LoopAsync(CancellationToken token)
    {
        var lastStructure = DateTimeOffset.MinValue;
        Task? structure = null;
        var tick = 0;
        using var timer = new PeriodicTimer(EventTick);
        do
        {
            try
            {
                if (tick++ % 5 == 0)
                {
                    var ui = await _ui();
                    var live = FindLive(ui);
                    var due = _structureWanted || DateTimeOffset.UtcNow - lastStructure > StructureEvery || NewProjectNeeded(live);
                    if (due && structure is not { IsCompleted: false })
                    {
                        _structureWanted = false;
                        lastStructure = DateTimeOffset.UtcNow;
                        structure = Task.Run(() => RefreshStructureAsync(ui, live, token), token);
                    }
                    UpdateLive(ui, live);
                }
                TailTranscripts();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // A background loop must outlive any one bad file or command; the next tick tries again
                Problem = ex.Message;
                Log("problem: " + ex);
            }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- structure

    private sealed record CachedProject(WorldProject Project, DateTimeOffset GitAt, DateTimeOffset FilesAt);

    private readonly ConcurrentDictionary<string, CachedProject> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _dirtyGit = new(StringComparer.Ordinal), _dirtyFiles = new(StringComparer.Ordinal);
    private static readonly TimeSpan GitEvery = TimeSpan.FromMinutes(5), FilesEvery = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reads the projects three at a time and writes world.json as each one arrives. A project's git tree is read again
    /// only after one of its sessions committed, pushed, merged or switched branch (or every five minutes); its files
    /// after a write (or every minute). The rest comes from the cache.
    /// </summary>
    private async Task RefreshStructureAsync(WorldUiState ui, IReadOnlyList<LiveTranscript> live, CancellationToken token)
    {
        try
        {
            var roots = new List<string>();
            foreach (var root in ui.RegisteredRoots.Concat(live.Select(l => l.Folder)))
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                var scope = await ScopeAsync(root, token);
                if (!roots.Contains(scope.Root, StringComparer.OrdinalIgnoreCase)) roots.Add(scope.Root);
            }
            // Projects no longer wanted leave the world
            foreach (var gone in _cache.Keys.Where(k => !roots.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) _cache.TryRemove(gone, out _);
            Publish(roots);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(roots.Select(async root =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var started = clock.Elapsed;
                    var scope = _scopes[root].Scope;
                    if (!_cache.ContainsKey(root))
                    {
                        // First sight: the island and its files now, the git tree when it has been read
                        _cache[root] = new CachedProject(await ReadSkeletonAsync(scope, token), DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
                        Publish(roots);
                    }
                    if (await RefreshProjectAsync(scope, token) is { } project)
                    {
                        Publish(roots);
                        Log($"read {project.Name}: {project.Files.Count} files, {project.Commits.Count} commits, {project.Sessions.Count} sessions in {(clock.Elapsed - started).TotalSeconds:0.0}s");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Problem = $"{root}: {ex.Message}";
                    Log($"could not read {root}: {ex}");
                }
                finally { gate.Release(); }
            }));
            Log($"structure: {roots.Count} projects in {clock.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Problem = ex.Message;
            Log("structure problem: " + ex);
        }
    }

    /// <summary>The project read again as far as it changed; null when the cache was still good.</summary>
    private async Task<WorldProject?> RefreshProjectAsync(TimelineScope scope, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var id = WorldSnapshotBuilder.ProjectId(scope.Root);
        var cached = _cache.GetValueOrDefault(scope.Root);
        var gitStale = cached is null || _dirtyGit.TryRemove(id, out _) || now - cached.GitAt > GitEvery;
        var filesStale = gitStale || _dirtyFiles.TryRemove(id, out _) || now - cached!.FilesAt > FilesEvery;
        if (gitStale)
        {
            var project = await ReadProjectAsync(scope, token);
            _cache[scope.Root] = new CachedProject(project, now, now);
            return project;
        }
        if (!filesStale) return null;
        var (files, truncated) = await ReadFilesAsync(scope, token);
        var updated = cached!.Project with { Files = files, FilesTruncated = truncated };
        _cache[scope.Root] = cached with { Project = updated, FilesAt = now };
        return updated;
    }

    /// <summary>The projects read so far, in the order of <paramref name="roots"/>, become the world's.</summary>
    private void Publish(IReadOnlyList<string> roots)
    {
        lock (_publishGate)
        {
            _projects = roots.Select(r => _cache.GetValueOrDefault(r)?.Project).OfType<WorldProject>().ToList();
            WriteWorld();
        }
    }

    private readonly object _publishGate = new();

    private void WriteWorld()
    {
        lock (_publishGate)
            WorldStore.Write(WorldPath, new WorldSnapshot(WorldSnapshot.CurrentSchema, DateTimeOffset.UtcNow, EventsFile, _projects, _live, _orchestrators));
    }

    private void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(_dir, "world-service.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<TimelineScope> ScopeAsync(string folder, CancellationToken token)
    {
        if (_scopes.TryGetValue(folder, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5)) return cached.Scope;
        var scope = await TimelineSource.ResolveAsync(_runner, folder, token);
        _scopes[folder] = (scope, DateTimeOffset.UtcNow);
        _scopes[scope.Root] = (scope, DateTimeOffset.UtcNow);
        return scope;
    }

    private async Task<WorldProject> ReadProjectAsync(TimelineScope scope, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<string>();
        void Step(string name) { steps.Add($"{name} {clock.Elapsed.TotalSeconds:0.0}s"); clock.Restart(); }
        var commits = await TimelineSource.ReadCommitsAsync(_runner, scope, token);
        var tips = await TimelineSource.ReadBranchTipsAsync(_runner, scope, token);
        Step("git");
        List<PullRequestFact>? prs = null;
        if (scope.IsGit)
        {
            if (_prs.TryGetValue(scope.Root, out var p) && DateTimeOffset.UtcNow - p.At < PullRequestsEvery) prs = p.List;
            else
            {
                prs = await TimelineSource.ReadPullRequestsAsync(_runner, scope, token);
                _prs[scope.Root] = (prs, DateTimeOffset.UtcNow);
            }
        }
        Step("pull requests");
        var squashes = await new SquashMatcher(_runner, scope).MatchAsync(commits, BranchGraph.OpenTips(commits, tips), SquashMatcher.FromPullRequests(prs, commits));
        Step("squashes");
        var sessions = await Task.Run(() => TimelineSource.ReadSessions(scope, _archiveRoot), token);
        Step("transcripts");
        var model = TimelineBuilder.Build(sessions, commits, tips, DateTimeOffset.UtcNow, squashes);
        var worktrees = await WorldSource.ReadWorktreesAsync(_runner, scope, token);
        var (files, truncated) = await ReadFilesAsync(scope, token);
        Step("files");
        Log($"{scope.Name}: {string.Join(", ", steps)}");
        return WorldSnapshotBuilder.BuildProject(scope, model, prs, worktrees, files, truncated);
    }

    /// <summary>A project before its git tree is read: its files and worktrees only, so its island shows at once.</summary>
    private async Task<WorldProject> ReadSkeletonAsync(TimelineScope scope, CancellationToken token)
    {
        var worktrees = await WorldSource.ReadWorktreesAsync(_runner, scope, token);
        var (files, truncated) = await ReadFilesAsync(scope, token);
        return new WorldProject(WorldSnapshotBuilder.ProjectId(scope.Root), scope.Name, scope.Root, "main", scope.IsGit, scope.Folders,
            [], [], [], [], [], worktrees, files, truncated);
    }

    private async Task<(List<WorldFileEntry>, bool)> ReadFilesAsync(TimelineScope scope, CancellationToken token)
    {
        if (!scope.IsGit) return ([], false);
        try
        {
            var (code, output, _) = await _runner.RunAsync("git", ["ls-files", "-z"], scope.Root, token);
            return code == 0 ? await Task.Run(() => WorldSnapshotBuilder.ParseFiles(output, scope.Root, MaxFilesPerProject), token) : ([], false);
        }
        catch (System.ComponentModel.Win32Exception) { return ([], false); }
    }

    // ---------------------------------------------------------------- live sessions

    private sealed record LiveTranscript(string Path, ContextSource Agent, string Folder, string? Title, DateTimeOffset LastActivity);

    private readonly ConcurrentDictionary<string, (SessionSummary? Summary, long Length)> _summaries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transcripts written within <see cref="LiveWindow"/>, from every Claude Code project folder and the recent Codex days.</summary>
    private List<LiveTranscript> FindLive(WorldUiState ui)
    {
        var since = DateTime.UtcNow - LiveWindow;
        var found = new List<LiveTranscript>();
        void Consider(string path, ContextSource agent)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LastWriteTimeUtc < since) return;
            var summary = Summary(path, agent, info.Length);
            var folder = summary?.Folder;
            if (string.IsNullOrWhiteSpace(folder) || SessionFolderDiscovery.IsAgentHubOwn(folder) || SessionFolderDiscovery.IsTemporary(folder)) return;
            found.Add(new LiveTranscript(path, agent, folder, summary?.Title ?? WorldSnapshotBuilder.Shorten(summary?.FirstPrompt, 50),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }
        try
        {
            if (Directory.Exists(SessionContextReader.ClaudeProjectsDir))
                foreach (var dir in Directory.EnumerateDirectories(SessionContextReader.ClaudeProjectsDir))
                    foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                        Consider(file, ContextSource.ClaudeCode);
            foreach (var day in new[] { DateTime.Now, DateTime.Now.AddDays(-1) })
            {
                var dir = Path.Combine(SessionContextReader.CodexSessionsDir, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
                if (Directory.Exists(dir))
                    foreach (var file in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
                        Consider(file, ContextSource.Codex);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem = ex.Message; }
        // A pane's bound transcript counts even when quiet for long, so its crab stays while the pane is open
        foreach (var pane in ui.Panes)
            if (pane.TranscriptPath is { } p && File.Exists(p) && found.All(f => !f.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                var agent = p.Contains(@"\.codex\", StringComparison.OrdinalIgnoreCase) ? ContextSource.Codex : ContextSource.ClaudeCode;
                var summary = Summary(p, agent, new FileInfo(p).Length);
                found.Add(new LiveTranscript(p, agent, summary?.Folder ?? pane.Folder, summary?.Title, new DateTimeOffset(File.GetLastWriteTimeUtc(p), TimeSpan.Zero)));
            }
        return found;
    }

    private SessionSummary? Summary(string path, ContextSource agent, long length)
    {
        // Re-summarise only when the file grew a lot: the title and folder rarely change
        if (_summaries.TryGetValue(path, out var cached) && length - cached.Length < 256 * 1024) return cached.Summary;
        var summary = SessionHistoryReader.Summarise(path, agent);
        _summaries[path] = (summary, length);
        return summary;
    }

    private bool NewProjectNeeded(List<LiveTranscript> live) =>
        live.Any(l => WorldSnapshotBuilder.Locate(_projects, l.Folder) is null && Directory.Exists(l.Folder));

    private void UpdateLive(WorldUiState ui, List<LiveTranscript> live)
    {
        var projects = _projects;
        var sessions = new List<WorldLiveSession>();
        foreach (var l in live)
        {
            if (WorldSnapshotBuilder.Locate(projects, l.Folder) is not { } located) continue;
            var pane = ui.Panes.FirstOrDefault(p => string.Equals(p.TranscriptPath, l.Path, StringComparison.OrdinalIgnoreCase))?.Title;
            sessions.Add(new WorldLiveSession(l.Path, WorldSnapshotBuilder.AgentName(l.Agent), l.Title, located.Project.Id,
                TimelineSource.NormalisePath(l.Folder), BranchOf(l.Folder), l.LastActivity, pane));
        }
        var orchestrators = ui.Orchestrators.Select(o => new WorldOrchestrator(o.Id, o.Title, o.WorkerTranscript, o.State, o.Step)).ToList();
        // Rewrite world.json only when what is drawn changes, not for every activity time
        var signature = string.Join("|", sessions.Select(s => $"{s.Id}>{s.Project}>{s.Branch}>{s.Pane}>{s.Title}"))
                        + "#" + string.Join("|", orchestrators.Select(o => $"{o.Id}>{o.Worker}>{o.State}>{o.Step}"));
        _live = sessions;
        _orchestrators = orchestrators;
        if (signature == _lastLiveSignature) return;
        _lastLiveSignature = signature;
        WriteWorld();
    }

    private string? BranchOf(string folder)
    {
        if (_branches.TryGetValue(folder, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(30)) return cached.Branch;
        string? branch = null;
        try
        {
            var head = Path.Combine(GitDir(folder) ?? "", "HEAD");
            if (File.Exists(head) && File.ReadAllText(head).Trim() is { } text && text.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                branch = text["ref: refs/heads/".Length..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _branches[folder] = (branch, DateTimeOffset.UtcNow);
        return branch;
    }

    /// <summary>The git directory for a folder inside a working tree: <c>.git</c> itself, or where a worktree's <c>.git</c> file points.</summary>
    private static string? GitDir(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git)) return git;
            if (File.Exists(git))
            {
                var line = File.ReadAllText(git).Trim();
                if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var target = line["gitdir:".Length..].Trim();
                    return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(dir.FullName, target));
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- events

    private void TailTranscripts()
    {
        foreach (var session in _live)
        {
            var codex = session.Agent == "codex";
            foreach (var line in _tail.Read(session.Id))
                foreach (var action in codex ? TranscriptActions.ParseCodexLine(line) : TranscriptActions.ParseClaudeLine(line))
                {
                    string? projectId = session.Project, file = null;
                    if (action.Path is { Length: > 0 } path)
                    {
                        var full = Path.IsPathRooted(path) ? path : Path.Combine(session.Folder, path);
                        if (WorldSnapshotBuilder.Locate(_projects, full) is { } located)
                        {
                            projectId = located.Project.Id;
                            file = located.Relative.Length > 0 ? located.Relative : null;
                        }
                    }
                    Emit(session.Id, action.Kind, projectId, file, action.Text, time: action.Time);
                    if (action.Kind is "commit" or "merge" or "branch" or "push") { _dirtyGit[projectId] = 0; _structureWanted = true; }
                    else if (action.Kind is "write" && file is not null) { _dirtyFiles[projectId] = 0; _structureWanted = true; }
                }
        }
    }

    private void Emit(string session, string kind, string? project, string? file, string? text, string? orchestrator = null, DateTimeOffset? time = null)
    {
        lock (_eventGate)
        {
            if (_events is null) return;
            var e = new WorldEvent(++_seq, time ?? DateTimeOffset.UtcNow, session, kind, project, file, text, orchestrator);
            try { _events.WriteLine(WorldStore.Serialize(e)); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Problem = ex.Message; }
        }
    }

    /// <summary>Events files from earlier runs: keep the newest few, so the folder does not grow forever.</summary>
    private void PruneOldEvents()
    {
        try
        {
            foreach (var old in new DirectoryInfo(_dir).GetFiles("events-*.ndjson").OrderByDescending(f => f.Name).Skip(4))
                old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_eventGate)
        {
            _events?.Dispose();
            _events = null;
        }
    }
}

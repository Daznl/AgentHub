using System.IO;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// The AgentHub repository's <see cref="Branch"/>: Daniel's own workflows (<see cref="WorkflowsFolder"/>) and the record
/// of every orchestrator run (<see cref="RunsFolder"/>). AgentHub commits there through a worktree of its own, so the
/// branch Daniel works on and his working tree are never touched. Nothing is pushed: the branch goes up when Daniel
/// pushes, and a clone gets it with <c>git fetch origin agenthub-workflows</c>.
/// </summary>
public sealed class AgentHubArchive
{
    public const string Branch = "agenthub-workflows";
    public const string RunsFolder = "runs";
    public const string WorkflowsFolder = "workflows";

    /// <summary>Files of a run that go into the repository. Worker transcripts are never among them.</summary>
    public static readonly string[] PublishedRunFiles = ["REVIEW.md", "run.json", "events.jsonl", "orchestrator.jsonl", "workflow.md"];

    private readonly ProcessRunner _runner;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string RepositoryPath { get; }
    public string WorktreePath { get; }

    public AgentHubArchive(ProcessRunner runner, string repositoryPath, string worktreePath)
    {
        _runner = runner;
        RepositoryPath = repositoryPath;
        WorktreePath = worktreePath;
    }

    public string RunsPath => Path.Combine(WorktreePath, RunsFolder);
    public string WorkflowsPath => Path.Combine(WorktreePath, WorkflowsFolder);

    private bool HasRepository => Directory.Exists(Path.Combine(RepositoryPath, ".git")) || File.Exists(Path.Combine(RepositoryPath, ".git"));

    /// <summary>Publishes one finished run folder. Returns a line for the log.</summary>
    public async Task<string> PublishRunAsync(string runFolder, CancellationToken cancellationToken = default)
    {
        if (!HasRepository) return $"Run kept locally only: no AgentHub repository at {RepositoryPath}.";
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureCoreAsync(cancellationToken);
            var runId = Path.GetFileName(runFolder.TrimEnd('\\', '/'));
            var target = Path.Combine(RunsPath, runId);
            Directory.CreateDirectory(target);
            foreach (var name in PublishedRunFiles)
            {
                var source = Path.Combine(runFolder, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(target, name), overwrite: true);
            }

            var records = Directory.EnumerateDirectories(RunsPath)
                .Select(d => RunRecorder.ReadRecord(Path.Combine(d, "run.json")))
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();
            await File.WriteAllTextAsync(Path.Combine(RunsPath, "INDEX.md"), RunReview.RenderIndex(records), cancellationToken);

            var record = records.FirstOrDefault(r => r.RunId == runId);
            var committed = await CommitAsync(RunsFolder, $"Orchestrator run {runId}: {record?.IdeaTitle ?? runId} ({record?.Outcome ?? "finished"})", cancellationToken);
            return committed ? $"Run committed to the '{Branch}' branch of {RepositoryPath} (not pushed)." : $"Run {runId} was already in the archive.";
        }
        finally { _lock.Release(); }
    }

    public async Task<string> SaveWorkflowAsync(string fileName, string text, string message, CancellationToken cancellationToken = default)
    {
        if (!HasRepository) throw new InvalidOperationException($"No AgentHub repository at {RepositoryPath}.");
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureCoreAsync(cancellationToken);
            Directory.CreateDirectory(WorkflowsPath);
            await File.WriteAllTextAsync(Path.Combine(WorkflowsPath, fileName), text, cancellationToken);
            var committed = await CommitAsync(WorkflowsFolder, message, cancellationToken);
            return committed ? $"Saved and committed to the '{Branch}' branch (not pushed)." : "No changes to save.";
        }
        finally { _lock.Release(); }
    }

    public async Task<string> DeleteWorkflowAsync(string fileName, string message, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureCoreAsync(cancellationToken);
            var path = Path.Combine(WorkflowsPath, fileName);
            if (File.Exists(path)) File.Delete(path);
            var committed = await CommitAsync(WorkflowsFolder, message, cancellationToken);
            return committed ? $"Deleted; committed to the '{Branch}' branch (not pushed)." : "Deleted.";
        }
        finally { _lock.Release(); }
    }

    /// <summary>Makes sure the worktree exists (creating the branch on first use). No-op without a repository.</summary>
    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!HasRepository) return;
        await _lock.WaitAsync(cancellationToken);
        try { await EnsureCoreAsync(cancellationToken); }
        finally { _lock.Release(); }
    }

    private async Task<bool> CommitAsync(string folder, string message, CancellationToken ct)
    {
        await GitAsync(ct, "add", "-A", "--", folder);
        var status = await GitAsync(ct, "status", "--porcelain", "--", folder);
        if (string.IsNullOrWhiteSpace(status.StdOut)) return false;
        var commit = await GitAsync(ct, "commit", "-q", "-m", message, "--", folder);
        if (commit.ExitCode != 0) throw new InvalidOperationException($"git commit failed: {commit.StdErr.Trim()}");
        return true;
    }

    private async Task EnsureCoreAsync(CancellationToken ct)
    {
        if (File.Exists(Path.Combine(WorktreePath, ".git")))
        {
            var check = await RunGitAsync(WorktreePath, ct, "rev-parse", "--abbrev-ref", "HEAD");
            if (check.ExitCode == 0 && check.StdOut.Trim() == Branch) return;
            throw new InvalidOperationException($"{WorktreePath} is not a worktree of the '{Branch}' branch; move it aside.");
        }

        // A stale registration (folder deleted by hand) would block "worktree add".
        await RunGitAsync(RepositoryPath, ct, "worktree", "prune");

        if (Directory.Exists(WorktreePath) && Directory.EnumerateFileSystemEntries(WorktreePath).Any())
            throw new InvalidOperationException($"{WorktreePath} exists and is not empty; move it aside so the archive can use it.");
        Directory.CreateDirectory(Path.GetDirectoryName(WorktreePath)!);

        (int ExitCode, string StdOut, string StdErr) add;
        if ((await RunGitAsync(RepositoryPath, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{Branch}")).ExitCode == 0)
            add = await RunGitAsync(RepositoryPath, ct, "worktree", "add", WorktreePath, Branch);
        else if ((await RunGitAsync(RepositoryPath, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{Branch}")).ExitCode == 0)
            add = await RunGitAsync(RepositoryPath, ct, "worktree", "add", "--track", "-b", Branch, WorktreePath, $"origin/{Branch}");
        else
        {
            add = await RunGitAsync(RepositoryPath, ct, "worktree", "add", "--orphan", "-b", Branch, WorktreePath);
            if (add.ExitCode == 0)
            {
                await File.WriteAllTextAsync(Path.Combine(WorktreePath, "README.md"), ReadMe, ct);
                await RunGitAsync(WorktreePath, ct, "add", "README.md");
                await RunGitAsync(WorktreePath, ct, "commit", "-q", "-m", "AgentHub workflows and run archive", "--", "README.md");
            }
        }
        if (add.ExitCode != 0) throw new InvalidOperationException($"Could not set up the archive worktree: {add.StdErr.Trim()}");
    }

    public const string ReadMe =
        "# AgentHub workflows and orchestrator runs\n\n" +
        "This branch holds only AgentHub's own data:\n\n" +
        "- `workflows/`: Daniel's orchestrator workflows (copies of the built-in templates, edited for a project or pipeline). " +
        "Pick one per idea in AgentHub's Ideas window.\n" +
        "- `runs/`: the record of every orchestrator run, one folder each. Start with `runs/INDEX.md`, then a run's `REVIEW.md` " +
        "(the run turn by turn), `run.json` (facts), `workflow.md` (the workflow exactly as it ran), `events.jsonl` (what AgentHub " +
        "sent and did) and `orchestrator.jsonl` (the orchestrator's Claude Code transcript). Worker transcripts are not kept here.\n\n" +
        "AgentHub commits here through its own worktree; it never pushes. Get it in a clone with `git fetch origin agenthub-workflows` " +
        "and `git worktree add ../agenthub-workflows agenthub-workflows`.\n";

    private Task<(int ExitCode, string StdOut, string StdErr)> GitAsync(CancellationToken ct, params string[] args) =>
        RunGitAsync(WorktreePath, ct, args);

    private Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(string dir, CancellationToken ct, params string[] args) =>
        _runner.RunAsync("git", args, dir, ct, TimeSpan.FromSeconds(60));
}

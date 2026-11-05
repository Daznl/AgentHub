using System.IO;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// Copies finished runs into the AgentHub repository on a dedicated <see cref="Branch"/> and commits them there, through
/// a worktree of its own, so the branch Daniel works on and his working tree are never touched. Nothing is pushed:
/// the branch goes up when Daniel pushes. A clone gets the runs with <c>git fetch origin orchestrator-runs</c>.
/// </summary>
public sealed class RunArchivePublisher
{
    public const string Branch = "orchestrator-runs";
    public const string RunsFolder = "orchestrator-runs";

    /// <summary>Files of a run that go into the repository. Worker transcripts are never among them.</summary>
    public static readonly string[] PublishedFiles = ["REVIEW.md", "run.json", "events.jsonl", "orchestrator.jsonl"];

    private readonly ProcessRunner _runner;

    public string RepositoryPath { get; }
    public string WorktreePath { get; }

    public RunArchivePublisher(ProcessRunner runner, string repositoryPath, string worktreePath)
    {
        _runner = runner;
        RepositoryPath = repositoryPath;
        WorktreePath = worktreePath;
    }

    public string RunsPath => Path.Combine(WorktreePath, RunsFolder);

    /// <summary>Publishes one finished run folder. Returns a line for the log.</summary>
    public async Task<string> PublishAsync(string runFolder, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Path.Combine(RepositoryPath, ".git")) && !File.Exists(Path.Combine(RepositoryPath, ".git")))
            return $"Run kept locally only: no AgentHub repository at {RepositoryPath}.";

        await EnsureWorktreeAsync(cancellationToken);

        var runId = Path.GetFileName(runFolder.TrimEnd('\\', '/'));
        var target = Path.Combine(RunsPath, runId);
        Directory.CreateDirectory(target);
        foreach (var name in PublishedFiles)
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

        await GitAsync(cancellationToken, "add", "--", RunsFolder);
        var status = await GitAsync(cancellationToken, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status.StdOut)) return $"Run {runId} was already in the archive.";

        var record = records.FirstOrDefault(r => r.RunId == runId);
        var message = $"Orchestrator run {runId}: {record?.IdeaTitle ?? runId} ({record?.Outcome ?? "finished"})";
        var commit = await GitAsync(cancellationToken, "commit", "-q", "-m", message);
        if (commit.ExitCode != 0) throw new InvalidOperationException($"git commit failed: {commit.StdErr.Trim()}");
        return $"Run committed to the '{Branch}' branch of {RepositoryPath} (not pushed).";
    }

    private async Task EnsureWorktreeAsync(CancellationToken ct)
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
            throw new InvalidOperationException($"{WorktreePath} exists and is not empty; move it aside so the run archive can use it.");
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
            }
        }
        if (add.ExitCode != 0) throw new InvalidOperationException($"Could not set up the run archive worktree: {add.StdErr.Trim()}");
    }

    public const string ReadMe =
        "# AgentHub orchestrator runs\n\n" +
        "This branch holds only the record of AgentHub's orchestrator runs, one folder per run under `orchestrator-runs/`, " +
        "so they can be reviewed and the orchestrator improved. Start with `orchestrator-runs/INDEX.md`, then a run's `REVIEW.md`.\n\n" +
        "Each run: `REVIEW.md` (the story, turn by turn), `run.json` (facts), `events.jsonl` (what AgentHub sent and did), " +
        "`orchestrator.jsonl` (the orchestrator's Claude Code transcript). Worker transcripts are not kept here.\n\n" +
        "AgentHub commits here through its own worktree; it never pushes. Get it in a clone with `git fetch origin orchestrator-runs` " +
        "and `git worktree add ../agenthub-runs orchestrator-runs`.\n";

    private Task<(int ExitCode, string StdOut, string StdErr)> GitAsync(CancellationToken ct, params string[] args) =>
        RunGitAsync(WorktreePath, ct, args);

    private Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(string dir, CancellationToken ct, params string[] args) =>
        _runner.RunAsync("git", args, dir, ct, TimeSpan.FromSeconds(60));
}

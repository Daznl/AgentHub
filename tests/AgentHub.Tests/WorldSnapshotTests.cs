using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentHub.Hosting;
using AgentHub.Services;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Timeline;
using AgentHub.Services.World;
using Xunit;

namespace AgentHub.Tests;

public class WorldSnapshotTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double hours) => Day.AddHours(hours);
    private static string Full(string hash) => hash.PadRight(40, '0');

    private static TimelineCommit Commit(string hash, double hours, string subject, params string[] parents) =>
        new(Full(hash), parents.Select(Full).ToList(), At(hours), "Ada", subject);

    private static readonly TimelineScope Scope = new(@"C:\repos\My Repo", "My Repo", [@"C:\repos\My Repo", @"C:\data\wt\feature x"], IsGit: true);

    // main: a -- b ------- m (merge of feature/x)
    //               \- c -/
    private static TimelineModel Model(out TimelineSessionInput session)
    {
        var commits = new List<TimelineCommit>
        {
            Commit("mmmmmmm", 5, "Merge pull request #3 from someone/feature/x", "bbbbbbb", "ccccccc"),
            Commit("ccccccc", 3, "feat: x", "bbbbbbb"),
            Commit("bbbbbbb", 1, "second", "aaaaaaa"),
            Commit("aaaaaaa", 0, "first"),
        };
        session = new TimelineSessionInput(
            new SessionSummary(@"C:\sessions\s1.jsonl", ContextSource.ClaudeCode, "Build feature x", null, At(2.5), At(3.5), 100),
            At(3.5), new HashSet<string>(StringComparer.Ordinal), @"C:\data\wt\feature x");
        return TimelineBuilder.Build([session], commits, [new TimelineBranchTip(Full("mmmmmmm"), "main", false)], At(24));
    }

    [Fact]
    public void Build_carries_rows_branches_commits_and_sessions_from_the_timeline()
    {
        var model = Model(out _);
        var pr = new PullRequestFact(3, "Feature x", "feature/x", "main", "ada", "bob", At(5), Full("mmmmmmm"), 1, false, "MERGED");
        var worktrees = new List<WorldWorktree> { new(@"C:\repos\My Repo", "main", Full("mmmmmmm"), true) };
        var snapshot = WorldSnapshotBuilder.Build(Scope, model, [pr], worktrees, [], At(24));

        Assert.Equal(WorldSnapshot.CurrentSchema, snapshot.Schema);
        Assert.Equal("main", snapshot.Repo.Trunk);
        Assert.Equal(model.Rows.Count, snapshot.Rows.Count);
        Assert.Equal(4, snapshot.Commits.Count);
        Assert.All(snapshot.Commits, c => Assert.InRange(c.Row, 0, snapshot.Rows.Count - 1));

        var feature = Assert.Single(snapshot.Branches, b => !b.IsTrunk);
        Assert.Equal("feature/x", feature.Name);
        Assert.True(feature.IsMerged);
        Assert.Equal(Full("mmmmmmm"), feature.MergeHash);
        Assert.Equal(TimelineBranch.TrunkKey, feature.Parent);

        var session = Assert.Single(snapshot.Sessions);
        Assert.Equal("claude", session.Agent);
        Assert.Equal("Build feature x", session.Title);
        Assert.Contains(Full("ccccccc"), session.Commits);
        Assert.Equal(feature.Key, session.Branch);
        Assert.Equal(@"C:\sessions\s1.jsonl", snapshot.Commits.Single(c => c.Hash == Full("ccccccc")).Session);

        Assert.Equal("MERGED", Assert.Single(snapshot.PullRequests).State);
    }

    [Fact]
    public void The_file_is_camel_case_json_that_round_trips_through_a_replace()
    {
        var snapshot = WorldSnapshotBuilder.Build(Scope, Model(out _), null, [], [], At(24));
        var dir = Path.Combine(Path.GetTempPath(), "agenthub world " + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var path = WorldFile.PathFor(Scope, dir);
            Assert.Contains("My-Repo-", path);
            WorldFile.Write(path, snapshot);
            WorldFile.Write(path, snapshot); // the second write replaces the first
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("schema").GetInt32());
            Assert.Equal("My Repo", root.GetProperty("repo").GetProperty("name").GetString());
            Assert.True(root.GetProperty("branches")[0].TryGetProperty("isTrunk", out _));
            Assert.Equal(JsonValueKind.Array, root.GetProperty("pullRequests").ValueKind);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Path_is_stable_per_root_and_differs_between_roots()
    {
        var other = Scope with { Root = @"C:\elsewhere\My Repo" };
        Assert.Equal(WorldFile.PathFor(Scope, @"C:\w"), WorldFile.PathFor(Scope with { Root = @"C:\REPOS\My Repo" }, @"C:\w"));
        Assert.NotEqual(WorldFile.PathFor(Scope, @"C:\w"), WorldFile.PathFor(other, @"C:\w"));
    }

    [Fact]
    public void ParseWorktrees_reads_branches_detached_heads_and_the_main_tree()
    {
        const string porcelain = "worktree C:/repos/My Repo\nHEAD abc123\nbranch refs/heads/main\n\n" +
                                 "worktree C:/data/wt/feature x\nHEAD def456\nbranch refs/heads/feature/x\n\n" +
                                 "worktree C:/data/wt/old\nHEAD 999999\ndetached\n";
        var list = WorldSnapshotBuilder.ParseWorktrees(porcelain);
        Assert.Equal(3, list.Count);
        Assert.True(list[0].IsMain);
        Assert.Equal(@"C:\repos\My Repo", list[0].Path);
        Assert.Equal("feature/x", list[1].Branch);
        Assert.False(list[1].IsMain);
        Assert.Null(list[2].Branch);
        Assert.Equal("999999", list[2].Head);
    }

    [Fact]
    public void PanesIn_keeps_sessions_in_the_repository_folders_only()
    {
        SessionRecord Rec(string id, string folder) => new() { Id = id, WorkingDirectory = folder, Title = id, IsAgent = true, CommandLine = "claude" };
        var panes = WorldSnapshotBuilder.PanesIn(Scope,
        [
            Rec("in-main", @"C:\repos\My Repo"),
            Rec("in-sub", @"C:\repos\My Repo\src"),
            Rec("in-worktree", @"C:\data\wt\feature x"),
            Rec("lookalike", @"C:\repos\My Repo 2"),
            Rec("elsewhere", @"C:\other"),
        ]);
        Assert.Equal(["in-main", "in-sub", "in-worktree"], panes.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void GodotLauncher_resolves_a_full_path_and_refuses_a_missing_one()
    {
        var self = Environment.ProcessPath!;
        Assert.Equal(self, GodotLauncher.Resolve(self));
        Assert.Null(GodotLauncher.Resolve(@"C:\no such folder\godot.exe"));
        Assert.Null(GodotLauncher.Resolve("no-such-godot-command-" + Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// Starts the real Godot (setting AGENTHUB_WORLD_LAUNCH=1, Godot on PATH) on the bundled sample, from the build
    /// output, through a world file in a folder whose path has spaces; it must still run after a few seconds.
    /// </summary>
    [Fact]
    public async Task Live_launch_of_godot_on_a_world_file()
    {
        if (Environment.GetEnvironmentVariable("AGENTHUB_WORLD_LAUNCH") != "1") return;
        var godot = GodotLauncher.Resolve("godot");
        Assert.NotNull(godot);
        var dir = Path.Combine(@"C:\data\agenthub\uitest", "world launch " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var world = Path.Combine(dir, "world.json");
        File.Copy(Path.Combine(GodotLauncher.ProjectDir, "sample", "world.json"), world);
        var process = GodotLauncher.Launch(godot!, world);
        string Log()
        {
            using var reader = new StreamReader(new FileStream(Path.Combine(dir, "godot.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.False(process.HasExited, "Godot exited early: " + Log());
            Assert.Contains("Godot Engine", Log());
            Assert.DoesNotContain("ERROR", Log());
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Writes the real world file for a repository: set AGENTHUB_WORLD_EXPORT to its folder (and optionally
    /// AGENTHUB_WORLD_DIR). A tool for working on the Godot side, skipped otherwise.
    /// </summary>
    [Fact]
    public async Task Live_export_of_a_real_repository()
    {
        var folder = Environment.GetEnvironmentVariable("AGENTHUB_WORLD_EXPORT");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var runner = new ProcessRunner();
        var scope = await TimelineSource.ResolveAsync(runner, folder);
        var snapshot = await WorldSource.ReadAsync(runner, scope, Environment.GetEnvironmentVariable("AGENTHUB_WORLD_ARCHIVE"));
        var path = WorldFile.PathFor(scope);
        WorldFile.Write(path, snapshot);
        Assert.NotEmpty(snapshot.Commits);
        Console.WriteLine($"WORLD {path}: {snapshot.Branches.Count} branches, {snapshot.Commits.Count} commits, {snapshot.Sessions.Count} sessions, {snapshot.Panes.Count} panes");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
    private static TimelineModel Model()
    {
        var commits = new List<TimelineCommit>
        {
            Commit("mmmmmmm", 5, "Merge pull request #3 from someone/feature/x", "bbbbbbb", "ccccccc"),
            Commit("ccccccc", 3, "feat: x", "bbbbbbb"),
            Commit("bbbbbbb", 1, "second", "aaaaaaa"),
            Commit("aaaaaaa", 0, "first"),
        };
        var session = new TimelineSessionInput(
            new SessionSummary(@"C:\sessions\s1.jsonl", ContextSource.ClaudeCode, "Build feature x", null, At(2.5), At(3.5), 100),
            At(3.5), new HashSet<string>(StringComparer.Ordinal), @"C:\data\wt\feature x");
        return TimelineBuilder.Build([session], commits, [new TimelineBranchTip(Full("mmmmmmm"), "main", false)], At(24));
    }

    private static WorldProject Project(IReadOnlyList<WorldFileEntry>? files = null) =>
        WorldSnapshotBuilder.BuildProject(Scope, Model(),
            [new PullRequestFact(3, "Feature x", "feature/x", "main", "ada", "bob", At(5), Full("mmmmmmm"), 1, false, "MERGED")],
            [new WorldWorktree(@"C:\repos\My Repo", "main", Full("mmmmmmm"), true)], files ?? [new("src/a.cs", 10)], false);

    [Fact]
    public void BuildProject_carries_rows_branches_commits_and_sessions_from_the_timeline()
    {
        var project = Project();
        Assert.Equal("main", project.Trunk);
        Assert.Equal(WorldSnapshotBuilder.ProjectId(@"C:\repos\My Repo"), project.Id);
        Assert.Equal(4, project.Commits.Count);
        Assert.All(project.Commits, c => Assert.InRange(c.Row, 0, project.Rows.Count - 1));

        var feature = Assert.Single(project.Branches, b => !b.IsTrunk);
        Assert.Equal("feature/x", feature.Name);
        Assert.True(feature.IsMerged);
        Assert.Equal(Full("mmmmmmm"), feature.MergeHash);
        Assert.Equal(TimelineBranch.TrunkKey, feature.Parent);

        var session = Assert.Single(project.Sessions);
        Assert.Equal("claude", session.Agent);
        Assert.Contains(Full("ccccccc"), session.Commits);
        Assert.Equal(feature.Key, session.Branch);
        Assert.Equal("MERGED", Assert.Single(project.PullRequests).State);
    }

    [Fact]
    public void The_world_file_is_camel_case_json_written_through_a_replace()
    {
        var snapshot = new WorldSnapshot(WorldSnapshot.CurrentSchema, At(24), "events-x.ndjson", [Project()],
            [new WorldLiveSession(@"C:\t\s.jsonl", "claude", "t", "p", @"C:\repos\My Repo", "main", At(24), "Terminal 1")],
            [new WorldOrchestrator("Terminal 1", "Orchestrator", @"C:\t\s.jsonl", "watching", null)]);
        var dir = Path.Combine(Path.GetTempPath(), "agenthub world " + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var path = WorldStore.WorldPath(dir);
            WorldStore.Write(path, snapshot);
            WorldStore.Write(path, snapshot);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("schema").GetInt32());
            Assert.Equal("events-x.ndjson", root.GetProperty("eventsFile").GetString());
            var project = root.GetProperty("projects")[0];
            Assert.Equal("My Repo", project.GetProperty("name").GetString());
            Assert.Equal("src/a.cs", project.GetProperty("files")[0].GetProperty("path").GetString());
            Assert.Equal("Terminal 1", root.GetProperty("live")[0].GetProperty("pane").GetString());
            Assert.Equal("watching", root.GetProperty("orchestrators")[0].GetProperty("state").GetString());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProjectId_is_stable_per_root_whatever_the_case()
    {
        Assert.Equal(WorldSnapshotBuilder.ProjectId(@"C:\repos\My Repo"), WorldSnapshotBuilder.ProjectId(@"c:\REPOS\my repo\"));
        Assert.NotEqual(WorldSnapshotBuilder.ProjectId(@"C:\repos\My Repo"), WorldSnapshotBuilder.ProjectId(@"C:\repos\Other"));
    }

    [Fact]
    public void Locate_finds_the_project_and_relative_path_through_worktrees()
    {
        var project = Project();
        var other = project with { Id = "other", Folders = [@"C:\repos\My Repo 2"] };
        var hit = WorldSnapshotBuilder.Locate([project, other], @"C:\data\wt\feature x\src\b.cs");
        Assert.NotNull(hit);
        Assert.Equal(project.Id, hit!.Value.Project.Id);
        Assert.Equal("src/b.cs", hit.Value.Relative);
        Assert.Equal("other", WorldSnapshotBuilder.Locate([project, other], @"C:\repos\My Repo 2\x.md")!.Value.Project.Id);
        Assert.Equal("", WorldSnapshotBuilder.Locate([project], @"C:\repos\My Repo")!.Value.Relative);
        Assert.Null(WorldSnapshotBuilder.Locate([project], @"C:\elsewhere\x.md"));
    }

    [Fact]
    public void ParseFiles_sizes_files_skips_deleted_ones_and_keeps_the_largest_when_capped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenthub files " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "src", "big.cs"), new string('x', 500));
            File.WriteAllText(Path.Combine(dir, "small.md"), "hi");
            File.WriteAllText(Path.Combine(dir, "mid.json"), new string('y', 50));
            var listing = "src/big.cs\0small.md\0mid.json\0gone.txt\0";
            var (all, cut) = WorldSnapshotBuilder.ParseFiles(listing, dir, 10);
            Assert.False(cut);
            Assert.Equal(["mid.json", "small.md", "src/big.cs"], all.Select(f => f.Path).ToArray());
            Assert.Equal(500, all.Single(f => f.Path == "src/big.cs").Size);
            var (two, truncated) = WorldSnapshotBuilder.ParseFiles(listing, dir, 2);
            Assert.True(truncated);
            Assert.Equal(["mid.json", "src/big.cs"], two.Select(f => f.Path).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
        Assert.Null(list[2].Branch);
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
    /// Starts the real Godot (AGENTHUB_WORLD_LAUNCH=1, Godot on PATH) on the bundled sample, from the build output,
    /// through a world file in a folder whose path has spaces; it must still run, error-free, after a few seconds.
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
        foreach (var name in new[] { "world.json", "events-sample.ndjson" })
            File.Copy(Path.Combine(GodotLauncher.ProjectDir, "sample", name), Path.Combine(dir, name));
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
    /// The service against this computer's real repositories and transcripts (AGENTHUB_WORLD_EXPORT = a repository folder,
    /// optionally AGENTHUB_WORLD_DIR): it writes world.json with the project and its files, and an events file.
    /// </summary>
    [Fact]
    public async Task Live_world_service_writes_the_world_and_events()
    {
        var folder = Environment.GetEnvironmentVariable("AGENTHUB_WORLD_EXPORT");
        if (string.IsNullOrWhiteSpace(folder)) return;
        using var service = new WorldService(new ProcessRunner(),
            () => Task.FromResult(new WorldUiState([folder], [], [])), Environment.GetEnvironmentVariable("AGENTHUB_WORLD_ARCHIVE"));
        var started = DateTime.UtcNow;
        service.Start();
        // world.json comes at once with no projects, then gains each project as it is read
        JsonDocument? doc = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(150))
        {
            await Task.Delay(1000);
            if (!File.Exists(service.WorldPath) || File.GetLastWriteTimeUtc(service.WorldPath) < started) continue;
            doc?.Dispose();
            try { doc = JsonDocument.Parse(File.ReadAllText(service.WorldPath)); }
            catch (IOException) { continue; }
            if (doc.RootElement.GetProperty("projects").GetArrayLength() > 0) break;
        }
        Assert.NotNull(doc);
        using var _ = doc;
        var projects = doc!.RootElement.GetProperty("projects");
        Assert.True(projects.GetArrayLength() >= 1, service.Problem);
        Assert.True(projects.EnumerateArray().Any(p => p.GetProperty("files").GetArrayLength() > 0));
        Console.WriteLine($"first project after {clock.Elapsed.TotalSeconds:0.0}s");
        // AGENTHUB_WORLD_HOLD=<seconds> keeps the service running, to draw it in Godot meanwhile
        if (int.TryParse(Environment.GetEnvironmentVariable("AGENTHUB_WORLD_HOLD"), out var hold)) await Task.Delay(TimeSpan.FromSeconds(hold));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(service.WorldPath)!, service.EventsFile)));
        Console.WriteLine($"WORLD {service.WorldPath}: {projects.GetArrayLength()} projects, {doc.RootElement.GetProperty("live").GetArrayLength()} live sessions");
    }
}

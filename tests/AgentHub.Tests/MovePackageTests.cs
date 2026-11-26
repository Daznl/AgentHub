using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentHub.Models;
using AgentHub.Services.Move;
using Xunit;

namespace AgentHub.Tests;

/// <summary>Packing AgentHub's data on one computer and unpacking it on another (Settings → Move to another computer).</summary>
public class MovePackageTests : IDisposable
{
    // Spaces on purpose: real profiles sit under "OneDrive - Company". Under C:\data, not %TEMP%: the owner can open it.
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("AGENTHUB_TEST_ROOT") is { Length: > 0 } r ? r : @"C:\data\agenthub\test-runs",
        "move package " + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private MoveLocations Computer(string name, string user)
    {
        var profile = Path.Combine(_root, name, "Users", user);
        var data = Path.Combine(_root, name, "data");
        return new MoveLocations
        {
            UserProfile = profile,
            SettingsDir = Path.Combine(profile, "AppData", "Roaming", "AgentHub"),
            ClaudeDir = Path.Combine(profile, ".claude"),
            CodexDir = Path.Combine(profile, ".codex"),
            HistoryArchiveDir = Path.Combine(data, "history"),
            WorkflowsDir = Path.Combine(data, "workflows"),
            RunsDir = Path.Combine(data, "runs"),
            PaneOrchestratorsDir = Path.Combine(data, "pane-orchestrators"),
            TimelineDir = Path.Combine(data, "timeline"),
        };
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Json(string path) => path.Replace(@"\", @"\\");

    private static string Line(string cwd, string text) =>
        $"{{\"type\":\"user\",\"cwd\":\"{Json(cwd)}\",\"message\":{{\"content\":\"{text}\"}}}}\n";

    // ---- paths ----

    [Fact]
    public void MapText_RewritesEscapedPlainAndForwardSlashPaths()
    {
        var mapper = new PathMapper([new PathMapping(@"C:\Users\old", @"C:\Users\new person")]);
        Assert.Equal(@"{""cwd"":""C:\\Users\\new person\\Repo""}", mapper.MapText(@"{""cwd"":""C:\\Users\\old\\Repo""}"));
        Assert.Equal(@"cd C:\Users\new person\Repo", mapper.MapText(@"cd C:\Users\old\Repo"));
        Assert.Equal("C:/Users/new person/Repo", mapper.MapText("C:/Users/old/Repo"));
        Assert.Equal(@"c:\users\NEW PERSON".ToLowerInvariant(), mapper.MapText(@"c:\users\OLD").ToLowerInvariant());
    }

    [Fact]
    public void MapText_MatchesWholeFoldersOnly()
    {
        var mapper = new PathMapper([new PathMapping(@"C:\Users\dan", @"C:\Users\x")]);
        Assert.Equal(@"C:\Users\daniel\Repo", mapper.MapText(@"C:\Users\daniel\Repo"));
        Assert.Equal(@"C:\Users\x\Repo and C:\Users\x", mapper.MapText(@"C:\Users\dan\Repo and C:\Users\dan"));
    }

    [Fact]
    public void MapText_NeverMapsAPathTwice()
    {
        // The new home sits inside the old one's name space: a second pass would map it again.
        var mapper = new PathMapper([
            new PathMapping(@"C:\Users\a", @"C:\Users\a\b"),
            new PathMapping(@"C:\Users\a\Repos", @"D:\Repos"),
        ]);
        Assert.Equal(@"D:\Repos\x C:\Users\a\b\y", mapper.MapText(@"C:\Users\a\Repos\x C:\Users\a\y"));
    }

    [Fact]
    public void MapEncodedFolder_RenamesClaudeProjectFoldersByWholeFolder()
    {
        var mapper = new PathMapper([new PathMapping(@"C:\Users\old.name", @"C:\Users\new")]);
        Assert.Equal("C--Users-new-OneDrive---IGO-Limited-Desktop", mapper.MapEncodedFolder("C--Users-old-name-OneDrive---IGO-Limited-Desktop"));
        Assert.Equal("C--Users-new", mapper.MapEncodedFolder("C--Users-old-name"));
        Assert.Equal("C--Users-old-names", mapper.MapEncodedFolder("C--Users-old-names"));
        Assert.Equal("C--data-agenthub", mapper.MapEncodedFolder("C--data-agenthub"));
    }

    [Fact]
    public void MapStream_KeepsLineEndingsAndLinesLongerThanTheBuffer()
    {
        var mapper = new PathMapper([new PathMapping(@"C:\Users\old", @"C:\Users\new")]);
        var longLine = new string('x', 200_000) + @"C:\\Users\\old\\f";
        var input = "a C:\\Users\\old\r\n" + longLine + "\nlast C:/Users/old";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var target = new MemoryStream();
        mapper.MapStream(source, target);
        Assert.Equal(input.Replace("old", "new"), Encoding.UTF8.GetString(target.ToArray()));
    }

    // ---- export ----

    [Fact]
    public async Task Export_LeavesOutSignInsKeysAndMachineState()
    {
        var from = Computer("old pc", "old");
        Write(Path.Combine(from.ClaudeDir, "CLAUDE.md"), "rules");
        Write(Path.Combine(from.ClaudeDir, ".credentials.json"), "{\"token\":\"secret\"}");
        Write(Path.Combine(from.ClaudeDir, "plugins", "x", "id.pem"), "key");
        Write(Path.Combine(from.ClaudeDir, "cache", "c.json"), "{}");
        Write(Path.Combine(from.ClaudeDir, "policy-limits.json"), "{}");
        Write(Path.Combine(from.ClaudeDir, "skills", "s", "SKILL.md"), "skill");
        Write(Path.Combine(from.CodexDir, "auth.json"), "{\"key\":\"secret\"}");
        Write(Path.Combine(from.CodexDir, "config.toml"), "model = \"x\"");
        Write(Path.Combine(from.SettingsDir, "crash.log"), "crash");
        Write(Path.Combine(from.SettingsDir, "settings.json"), "{}");

        var zip = Path.Combine(_root, "out", "move.zip");
        await MoveExporter.ExportAsync(zip, MoveExporter.Scan(from), from, new AppSettings());

        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("items/claude/CLAUDE.md", names);
        Assert.Contains("items/claude/skills/s/SKILL.md", names);
        Assert.Contains("items/codex/config.toml", names);
        Assert.Contains("items/agenthub/settings.json", names);
        Assert.DoesNotContain(names, n => n.Contains("credentials") || n.EndsWith("auth.json") || n.EndsWith(".pem"));
        Assert.DoesNotContain(names, n => n.Contains("/cache/") || n.Contains("policy-limits") || n.EndsWith("crash.log"));
        Assert.False(File.Exists(zip + ".partial"));
    }

    [Fact]
    public async Task Export_PacksOnlyArchivedSessionsTheLiveFolderNoLongerCovers()
    {
        var from = Computer("old pc", "old");
        var project = "C--Work-Repo";
        Write(Path.Combine(from.ClaudeDir, "projects", project, "live.jsonl"), "a\nb\n");
        Write(Path.Combine(from.HistoryArchiveDir, "claude", project, "live.jsonl"), "a\n");
        Write(Path.Combine(from.HistoryArchiveDir, "claude", project, "deleted.jsonl"), "gone from Claude Code\n");

        var plans = MoveExporter.Scan(from);
        var archived = plans.Single(p => p.Item.Key == "history-claude").Files.Select(f => f.Relative).ToList();
        Assert.Equal([Path.Combine(project, "deleted.jsonl")], archived);
    }

    [Fact]
    public async Task Export_Cancelled_LeavesNoZip()
    {
        var from = Computer("old pc", "old");
        Write(Path.Combine(from.ClaudeDir, "CLAUDE.md"), "rules");
        var zip = Path.Combine(_root, "out", "cancelled.zip");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MoveExporter.ExportAsync(zip, MoveExporter.Scan(from), from, new AppSettings(), cancel: cancel.Token));
        Assert.False(File.Exists(zip));
        Assert.False(File.Exists(zip + ".partial"));
    }

    // ---- round trip ----

    [Fact]
    public async Task RoundTrip_ToAnotherUser_RenamesSessionFoldersAndRewritesPaths()
    {
        var from = Computer("old pc", "old");
        var to = Computer("new pc", "new");
        var repo = Path.Combine(from.UserProfile, "OneDrive - IGO", "Repo");
        var encoded = AgentHub.Services.Context.SessionContextReader.EncodeClaudeProjectName(repo);
        Write(Path.Combine(from.ClaudeDir, "projects", encoded, "s1.jsonl"), Line(repo, "hello") + Line(repo, "again"));
        Write(Path.Combine(from.ClaudeDir, "projects", encoded, "memory", "MEMORY.md"), $"- repo at {repo}\n");
        Write(Path.Combine(from.HistoryArchiveDir, "claude", encoded, "old.jsonl"), Line(repo, "archived only"));
        Write(Path.Combine(from.UserProfile, "TODO.md"), "- [ ] move computers\n");
        Write(Path.Combine(from.RunsDir, "r1", "run.json"), $"{{\"folder\":\"{Json(repo)}\"}}");
        var settings = new AppSettings
        {
            Repositories = [new RepositoryDefinition { Name = "Repo", LocalPath = repo, RemoteUrl = "https://github.com/o/Repo.git" }],
            PaneLayout = [new PaneLayoutEntry { SessionId = "host-1", Folder = repo, TranscriptPath = Path.Combine(from.ClaudeDir, "projects", encoded, "s1.jsonl") }],
        };
        Write(Path.Combine(from.SettingsDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(settings));

        var zip = Path.Combine(_root, "out", "move.zip");
        await MoveExporter.ExportAsync(zip, MoveExporter.Scan(from), from, settings);

        var manifest = MoveImporter.ReadManifest(zip);
        Assert.Equal(from.UserProfile, manifest.UserProfile);
        var mappings = MoveImporter.SuggestMappings(manifest, to);
        Assert.Contains(mappings, m => m.From == from.UserProfile && m.To == to.UserProfile);

        var result = await MoveImporter.ImportAsync(zip, new MoveImportOptions { Target = to, Mappings = mappings, ImportDir = Path.Combine(_root, "import") });

        var newRepo = Path.Combine(to.UserProfile, "OneDrive - IGO", "Repo");
        var newEncoded = AgentHub.Services.Context.SessionContextReader.EncodeClaudeProjectName(newRepo);
        var transcript = Path.Combine(to.ClaudeDir, "projects", newEncoded, "s1.jsonl");
        Assert.True(File.Exists(transcript), "transcript filed under the new folder's encoded name");
        Assert.Equal(Line(newRepo, "hello") + Line(newRepo, "again"), File.ReadAllText(transcript));
        Assert.Contains(newRepo, File.ReadAllText(Path.Combine(to.ClaudeDir, "projects", newEncoded, "memory", "MEMORY.md")));
        Assert.True(File.Exists(Path.Combine(to.HistoryArchiveDir, "claude", newEncoded, "old.jsonl")));
        Assert.Equal("- [ ] move computers\n", File.ReadAllText(Path.Combine(to.UserProfile, "TODO.md")));
        Assert.Contains(Json(newRepo), File.ReadAllText(Path.Combine(to.RunsDir, "r1", "run.json")));

        // settings.json waits for the next start; the live one is not touched
        Assert.True(result.SettingsPending);
        Assert.False(File.Exists(Path.Combine(to.SettingsDir, "settings.json")));
        var pending = File.ReadAllText(Path.Combine(to.SettingsDir, SettingsImport.PendingFileName));
        Assert.Contains(Json(newRepo), pending);
        Assert.DoesNotContain(Json(from.UserProfile), pending);

        var missing = Assert.Single(result.MissingRepositories);
        Assert.Equal(newRepo, missing.LocalPath);
        Assert.Equal("https://github.com/o/Repo.git", missing.RemoteUrl);
        Assert.True(File.Exists(result.ReportPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "import", "staging")));
    }

    [Fact]
    public async Task RoundTrip_SameLayout_MapsNothing()
    {
        var from = Computer("pc", "me");
        Write(Path.Combine(from.ClaudeDir, "projects", "C--Repo", "s.jsonl"), Line(@"C:\Repo", "x"));
        var zip = Path.Combine(_root, "out", "same.zip");
        await MoveExporter.ExportAsync(zip, MoveExporter.Scan(from), from, new AppSettings());
        Assert.Empty(MoveImporter.SuggestMappings(MoveImporter.ReadManifest(zip), from));
    }

    // ---- never overwrite ----

    [Fact]
    public async Task Import_NeverOverwrites_CompletesTranscriptsAndSetsDifferencesAside()
    {
        var from = Computer("old pc", "me");
        var to = Computer("new pc", "me");
        var p = "C--Repo";
        Write(Path.Combine(from.ClaudeDir, "projects", p, "grown.jsonl"), "1\n2\n3\n");
        Write(Path.Combine(from.ClaudeDir, "projects", p, "shorter.jsonl"), "1\n");
        Write(Path.Combine(from.ClaudeDir, "projects", p, "same.jsonl"), "same\n");
        Write(Path.Combine(from.ClaudeDir, "projects", p, "forked.jsonl"), "a\nb\n");
        Write(Path.Combine(from.ClaudeDir, "CLAUDE.md"), "old computer's rules");
        Write(Path.Combine(from.ClaudeDir, "agents", "new.md"), "agent");

        Write(Path.Combine(to.ClaudeDir, "projects", p, "grown.jsonl"), "1\n");
        Write(Path.Combine(to.ClaudeDir, "projects", p, "shorter.jsonl"), "1\n2\n");
        Write(Path.Combine(to.ClaudeDir, "projects", p, "same.jsonl"), "same\n");
        Write(Path.Combine(to.ClaudeDir, "projects", p, "forked.jsonl"), "a\nc\n");
        var rules = Write(Path.Combine(to.ClaudeDir, "CLAUDE.md"), "new computer's rules");

        var zip = Path.Combine(_root, "out", "merge.zip");
        await MoveExporter.ExportAsync(zip, MoveExporter.Scan(from), from, new AppSettings());
        var importDir = Path.Combine(_root, "import");
        var result = await MoveImporter.ImportAsync(zip, new MoveImportOptions { Target = to, ImportDir = importDir });

        Assert.Empty(result.Skipped);
        string Read(string name) => File.ReadAllText(Path.Combine(to.ClaudeDir, "projects", p, name));
        Assert.Equal("1\n2\n3\n", Read("grown.jsonl"));
        Assert.Equal("1\n2\n", Read("shorter.jsonl"));
        Assert.Equal("same\n", Read("same.jsonl"));
        Assert.Equal("a\nc\n", Read("forked.jsonl"));
        Assert.Equal("new computer's rules", File.ReadAllText(rules));
        Assert.Equal("agent", File.ReadAllText(Path.Combine(to.ClaudeDir, "agents", "new.md")));

        var projects = result.Counts["claude-projects"];
        Assert.Equal((1, 2, 1), (projects.Extended, projects.Same, projects.Conflicts));
        Assert.Equal(2, result.Conflicts.Count);
        var claudeMd = result.Conflicts.Single(c => c.TargetPath == rules);
        Assert.Equal("old computer's rules", File.ReadAllText(claudeMd.ImportedCopyPath));

        // Choosing the imported copy keeps this computer's in replaced\
        var backup = MoveImporter.UseImportedCopy(claudeMd, importDir);
        Assert.Equal("old computer's rules", File.ReadAllText(rules));
        Assert.Equal("new computer's rules", File.ReadAllText(backup));
    }

    [Fact]
    public async Task Import_RejectsEntriesThatEscapeTheirFolder()
    {
        var to = Computer("new pc", "me");
        var zip = Path.Combine(_root, "out", "evil.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var s = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                s.Write("{\"format\":\"agenthub-move\",\"version\":1}");
            using (var s = new StreamWriter(archive.CreateEntry("items/claude/../../escaped.txt").Open()))
                s.Write("x");
            using (var s = new StreamWriter(archive.CreateEntry("items/claude/.credentials.json").Open()))
                s.Write("{}");
        }

        var result = await MoveImporter.ImportAsync(zip, new MoveImportOptions { Target = to, ImportDir = Path.Combine(_root, "import") });
        Assert.Equal(2, result.Counts["claude"].Skipped);
        Assert.False(File.Exists(Path.Combine(to.UserProfile, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(to.ClaudeDir, ".credentials.json")));
    }

    [Fact]
    public void ReadManifest_RefusesOtherZipsAndNewerFormats()
    {
        var plain = Path.Combine(_root, "out", "plain.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        using (var archive = ZipFile.Open(plain, ZipArchiveMode.Create)) archive.CreateEntry("x.txt");
        Assert.Throws<InvalidDataException>(() => MoveImporter.ReadManifest(plain));

        var newer = Path.Combine(_root, "out", "newer.zip");
        using (var archive = ZipFile.Open(newer, ZipArchiveMode.Create))
        using (var s = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
            s.Write("{\"format\":\"agenthub-move\",\"version\":99}");
        Assert.Contains("newer AgentHub", Assert.Throws<InvalidDataException>(() => MoveImporter.ReadManifest(newer)).Message);
    }

    // ---- settings ----

    [Fact]
    public void SettingsMerge_AddsThisComputersReposAndPanes_KeepsItsFolders_DropsOldSessionIds()
    {
        var current = new AppSettings
        {
            HistoryArchiveDirectory = @"D:\archive",
            SessionsDirectory = @"D:\sessions",
            Repositories = [new RepositoryDefinition { Name = "Here", LocalPath = @"C:\Here" }, new RepositoryDefinition { Name = "Both", LocalPath = @"C:\Both\" }],
            PaneLayout = [new PaneLayoutEntry { SessionId = "live", Folder = @"C:\Here" }],
            SshConnections = [new SshConnection { Target = "me@box" }],
        };
        var imported = new AppSettings
        {
            HistoryArchiveDirectory = @"C:\data\agenthub\history",
            SessionsDirectory = @"C:\data\agenthub\sessions",
            Repositories = [new RepositoryDefinition { Name = "Both", LocalPath = @"c:\both" }, new RepositoryDefinition { Name = "There", LocalPath = @"C:\There" }],
            PaneLayout = [new PaneLayoutEntry { SessionId = "old-host", Folder = @"C:\There", TranscriptPath = @"C:\t.jsonl" }],
            SshConnections = [new SshConnection { Target = "ME@box" }, new SshConnection { Target = "other" }],
            CrabMascot = false,
        };

        var merged = SettingsImport.Merge(current, imported);

        Assert.Equal([@"c:\both", @"C:\There", @"C:\Here"], merged.Repositories.Select(r => r.LocalPath));
        Assert.Equal(@"D:\archive", merged.HistoryArchiveDirectory);
        Assert.Equal(@"D:\sessions", merged.SessionsDirectory);
        Assert.False(merged.CrabMascot);
        Assert.Equal(2, merged.PaneLayout.Count);
        Assert.Equal("live", merged.PaneLayout[0].SessionId);
        Assert.Null(merged.PaneLayout[1].SessionId);
        Assert.Equal(@"C:\t.jsonl", merged.PaneLayout[1].TranscriptPath);
        Assert.Equal(2, merged.SshConnections.Count);
    }

    [Fact]
    public async Task SettingsService_MergesAPendingImportOnce_AndKeepsTheOldSettings()
    {
        var dir = Path.Combine(_root, "settings");
        Write(Path.Combine(dir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new AppSettings
        {
            Repositories = [new RepositoryDefinition { Name = "Here", LocalPath = @"C:\Here" }],
        }));
        Write(Path.Combine(dir, SettingsImport.PendingFileName), System.Text.Json.JsonSerializer.Serialize(new AppSettings
        {
            Repositories = [new RepositoryDefinition { Name = "There", LocalPath = @"C:\There" }],
        }));

        var service = new AgentHub.Services.SettingsService(dir);
        var settings = await service.LoadAsync();
        Assert.True(service.PendingImportApplied);
        Assert.Equal(["There", "Here"], settings.Repositories.Select(r => r.Name));
        Assert.False(File.Exists(Path.Combine(dir, SettingsImport.PendingFileName)));
        Assert.Single(Directory.GetFiles(dir, "settings.before-import-*.json"));
        Assert.Single(Directory.GetFiles(dir, "settings.import-applied-*.json"));

        var again = await new AgentHub.Services.SettingsService(dir).LoadAsync();
        Assert.Equal(2, again.Repositories.Count);
    }
}

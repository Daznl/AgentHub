using System.IO.Compression;
using System.Text.Json;
using AgentHub.Models;
using AgentHub.Services.Todo;

namespace AgentHub.Services.Move;

/// <summary>One file that goes into a package.</summary>
public sealed record MoveFile(string FullPath, string Relative, long Length);

/// <summary>What one item would put in the package, found by <see cref="MoveExporter.Scan"/>.</summary>
public sealed record MoveItemPlan(MoveItem Item, string Root, IReadOnlyList<MoveFile> Files)
{
    public long Bytes => Files.Sum(f => f.Length);
}

/// <summary>Progress of an export or import: bytes and files done of the total, and the file being worked on.</summary>
public sealed record MoveProgress(string Stage, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string? Current = null);

public sealed record MoveExportResult(string ZipPath, long ZipBytes, int Files, long SourceBytes, IReadOnlyList<MoveSkipped> Skipped);

/// <summary>
/// Packs AgentHub's data, and the coding CLIs' setup and sessions it reads, into one zip for another computer.
/// Sign-ins and keys are never packed: they are left out by name before anything is read.
/// </summary>
public static class MoveExporter
{
    /// <summary>Never packed, at any depth: CLI sign-ins, keys and certificates.</summary>
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".credentials.json", "credentials.json", "auth.json", ".env", ".netrc", ".git-credentials",
    };

    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12",
    };

    /// <summary>Claude Code's own state that belongs to this computer, its sign-in, or a running process. Top level of ~\.claude.</summary>
    private static readonly HashSet<string> ClaudeMachineState = new(StringComparer.OrdinalIgnoreCase)
    {
        "projects", "cache", "telemetry", "statsig", "shell-snapshots", "session-env", "sessions", "daemon", "daemon.log",
        "jobs", "ide", "debug", "downloads", "chrome", "state", "stats-cache.json", "mcp-needs-auth-cache.json", "backups",
        "paste-cache", "logs",
    };

    private static readonly string[] ClaudeMachineStatePrefixes = ["policy-limits", "remote-settings", ".last-"];

    /// <summary>Codex's own state that belongs to this computer, its sign-in or a running process. Top level of ~\.codex.</summary>
    private static readonly HashSet<string> CodexMachineState = new(StringComparer.OrdinalIgnoreCase)
    {
        "sessions", "log", "logs", "cache", "tmp", ".tmp", "version.json", "models_cache.json",
    };

    public static bool IsSecret(string path)
    {
        var name = Path.GetFileName(path);
        return SecretNames.Contains(name) || SecretExtensions.Contains(Path.GetExtension(name));
    }

    /// <summary>Lists what each item would pack. Nothing is read beyond folder listings.</summary>
    public static List<MoveItemPlan> Scan(MoveLocations locations, CancellationToken cancel = default)
    {
        var plans = new List<MoveItemPlan>();
        foreach (var item in MoveLocations.Items)
        {
            cancel.ThrowIfCancellationRequested();
            var root = locations.Root(item.Key);
            plans.Add(new MoveItemPlan(item, root, ScanItem(item.Key, root, locations)));
        }
        return plans;
    }

    private static List<MoveFile> ScanItem(string key, string root, MoveLocations locations) => key switch
    {
        "agenthub" => List(root, recurse: false, keep: r => IsAgentHubSettingsFile(r)),
        "claude" => List(root, recurse: true, keep: r => !IsMachineState(r, ClaudeMachineState, ClaudeMachineStatePrefixes)),
        "codex" => List(root, recurse: true, keep: r => !IsMachineState(r, CodexMachineState, [])),
        "todo" => List(root, recurse: false, keep: r => TodoDocument.FileNames.Contains(r, StringComparer.OrdinalIgnoreCase)),
        "history-claude" => ArchiveOnly(root, locations.Root("claude-projects")),
        "history-codex" => ArchiveOnly(root, locations.Root("codex-sessions")),
        _ => List(root, recurse: true, keep: _ => true),
    };

    private static bool IsAgentHubSettingsFile(string relative) =>
        relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && !relative.StartsWith("settings.import-", StringComparison.OrdinalIgnoreCase)
        && !relative.StartsWith("settings.before-import", StringComparison.OrdinalIgnoreCase)
        && !relative.Contains(".broken-", StringComparison.OrdinalIgnoreCase);

    private static bool IsMachineState(string relative, HashSet<string> names, string[] prefixes)
    {
        var top = relative.Split('\\', '/')[0];
        return names.Contains(top) || prefixes.Any(p => top.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Archive files the live folder does not already cover: sessions the CLI has deleted, and kept versions of rewritten
    /// ones. A live transcript at least as long as its archived copy covers it, since transcripts only grow.
    /// </summary>
    private static List<MoveFile> ArchiveOnly(string archiveRoot, string liveRoot) =>
        List(archiveRoot, recurse: true, keep: _ => true)
            .Where(f =>
            {
                var live = new FileInfo(Path.Combine(liveRoot, f.Relative));
                return !live.Exists || live.Length < f.Length;
            })
            .ToList();

    private static List<MoveFile> List(string root, bool recurse, Func<string, bool> keep)
    {
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var files = new List<MoveFile>();
        try
        {
            foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                var relative = Path.GetRelativePath(root, info.FullName);
                if (IsSecret(relative) || !keep(relative)) continue;
                files.Add(new MoveFile(info.FullName, relative, info.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that vanished or cannot be listed packs what was found before it.
        }
        return files;
    }

    /// <summary>
    /// Writes the chosen items to <paramref name="zipPath"/>. It is written beside as <c>.partial</c> and renamed only
    /// once complete, so a cancelled or failed export never leaves a zip that looks whole.
    /// </summary>
    public static async Task<MoveExportResult> ExportAsync(
        string zipPath, IReadOnlyList<MoveItemPlan> plans, MoveLocations locations, AppSettings settings,
        IProgress<MoveProgress>? progress = null, CancellationToken cancel = default)
    {
        var manifest = new MoveManifest
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Machine = Environment.MachineName,
            UserName = Environment.UserName,
            UserProfile = locations.UserProfile,
            AppVersion = typeof(MoveExporter).Assembly.GetName().Version?.ToString(),
            Locations = locations.ToDictionary(),
            Repositories = settings.Repositories.Select(r => new MoveRepository(r.Name, r.LocalPath, r.RemoteUrl)).ToList(),
            McpServers = ReadMcpServerNames(locations.UserProfile),
        };

        var totalBytes = plans.Sum(p => p.Bytes);
        var totalFiles = plans.Sum(p => p.Files.Count);
        long doneBytes = 0;
        var doneFiles = 0;
        var partial = zipPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);

        try
        {
            await Task.Run(() =>
            {
                using var zipStream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);
                var buffer = new byte[1 << 20];
                foreach (var plan in plans)
                {
                    var packedFiles = 0;
                    long packedBytes = 0;
                    foreach (var file in plan.Files)
                    {
                        cancel.ThrowIfCancellationRequested();
                        progress?.Report(new MoveProgress("Packing " + plan.Item.Label, doneBytes, totalBytes, doneFiles, totalFiles, file.Relative));
                        var written = PackFile(zip, plan.Item.Key, file, buffer, manifest.Skipped);
                        if (written >= 0) { packedFiles++; packedBytes += written; }
                        doneBytes += file.Length;
                        doneFiles++;
                    }
                    manifest.Items.Add(new MoveManifestItem(plan.Item.Key, packedFiles, packedBytes));
                }

                var entry = zip.CreateEntry(MoveManifest.EntryName, CompressionLevel.Optimal);
                using var manifestStream = entry.Open();
                JsonSerializer.Serialize(manifestStream, manifest, MoveManifest.JsonOptions);
            }, cancel);

            File.Move(partial, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { }
            throw;
        }

        progress?.Report(new MoveProgress("Done", totalBytes, totalBytes, totalFiles, totalFiles));
        return new MoveExportResult(zipPath, new FileInfo(zipPath).Length, manifest.Items.Sum(i => i.Files), manifest.Items.Sum(i => i.Bytes), manifest.Skipped);
    }

    // The CLI may be writing the file: it is opened shared and only its length at open time is packed. Returns -1 when skipped.
    private static long PackFile(ZipArchive zip, string key, MoveFile file, byte[] buffer, List<MoveSkipped> skipped)
    {
        FileStream input;
        try
        {
            input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new MoveSkipped(file.FullPath, ex.Message));
            return -1;
        }

        using (input)
        {
            var entry = zip.CreateEntry(MoveManifest.ItemPrefix(key) + file.Relative.Replace('\\', '/'), CompressionLevel.Optimal);
            try { entry.LastWriteTime = File.GetLastWriteTime(file.FullPath); }
            catch (ArgumentOutOfRangeException) { /* before 1980: the zip keeps its default */ }

            using var output = entry.Open();
            var length = input.Length;
            long written = 0;
            while (written < length)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - written));
                if (read == 0) break;
                output.Write(buffer, 0, read);
                written += read;
            }
            return written;
        }
    }

    /// <summary>The names of Claude Code's user MCP servers in ~\.claude.json. Their settings stay behind: they can hold keys.</summary>
    public static List<string> ReadMcpServerNames(string userProfile)
    {
        var path = Path.Combine(userProfile, ".claude.json");
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object
                ? servers.EnumerateObject().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>A file name for a new package: <c>AgentHub-move-&lt;computer&gt;-&lt;date&gt;.zip</c>.</summary>
    public static string SuggestedFileName(DateTime now) => $"AgentHub-move-{Environment.MachineName}-{now:yyyyMMdd-HHmm}.zip";
}

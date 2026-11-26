using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AgentHub.Services.Move;

/// <summary>How many files of one item went where.</summary>
public sealed class MoveItemCounts
{
    /// <summary>New on this computer.</summary>
    public int Written { get; set; }
    /// <summary>A transcript this computer had an earlier part of, now complete.</summary>
    public int Extended { get; set; }
    /// <summary>Already here, identical or longer.</summary>
    public int Same { get; set; }
    /// <summary>Here with different contents: kept, the imported copy set aside.</summary>
    public int Conflicts { get; set; }
    /// <summary>Not imported: unsafe name or unreadable entry.</summary>
    public int Skipped { get; set; }
}

/// <summary>A file this computer already had with different contents. The existing one is kept; the imported copy waits here.</summary>
public sealed record MoveConflict(string ItemKey, string TargetPath, string ImportedCopyPath);

public sealed record MoveMissingRepository(string Name, string LocalPath, string? RemoteUrl);

public sealed class MoveImportResult
{
    public required string ImportDir { get; init; }
    public required string ReportPath { get; init; }
    public Dictionary<string, MoveItemCounts> Counts { get; } = [];
    public List<MoveConflict> Conflicts { get; } = [];
    public List<MoveMissingRepository> MissingRepositories { get; } = [];
    public List<MoveSkipped> Skipped { get; } = [];
    /// <summary>settings.json was staged for the next start (<see cref="SettingsImport"/>): AgentHub must restart.</summary>
    public bool SettingsPending { get; set; }
}

public sealed class MoveImportOptions
{
    public required MoveLocations Target { get; init; }
    public IReadOnlyList<PathMapping> Mappings { get; init; } = [];
    /// <summary>Item keys to import; all when null.</summary>
    public IReadOnlySet<string>? Items { get; init; }
    /// <summary>Report, staging and set-aside copies. A new folder under <see cref="MoveLocations.ImportsRoot"/> by default.</summary>
    public string? ImportDir { get; init; }
}

/// <summary>
/// Unpacks a move package onto this computer without overwriting anything: a file that is missing is written, an
/// identical one is left, a transcript this computer has only the start of is completed, and any other difference keeps
/// this computer's copy and sets the imported one aside in the import folder, listed in the report.
/// </summary>
public static class MoveImporter
{
    public static MoveManifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return ReadManifest(zip);
    }

    private static MoveManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(MoveManifest.EntryName)
            ?? throw new InvalidDataException("This zip is not an AgentHub move package: it has no manifest.json.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<MoveManifest>(stream, MoveManifest.JsonOptions)
            ?? throw new InvalidDataException("The package's manifest.json is empty.");
        if (manifest.Format != MoveManifest.FormatName)
            throw new InvalidDataException("This zip is not an AgentHub move package.");
        if (manifest.Version > MoveManifest.CurrentVersion)
            throw new InvalidDataException($"This package was made by a newer AgentHub (format {manifest.Version}). Update AgentHub on this computer first.");
        return manifest;
    }

    /// <summary>
    /// The folder moves the package implies: the home folder, and each item folder that is somewhere else here.
    /// Identical ones are dropped, so a package from the same user on the same layout maps nothing.
    /// </summary>
    public static List<PathMapping> SuggestMappings(MoveManifest manifest, MoveLocations target)
    {
        var mappings = new List<PathMapping>();
        if (!string.IsNullOrWhiteSpace(manifest.UserProfile))
            mappings.Add(new PathMapping(manifest.UserProfile, target.UserProfile));
        foreach (var (key, sourceRoot) in manifest.Locations)
        {
            if (MoveLocations.Items.All(i => i.Key != key) || key == "todo") continue;
            mappings.Add(new PathMapping(sourceRoot, target.Root(key)));
        }
        return mappings.Where(m => m.IsChange)
            .DistinctBy(m => PathMapping.Normalise(m.From), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Task<MoveImportResult> ImportAsync(string zipPath, MoveImportOptions options,
        IProgress<MoveProgress>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() => Import(zipPath, options, progress, cancel), cancel);

    private static MoveImportResult Import(string zipPath, MoveImportOptions options, IProgress<MoveProgress>? progress, CancellationToken cancel)
    {
        var importDir = options.ImportDir ?? Path.Combine(MoveLocations.ImportsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var staging = Path.Combine(importDir, "staging");
        Directory.CreateDirectory(staging);
        var result = new MoveImportResult { ImportDir = importDir, ReportPath = Path.Combine(importDir, "import-report.md") };
        var mapper = new PathMapper(options.Mappings);

        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = ReadManifest(zip);
        var entries = zip.Entries
            .Where(e => e.FullName != MoveManifest.EntryName && !e.FullName.EndsWith('/'))
            .Select(e => (Entry: e, Key: ItemKey(e.FullName)))
            .Where(x => x.Key is not null && (options.Items is null || options.Items.Contains(x.Key)))
            .ToList();
        var totalBytes = entries.Sum(x => x.Entry.Length);
        long doneBytes = 0;
        var doneFiles = 0;

        try
        {
            foreach (var (entry, key) in entries)
            {
                cancel.ThrowIfCancellationRequested();
                var item = MoveLocations.Items.First(i => i.Key == key);
                var counts = result.Counts.TryGetValue(key!, out var c) ? c : result.Counts[key!] = new MoveItemCounts();
                progress?.Report(new MoveProgress("Unpacking " + item.Label, doneBytes, totalBytes, doneFiles, entries.Count, entry.FullName));
                try
                {
                    ImportEntry(entry, item, options.Target, mapper, staging, importDir, counts, result);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    counts.Skipped++;
                    result.Skipped.Add(new MoveSkipped(entry.FullName, ex.Message));
                }
                doneBytes += entry.Length;
                doneFiles++;
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }

        foreach (var repo in manifest.Repositories)
        {
            var path = mapper.MapText(repo.LocalPath);
            if (!Directory.Exists(path)) result.MissingRepositories.Add(new MoveMissingRepository(repo.Name, path, repo.RemoteUrl));
        }

        File.WriteAllText(result.ReportPath, Report(zipPath, manifest, mapper, result), new UTF8Encoding(false));
        progress?.Report(new MoveProgress("Done", totalBytes, totalBytes, entries.Count, entries.Count));
        return result;
    }

    private static string? ItemKey(string entryName)
    {
        if (!entryName.StartsWith("items/", StringComparison.Ordinal)) return null;
        var slash = entryName.IndexOf('/', "items/".Length);
        if (slash < 0) return null;
        var key = entryName["items/".Length..slash];
        return MoveLocations.Items.Any(i => i.Key == key) ? key : null;
    }

    private static void ImportEntry(ZipArchiveEntry entry, MoveItem item, MoveLocations target, PathMapper mapper,
        string staging, string importDir, MoveItemCounts counts, MoveImportResult result)
    {
        var relative = entry.FullName[MoveManifest.ItemPrefix(item.Key).Length..].Replace('/', '\\');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('\\').Any(s => s is ".." or "."))
            throw new InvalidDataException("Unsafe file name in the package: " + entry.FullName);
        if (MoveExporter.IsSecret(relative))
            throw new InvalidDataException("Sign-in files are never imported: " + entry.FullName);

        if (item.EncodedFolderNames)
        {
            var parts = relative.Split('\\', 2);
            relative = parts.Length == 2 ? mapper.MapEncodedFolder(parts[0]) + "\\" + parts[1] : relative;
        }

        var root = Path.GetFullPath(target.Root(item.Key));
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!destination.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe file name in the package: " + entry.FullName);

        // Unpack (paths rewritten) to staging first, so the comparison is with exactly what would be written.
        var staged = Path.Combine(staging, item.Key, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        using (var input = entry.Open())
        using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            if (!mapper.IsEmpty && PathMapper.IsText(relative)) mapper.MapStream(input, output);
            else input.CopyTo(output, 1 << 16);
        }
        var stamp = entry.LastWriteTime.UtcDateTime;
        File.SetLastWriteTimeUtc(staged, stamp);

        if (item.Kind == MoveItemKind.AgentHubSettings && relative.Equals("settings.json", StringComparison.OrdinalIgnoreCase))
        {
            // Never written over the live file: AgentHub merges it at its next start (SettingsImport).
            Directory.CreateDirectory(target.SettingsDir);
            File.Move(staged, Path.Combine(target.SettingsDir, SettingsImport.PendingFileName), overwrite: true);
            result.SettingsPending = true;
            counts.Written++;
            return;
        }

        if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(staged, destination);
            counts.Written++;
            return;
        }

        var stagedInfo = new FileInfo(staged);
        var existing = new FileInfo(destination);
        if (stagedInfo.Length == existing.Length && StartsWith(destination, staged, stagedInfo.Length))
        {
            File.Delete(staged);
            counts.Same++;
            return;
        }

        if (item.Kind == MoveItemKind.Transcripts && relative.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            // Transcripts only grow: the longer of two copies that agree on their common start is the complete one.
            if (stagedInfo.Length > existing.Length && StartsWith(staged, destination, existing.Length))
            {
                File.Move(staged, destination, overwrite: true);
                counts.Extended++;
                return;
            }
            if (existing.Length > stagedInfo.Length && StartsWith(destination, staged, stagedInfo.Length))
            {
                File.Delete(staged);
                counts.Same++;
                return;
            }
        }

        var aside = Path.Combine(importDir, "conflicts", item.Key, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
        File.Move(staged, aside, overwrite: true);
        counts.Conflicts++;
        result.Conflicts.Add(new MoveConflict(item.Key, destination, aside));
    }

    /// <summary>Whether the first <paramref name="count"/> bytes of <paramref name="longer"/> equal those of <paramref name="prefix"/>.</summary>
    private static bool StartsWith(string longer, string prefix, long count)
    {
        using var a = new FileStream(longer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var b = new FileStream(prefix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (a.Length < count || b.Length < count) return false;
        var bufA = new byte[1 << 16];
        var bufB = new byte[1 << 16];
        long done = 0;
        while (done < count)
        {
            var want = (int)Math.Min(bufA.Length, count - done);
            var readA = a.ReadAtLeast(bufA.AsSpan(0, want), want, throwOnEndOfStream: false);
            var readB = b.ReadAtLeast(bufB.AsSpan(0, want), want, throwOnEndOfStream: false);
            if (readA != want || readB != want) return false;
            if (!bufA.AsSpan(0, want).SequenceEqual(bufB.AsSpan(0, want))) return false;
            done += want;
        }
        return true;
    }

    /// <summary>
    /// The owner chose the imported copy of a conflicting file: this computer's copy moves to <c>replaced\</c> in the
    /// import folder first, so nothing is lost.
    /// </summary>
    public static string UseImportedCopy(MoveConflict conflict, string importDir)
    {
        var backup = Path.Combine(importDir, "replaced", conflict.ItemKey, Path.GetRelativePath(Path.Combine(importDir, "conflicts", conflict.ItemKey), conflict.ImportedCopyPath));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if (File.Exists(conflict.TargetPath)) File.Move(conflict.TargetPath, backup, overwrite: true);
        File.Copy(conflict.ImportedCopyPath, conflict.TargetPath, overwrite: false);
        return backup;
    }

    private static string Report(string zipPath, MoveManifest manifest, PathMapper mapper, MoveImportResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AgentHub import report");
        sb.AppendLine();
        sb.AppendLine($"- Package: `{zipPath}`");
        sb.AppendLine($"- From: {manifest.Machine} ({manifest.UserName}), packed {manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Imported: {DateTime.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName} ({Environment.UserName})");
        sb.AppendLine();
        sb.AppendLine("## Paths rewritten");
        sb.AppendLine();
        if (mapper.IsEmpty) sb.AppendLine("None: the folders are the same on both computers.");
        foreach (var m in mapper.Mappings) sb.AppendLine($"- `{m.From}` → `{m.To}`");
        sb.AppendLine();
        sb.AppendLine("## Files");
        sb.AppendLine();
        sb.AppendLine("| Item | New | Completed | Already here | Kept this computer's | Skipped |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var item in MoveLocations.Items)
        {
            if (!result.Counts.TryGetValue(item.Key, out var c)) continue;
            sb.AppendLine($"| {item.Label} | {c.Written} | {c.Extended} | {c.Same} | {c.Conflicts} | {c.Skipped} |");
        }
        if (result.SettingsPending)
        {
            sb.AppendLine();
            sb.AppendLine("AgentHub's settings.json is merged at the next start: repositories and panes are added to this computer's, the rest is taken from the package. The settings before the merge are kept as `settings.before-import-<time>.json`.");
        }
        if (result.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Different on this computer (this computer's copy kept)");
            sb.AppendLine();
            foreach (var c in result.Conflicts) sb.AppendLine($"- `{c.TargetPath}`: imported copy in `{c.ImportedCopyPath}`");
        }
        if (result.MissingRepositories.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Repositories not on this computer");
            sb.AppendLine();
            foreach (var r in result.MissingRepositories) sb.AppendLine($"- {r.Name}: `{r.LocalPath}`{(r.RemoteUrl is { } u ? $" from {u}" : "")}");
        }
        if (manifest.McpServers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Claude Code MCP servers to add again");
            sb.AppendLine();
            sb.AppendLine("Their settings can hold keys, so they were not packed: " + string.Join(", ", manifest.McpServers.Select(s => $"`{s}`")) + ".");
        }
        var skipped = manifest.Skipped.Concat(result.Skipped).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Not moved");
            sb.AppendLine();
            foreach (var s in skipped) sb.AppendLine($"- `{s.Path}`: {s.Reason}");
        }
        sb.AppendLine();
        sb.AppendLine("Sign-ins are never moved: sign in to Claude Code, Codex and GitHub (`gh auth login`) on this computer.");
        return sb.ToString();
    }
}

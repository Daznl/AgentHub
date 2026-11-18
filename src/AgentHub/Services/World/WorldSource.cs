using System.Diagnostics;
using AgentHub.Hosting;
using AgentHub.Services.Timeline;

namespace AgentHub.Services.World;

/// <summary>Reads what the Timeline does not: the repository's worktrees and the sessions AgentHub hosts in them.</summary>
public static class WorldSource
{
    public static async Task<List<WorldWorktree>> ReadWorktreesAsync(ProcessRunner runner, TimelineScope scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsGit) return [];
        try
        {
            var (code, output, _) = await runner.RunAsync("git", ["worktree", "list", "--porcelain"], scope.Root, cancellationToken);
            return code == 0 ? WorldSnapshotBuilder.ParseWorktrees(output) : [];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return []; // git not installed
        }
    }

    /// <summary>
    /// The whole snapshot read from scratch, as the Timeline window reads its model (for tools and tests; the window
    /// passes its own model instead of reading twice).
    /// </summary>
    public static async Task<WorldSnapshot> ReadAsync(ProcessRunner runner, TimelineScope scope, string? archiveRoot, CancellationToken cancellationToken = default)
    {
        var commits = await TimelineSource.ReadCommitsAsync(runner, scope, cancellationToken);
        var tips = await TimelineSource.ReadBranchTipsAsync(runner, scope, cancellationToken);
        var prs = scope.IsGit ? await TimelineSource.ReadPullRequestsAsync(runner, scope, cancellationToken) : null;
        var squashes = await new SquashMatcher(runner, scope).MatchAsync(commits, BranchGraph.OpenTips(commits, tips), SquashMatcher.FromPullRequests(prs, commits));
        var model = TimelineBuilder.Build(TimelineSource.ReadSessions(scope, archiveRoot), commits, tips, DateTimeOffset.UtcNow, squashes);
        var worktrees = await ReadWorktreesAsync(runner, scope, cancellationToken);
        return WorldSnapshotBuilder.Build(scope, model, prs, worktrees, ReadPanes(scope), DateTimeOffset.UtcNow);
    }

    /// <summary>Hosted sessions whose host still runs, in the scope's folders.</summary>
    public static List<WorldPane> ReadPanes(TimelineScope scope, SessionStore? store = null)
    {
        var live = (store ?? new SessionStore()).ListAll().Where(r => !r.HasExited && SessionStore.IsHostAlive(r));
        return WorldSnapshotBuilder.PanesIn(scope, live);
    }
}

/// <summary>
/// Starts the Godot project that draws a world file. The project ships beside <c>AgentHub.exe</c> in <c>world\</c>.
/// Godot is found from the setting (a full path, or a name looked up on <c>PATH</c>; a <c>.cmd</c> launcher works too).
/// Godot's output goes to <c>godot.log</c> beside the world file.
/// </summary>
public static class GodotLauncher
{
    public static string ProjectDir => Path.Combine(AppContext.BaseDirectory, "world");

    /// <summary>The full path of the Godot command, or null when it cannot be found.</summary>
    public static string? Resolve(string? command)
    {
        command = string.IsNullOrWhiteSpace(command) ? "godot" : command.Trim().Trim('"');
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;
        var extensions = Path.HasExtension(command) ? [""] : new[] { ".exe", ".cmd", ".bat" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* a malformed PATH entry */ }
            }
        return null;
    }

    /// <summary>Starts Godot on the world project, following <paramref name="worldPath"/>. Throws when Godot or the project is missing.</summary>
    public static Process Launch(string godot, string worldPath)
    {
        var project = ProjectDir;
        if (!File.Exists(Path.Combine(project, "project.godot")))
            throw new FileNotFoundException($"The 3D world project is missing from {project}.");
        var args = $"--path {Quote(project)} -- --world {Quote(worldPath)}";
        var isScript = godot.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || godot.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            // cmd /s /c "<whole line>": the outer quotes go, everything inside stays as written, spaces and all
            FileName = isScript ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : godot,
            Arguments = isScript ? $"/s /c \"{Quote(godot)} {args}\"" : args,
            WorkingDirectory = project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Godot did not start.");
        var log = Path.Combine(Path.GetDirectoryName(worldPath)!, "godot.log");
        // Shared, so the log can be read (by the owner, by tests) while Godot still runs
        var writer = new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        var gate = new object();
        var closed = false;
        void Line(string? text) { lock (gate) if (text is not null && !closed) writer.WriteLine(text); }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => { lock (gate) { closed = true; writer.Dispose(); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
}

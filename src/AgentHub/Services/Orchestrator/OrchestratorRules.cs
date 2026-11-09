using System.IO;
using AgentHub.Services.Instructions;

namespace AgentHub.Services.Orchestrator;

/// <summary>One instruction file the worker loads, as passed to the orchestrator.</summary>
public sealed record RulesFile(string Label, string Path, string Text);

/// <summary>
/// What the orchestrator may and may not ask of the worker. Its own Claude session loads no settings or CLAUDE.md
/// (<c>--setting-sources ""</c>), so without this it knew none of the owner's rules and once told a worker to force push
/// (owner report 2026-11-09). Two parts: fixed rules always in its prompt, and the text of the instruction files the worker
/// itself loads (the CLI's user-wide file and the project's CLAUDE.md / AGENTS.md / GEMINI.md), read when it is attached.
/// </summary>
public static class OrchestratorRules
{
    /// <summary>Longest single file passed on; the start of an instruction file carries its most general rules.</summary>
    public const int MaxFileChars = 30_000;

    /// <summary>Always in the orchestrator's prompt, whatever the files say.</summary>
    public const string HardRules = """
        Rules you never break:
        - Nothing destructive or hard to undo without the owner. Never tell the worker to force push (--force, --force-with-lease, +refspec),
          reset --hard, rebase or amend commits already pushed, delete branches, tags or files it did not create, git clean, discard
          uncommitted changes, overwrite a deployment, shared data or someone else's work, or skip hooks or signing (--no-verify), unless
          the owner asked for exactly that in this conversation. If the work seems to need one, stop: tell the owner what and why, offer
          the safe alternative (a new branch, a merge, a revert commit), and send the worker nothing until they answer.
        - Same for outward-facing steps the owner has not cleared: pushing to the trunk, merging pull requests, deploying, publishing,
          sending messages. Follow the instruction files below on how these are done (branches, pull requests, commit messages).
        - The owner's instruction files (copied below when there are any) are their standing rules for this project. They bind you and the worker as much as the owner's
          messages do; every to_worker must keep to them. When the owner asks for something they forbid, point to the rule and ask
          them to confirm before acting.
        - When the worker proposes or asks to do something these rules forbid, refuse it in to_worker, or put it to the owner.
        - Never answer a security check, permission dialog or confirmation the worker shows on the owner's behalf.
        """;

    /// <summary>
    /// The instruction files the worker loads, outermost first (user-wide, then the project's from its repository root down to
    /// <paramref name="projectDirectory"/>), existing and non-empty only, identical texts once.
    /// </summary>
    /// <param name="workerCli">The worker's CLI, for its user-wide file; Claude Code when unknown.</param>
    public static IReadOnlyList<RulesFile> Gather(string projectDirectory, InstructionCli? workerCli,
        string? claudeHome = null, string? codexHome = null, string? geminiHome = null)
    {
        var candidates = new List<(string Label, string Path)>();
        var cli = workerCli ?? InstructionCli.ClaudeCode;
        var global = InstructionFiles.GlobalFiles(claudeHome, codexHome, geminiHome).First(f => f.Cli == cli);
        candidates.Add(($"user-wide {global.Label} instructions", global.Path));

        var folders = FoldersFromRepositoryRoot(projectDirectory);
        foreach (var folder in folders)
            foreach (var f in InstructionFiles.RepoFiles(folder))
                candidates.Add(($"project {Path.GetRelativePath(folders[0], f.Path)}", f.Path));

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTexts = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<RulesFile>();
        foreach (var (label, path) in candidates)
        {
            if (!seenPaths.Add(Path.GetFullPath(path))) continue;
            string text;
            try { text = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n").Trim() : ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (text.Length == 0 || !seenTexts.Add(text)) continue;
            files.Add(new RulesFile(label, path, OrchestratorMessages.Cut(text, MaxFileChars, keepTail: false)));
        }
        return files;
    }

    /// <summary>The prompt section for <paramref name="files"/>; empty when there are none.</summary>
    public static string Section(IReadOnlyList<RulesFile> files)
    {
        if (files.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append("===== THE OWNER'S INSTRUCTION FILES =====\n")
          .Append("The worker loads these at the start of every session; you do not, so they are copied here. They are binding (see the rules above).\n");
        foreach (var f in files)
            sb.Append("\n----- ").Append(f.Label).Append(" (").Append(f.Path).Append(") -----\n").Append(f.Text).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The repository root (the nearest folder up holding <c>.git</c>) down to <paramref name="projectDirectory"/>, as Claude Code
    /// reads CLAUDE.md files in parent folders; just the folder itself outside a repository.
    /// </summary>
    private static IReadOnlyList<string> FoldersFromRepositoryRoot(string projectDirectory)
    {
        var chain = new List<string>();
        for (var dir = new DirectoryInfo(Path.GetFullPath(projectDirectory)); dir != null; dir = dir.Parent)
        {
            chain.Add(dir.FullName);
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                chain.Reverse();
                return chain;
            }
        }
        return [Path.GetFullPath(projectDirectory)];
    }
}

using AgentHub.Models;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Instructions;
using AgentHub.Services.Orchestrator;
using AgentHub.Services.Timeline;

namespace AgentHub.Services.Move;

/// <summary>What one part of a move package holds, and how it is copied.</summary>
public enum MoveItemKind
{
    /// <summary>AgentHub's settings folder. settings.json is merged at the next start, never copied over.</summary>
    AgentHubSettings,
    /// <summary>Configuration files: copied when missing, set aside when the target has a different copy.</summary>
    Files,
    /// <summary>
    /// Session transcripts filed under the folder they ran in (Claude Code encodes it into the first folder name). The
    /// folder names are renamed with the path mappings; a transcript that only grew since is extended.
    /// </summary>
    Transcripts,
}

/// <summary>One part of a move package: a folder on each computer and the rule for what in it is moved.</summary>
public sealed record MoveItem(string Key, string Label, string Description, MoveItemKind Kind, bool EncodedFolderNames = false);

/// <summary>
/// Where each part of AgentHub's data lives on one computer. The source computer's copy is written into the package
/// manifest; on import, each part lands in this computer's matching folder.
/// </summary>
public sealed class MoveLocations
{
    public const string ImportsRoot = @"C:\data\agenthub\imports";
    public const string ExportsRoot = @"C:\data\agenthub\exports";

    public static readonly IReadOnlyList<MoveItem> Items =
    [
        new("agenthub", "AgentHub settings", "Repositories, agents, usage cards and the pane layout (settings.json), plus steering and ideas.", MoveItemKind.AgentHubSettings),
        new("claude", "Claude Code setup", "CLAUDE.md, settings, skills, plugins, plans, prompt history and file checkpoints. Never the sign-in.", MoveItemKind.Files),
        new("claude-projects", "Claude Code sessions", "Every Claude Code transcript and each project's memory: what History, Resume and Context read.", MoveItemKind.Transcripts, EncodedFolderNames: true),
        new("history-claude", "Archived Claude Code sessions", "Sessions Claude Code has already cleaned up, kept only in AgentHub's history archive.", MoveItemKind.Transcripts, EncodedFolderNames: true),
        new("codex", "Codex setup", "config.toml, AGENTS.md, prompts and prompt history. Never the sign-in.", MoveItemKind.Files),
        new("codex-sessions", "Codex sessions", "Every Codex transcript.", MoveItemKind.Transcripts),
        new("history-codex", "Archived Codex sessions", "Codex sessions kept only in AgentHub's history archive.", MoveItemKind.Transcripts),
        new("todo", "General to-do list", "TODO.md in the home folder (✅ To do).", MoveItemKind.Files),
        new("workflows", "Own workflows", "Workflows saved for the 🧭 Orchestrate menu.", MoveItemKind.Files),
        new("runs", "Template runs", "Runs of advanced templates kept on this computer (⏯ Runs).", MoveItemKind.Files),
        new("pane-orchestrators", "Pane orchestrators", "Each pane orchestrator's working folder and system prompt.", MoveItemKind.Files),
        new("timeline", "Timeline reviews", "Reports written by the Timeline's reviews.", MoveItemKind.Files),
    ];

    public static MoveItem Item(string key) => Items.First(i => i.Key == key);

    public required string UserProfile { get; init; }
    public required string SettingsDir { get; init; }
    public required string ClaudeDir { get; init; }
    public required string CodexDir { get; init; }
    public required string HistoryArchiveDir { get; init; }
    public required string WorkflowsDir { get; init; }
    public required string RunsDir { get; init; }
    public required string PaneOrchestratorsDir { get; init; }
    public required string TimelineDir { get; init; }

    /// <summary>This computer's folders, from the same settings and environment overrides the rest of AgentHub uses.</summary>
    public static MoveLocations ForThisComputer(AppSettings settings, string settingsDir) => new()
    {
        UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        SettingsDir = settingsDir,
        ClaudeDir = InstructionFiles.ClaudeHome,
        CodexDir = InstructionFiles.CodexHome,
        HistoryArchiveDir = string.IsNullOrWhiteSpace(settings.HistoryArchiveDirectory) ? HistoryArchiver.DefaultRoot : settings.HistoryArchiveDirectory,
        WorkflowsDir = WorkflowCatalogue.DefaultOwnFolder(),
        RunsDir = new RunStore().Root,
        PaneOrchestratorsDir = PaneOrchestrator.DefaultSessionsRoot,
        TimelineDir = TimelineReview.ReportRoot,
    };

    /// <summary>The folder that holds <paramref name="key"/>'s files on this computer.</summary>
    public string Root(string key) => key switch
    {
        "agenthub" => SettingsDir,
        "claude" => ClaudeDir,
        "claude-projects" => Path.Combine(ClaudeDir, "projects"),
        "history-claude" => HistoryArchiver.ClaudeArchiveDir(HistoryArchiveDir),
        "codex" => CodexDir,
        "codex-sessions" => Path.Combine(CodexDir, "sessions"),
        "history-codex" => HistoryArchiver.CodexArchiveDir(HistoryArchiveDir),
        "todo" => UserProfile,
        "workflows" => WorkflowsDir,
        "runs" => RunsDir,
        "pane-orchestrators" => PaneOrchestratorsDir,
        "timeline" => TimelineDir,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown move item."),
    };

    public Dictionary<string, string> ToDictionary() => Items.ToDictionary(i => i.Key, i => Root(i.Key));
}

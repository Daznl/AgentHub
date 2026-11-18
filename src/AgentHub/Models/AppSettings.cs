namespace AgentHub.Models;

public sealed class AppSettings
{
    public List<RepositoryDefinition> Repositories { get; set; } = [];

    /// <summary>Saved SSH targets shown on each Cockpit pane's SSH button. Addresses only, never credentials.</summary>
    public List<SshConnection> SshConnections { get; set; } = [];

    public List<AgentDefinition> Agents { get; set; } =
    [
        new() { Id = "codex", Name = "Codex", Command = "codex", Notes = "OpenAI Codex CLI" },
        new() { Id = "claude", Name = "Claude Code", Command = "claude", Notes = "Anthropic Claude Code" },
        new() { Id = "antigravity", Name = "Antigravity", Command = "agy", Notes = "Google Antigravity CLI" }
    ];

    public List<UsageProviderDefinition> UsageProviders { get; set; } =
    [
        new()
        {
            Id = "gemini", Name = "Antigravity", Command = "agy",
            Arguments = ["-p", "/usage"],
            Enabled = true
        },
        new()
        {
            Id = "codex", Name = "Codex", Command = "codex",
            Arguments = ["/status"], UnqualifiedPercentagesAreRemaining = true,
            Enabled = true
        },
        new()
        {
            Id = "claude", Name = "Claude Code", Command = "claude",
            Arguments = ["/status"],
            Enabled = true
        }
    ];

    public double UsageRefreshMinutes { get; set; } = 2;

    public bool PreferWindowsTerminal { get; set; } = true;

    /// <summary>
    /// Drift/scan motion of the main window backdrop. Set false for a static backdrop
    /// (reduced motion, remote desktop, or low-power laptops). The glow layers still render.
    /// </summary>
    public bool AnimatedBackground { get; set; } = true;

    /// <summary>
    /// Whether the pixel-art crab at the top right of the header (the AgentHub helper's button) wanders and does tricks.
    /// False keeps it still at home; it is always shown. It also stays still while <see cref="AnimatedBackground"/> or
    /// Windows animations are off.
    /// </summary>
    public bool CrabMascot { get; set; } = true;

    /// <summary>Whether the usage section above the terminals is shown. Toggled by the 📊 Usage header button.</summary>
    public bool ShowUsagePanel { get; set; } = true;

    /// <summary>Provider ids whose usage card the user has closed in the Cockpit. Reopen from the chips beside "Usage limits".</summary>
    public List<string> HiddenUsageCards { get; set; } = [];

    /// <summary>
    /// Flash a Cockpit pane (and the taskbar button when AgentHub is in the background) the first time
    /// a coding CLI in that pane stops working and waits for input. Plain shells are never tracked.
    /// </summary>
    public bool AttentionFlashEnabled { get; set; } = true;

    /// <summary>Seconds of output silence after a burst of agent activity before the pane counts as waiting.</summary>
    public double AttentionIdleSeconds { get; set; } = 3;

    /// <summary>
    /// Context window size assumed for Claude Code sessions in the Cockpit context badge. Claude Code's transcripts do not
    /// record it; if a session grows past this the badge raises it automatically. Codex records its own window.
    /// </summary>
    public long ClaudeContextWindowTokens { get; set; } = 1_000_000;

    /// <summary>
    /// The Godot command that draws the 3D world (🌐 World): a full path, or a name looked up on PATH.
    /// A .cmd launcher works too. Experimental (branch feature/world-3d).
    /// </summary>
    public string GodotCommand { get; set; } = "godot";

    /// <summary>
    /// Context window fraction (0.40–0.95) at which a pane orchestrator (🧭 Orchestrate) hands its session off to a fresh one,
    /// when the picked workflow sets none. The field in the orchestrator box changes it live and saves it here for the next one.
    /// </summary>
    public double PaneOrchestratorContextThreshold { get; set; } = 0.75;

    /// <summary>
    /// Keep AgentHub's own copy of every Claude Code and Codex transcript in <see cref="HistoryArchiveDirectory"/>, so
    /// session history survives the CLIs' cleanup. Synced at startup and every minute; nothing is ever deleted from it.
    /// </summary>
    public bool HistoryArchiveEnabled { get; set; } = true;

    /// <summary>Where the history archive lives. Keep it outside synced folders and Git: transcripts can be gigabytes and hold secrets.</summary>
    public string HistoryArchiveDirectory { get; set; } = Services.History.HistoryArchiver.DefaultRoot;

    /// <summary>Last folder chosen for "open a shell in a folder"; the picker starts here next time.</summary>
    public string? LastTerminalFolder { get; set; }

    /// <summary>Last folder chosen for "Scan folder for repos"; the picker starts here next time.</summary>
    public string? LastScanFolder { get; set; }

    /// <summary>
    /// Run each Cockpit session in its own AgentHub.SessionHost process, so closing (or updating) AgentHub leaves the CLI
    /// running and the next start reattaches to it. Off, sessions run inside AgentHub and end with it.
    /// </summary>
    public bool SessionsOutliveAgentHub { get; set; } = true;

    /// <summary>Where session host records, replays and logs live. One folder per session; ended ones are pruned to the newest 20.</summary>
    public string SessionsDirectory { get; set; } = Hosting.SessionStore.DefaultRoot;

    /// <summary>The Cockpit panes as last saved, in order, so the next start brings them back. Maintained by AgentHub.</summary>
    public List<PaneLayoutEntry> PaneLayout { get; set; } = [];
}

namespace AgentHub.Models;

public sealed class AppSettings
{
    public List<RepositoryDefinition> Repositories { get; set; } = [];

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

    /// <summary>Last folder chosen for "open a shell in a folder"; the picker starts here next time.</summary>
    public string? LastTerminalFolder { get; set; }

    /// <summary>Last folder chosen for "Scan folder for repos"; the picker starts here next time.</summary>
    public string? LastScanFolder { get; set; }
}

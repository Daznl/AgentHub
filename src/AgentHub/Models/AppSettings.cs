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
}

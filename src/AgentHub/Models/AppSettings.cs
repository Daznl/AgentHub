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

    public bool PreferWindowsTerminal { get; set; } = true;
}

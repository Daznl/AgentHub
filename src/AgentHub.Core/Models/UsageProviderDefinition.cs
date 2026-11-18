namespace AgentHub.Models;

public sealed class UsageProviderDefinition
{
    public string Id { get; set; } = "provider";
    public string Name { get; set; } = "Provider";
    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public bool Enabled { get; set; } = true;

    // Codex reports percentages remaining. Claude and Gemini normally report used percentages.
    public bool UnqualifiedPercentagesAreRemaining { get; set; }
}

namespace AgentHub.Models;

public sealed class AgentDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Agent";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }

    public override string ToString() => Name;
}

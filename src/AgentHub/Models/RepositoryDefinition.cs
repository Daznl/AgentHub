namespace AgentHub.Models;

public sealed class RepositoryDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Repository";
    public string LocalPath { get; set; } = "";
    public string? RemoteUrl { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    public override string ToString() => Name;
}

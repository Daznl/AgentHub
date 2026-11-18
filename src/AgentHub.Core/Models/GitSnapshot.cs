namespace AgentHub.Models;

public sealed record GitSnapshot(
    string Branch,
    string Remote,
    int Ahead,
    int Behind,
    int Modified,
    int Added,
    int Deleted,
    int Untracked,
    string RawStatus)
{
    public int ChangedFiles => Modified + Added + Deleted + Untracked;
    public bool IsClean => ChangedFiles == 0;
}

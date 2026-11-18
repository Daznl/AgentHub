using System.IO;
using System.Text.Json.Serialization;

namespace AgentHub.Models;

public sealed class RepositoryDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Repository";
    public string LocalPath { get; set; } = "";
    public string? RemoteUrl { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    // Runtime state (not saved in settings.json)
    [JsonIgnore]
    public bool ExistsOnDisk => Directory.Exists(LocalPath);

    [JsonIgnore]
    public string? CurrentBranch { get; set; }

    [JsonIgnore]
    public int Ahead { get; set; }

    [JsonIgnore]
    public int Behind { get; set; }

    [JsonIgnore]
    public int ChangedFiles { get; set; }

    [JsonIgnore]
    public string StatusBadgeText
    {
        get
        {
            if (!ExistsOnDisk) return "⚠️ Missing";
            if (Behind > 0 && Ahead > 0) return $"⇡{Ahead} ⇣{Behind}";
            if (Behind > 0) return $"⇣{Behind} behind";
            if (Ahead > 0) return $"⇡{Ahead} ahead";
            if (ChangedFiles > 0) return $"✎{ChangedFiles} modified";
            return "✓ Up to date";
        }
    }

    [JsonIgnore]
    public BadgeTone StatusTone
    {
        get
        {
            if (!ExistsOnDisk) return BadgeTone.Missing;
            if (Behind > 0) return BadgeTone.Behind;
            if (Ahead > 0) return BadgeTone.Ahead;
            if (ChangedFiles > 0) return BadgeTone.Dirty;
            return BadgeTone.UpToDate;
        }
    }

    public override string ToString() => Name;
}

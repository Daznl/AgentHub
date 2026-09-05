using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace AgentHub.Models;

public sealed class RepositoryDefinition
{
    private static readonly SolidColorBrush CleanBg = new(Color.FromRgb(6, 95, 70));
    private static readonly SolidColorBrush CleanFg = new(Color.FromRgb(110, 231, 183));
    private static readonly SolidColorBrush BehindBg = new(Color.FromRgb(146, 64, 14));
    private static readonly SolidColorBrush BehindFg = new(Color.FromRgb(254, 230, 138));
    private static readonly SolidColorBrush AheadBg = new(Color.FromRgb(30, 64, 175));
    private static readonly SolidColorBrush AheadFg = new(Color.FromRgb(147, 197, 253));
    private static readonly SolidColorBrush DirtyBg = new(Color.FromRgb(133, 77, 14));
    private static readonly SolidColorBrush DirtyFg = new(Color.FromRgb(254, 240, 138));
    private static readonly SolidColorBrush MissingBg = new(Color.FromRgb(153, 27, 27));
    private static readonly SolidColorBrush MissingFg = new(Color.FromRgb(254, 202, 202));

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
    public SolidColorBrush StatusBadgeBrush
    {
        get
        {
            if (!ExistsOnDisk) return MissingBg;
            if (Behind > 0) return BehindBg;
            if (Ahead > 0) return AheadBg;
            if (ChangedFiles > 0) return DirtyBg;
            return CleanBg;
        }
    }

    [JsonIgnore]
    public SolidColorBrush StatusTextBrush
    {
        get
        {
            if (!ExistsOnDisk) return MissingFg;
            if (Behind > 0) return BehindFg;
            if (Ahead > 0) return AheadFg;
            if (ChangedFiles > 0) return DirtyFg;
            return CleanFg;
        }
    }

    public override string ToString() => Name;
}

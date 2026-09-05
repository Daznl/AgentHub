using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace AgentHub.Models;

public sealed class GitHubRepository
{
    private static readonly SolidColorBrush PrivateBg = new(Color.FromRgb(49, 46, 129));
    private static readonly SolidColorBrush PrivateFg = new(Color.FromRgb(224, 231, 255));
    private static readonly SolidColorBrush PublicBg = new(Color.FromRgb(6, 78, 59));
    private static readonly SolidColorBrush PublicFg = new(Color.FromRgb(167, 243, 208));

    private static readonly SolidColorBrush AddedBtnBg = new(Color.FromRgb(30, 41, 59));
    private static readonly SolidColorBrush CloneBtnBg = new(Color.FromRgb(37, 99, 235));

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("nameWithOwner")]
    public string NameWithOwner { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("pushedAt")]
    public DateTimeOffset? PushedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    // UI state properties
    public bool IsAlreadyAdded { get; set; }
    public string? LocalPath { get; set; }

    public string VisibilityLabel => IsPrivate ? "Private" : "Public";
    public SolidColorBrush VisibilityBadgeBrush => IsPrivate ? PrivateBg : PublicBg;
    public SolidColorBrush VisibilityTextBrush => IsPrivate ? PrivateFg : PublicFg;

    public Visibility AddedBadgeVisibility => IsAlreadyAdded ? Visibility.Visible : Visibility.Collapsed;

    public string ActionButtonText => IsAlreadyAdded ? "Open in Local Repos" : "⬇ Clone & Add";
    public SolidColorBrush ActionButtonBrush => IsAlreadyAdded ? AddedBtnBg : CloneBtnBg;

    public string LocalPathFormatted => IsAlreadyAdded && !string.IsNullOrWhiteSpace(LocalPath)
        ? $"📁 {LocalPath}"
        : string.Empty;

    public string PushedAtFormatted => PushedAt.HasValue
        ? $"Updated {PushedAt.Value.ToLocalTime():d MMM yyyy, HH:mm}"
        : "No commits pushed";

    public string DisplayDescription => string.IsNullOrWhiteSpace(Description)
        ? "(No description provided)"
        : Description;
}

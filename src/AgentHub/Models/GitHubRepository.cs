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
    private static readonly SolidColorBrush PullBtnBg = new(Color.FromRgb(217, 119, 6));

    private static readonly SolidColorBrush StatusUpToDateBg = new(Color.FromRgb(6, 95, 70));
    private static readonly SolidColorBrush StatusUpToDateFg = new(Color.FromRgb(110, 231, 183));
    private static readonly SolidColorBrush StatusBehindBg = new(Color.FromRgb(146, 64, 14));
    private static readonly SolidColorBrush StatusBehindFg = new(Color.FromRgb(254, 230, 138));
    private static readonly SolidColorBrush StatusAheadBg = new(Color.FromRgb(30, 64, 175));
    private static readonly SolidColorBrush StatusAheadFg = new(Color.FromRgb(147, 197, 253));
    private static readonly SolidColorBrush StatusDirtyBg = new(Color.FromRgb(133, 77, 14));
    private static readonly SolidColorBrush StatusDirtyFg = new(Color.FromRgb(254, 240, 138));
    private static readonly SolidColorBrush StatusNotClonedBg = new(Color.FromRgb(39, 39, 42));
    private static readonly SolidColorBrush StatusNotClonedFg = new(Color.FromRgb(161, 161, 170));
    private static readonly SolidColorBrush StatusMissingBg = new(Color.FromRgb(153, 27, 27));
    private static readonly SolidColorBrush StatusMissingFg = new(Color.FromRgb(254, 202, 202));

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

    [JsonPropertyName("isArchived")]
    public bool IsArchived { get; set; }

    [JsonPropertyName("isFork")]
    public bool IsFork { get; set; }

    [JsonPropertyName("isInOrganization")]
    public bool IsInOrganization { get; set; }

    /// <summary>GitHub's permission for the signed-in user: ADMIN, MAINTAIN, WRITE, TRIAGE or READ.</summary>
    [JsonPropertyName("viewerPermission")]
    public string? ViewerPermission { get; set; }

    [JsonPropertyName("owner")]
    public GitHubRepositoryOwner? Owner { get; set; }

    /// <summary>True when the signed-in account owns this repository. Set after load, not by GitHub.</summary>
    public bool IsOwnedByViewer { get; set; }

    public string OwnerLogin => Owner?.Login ?? (NameWithOwner.Contains('/') ? NameWithOwner[..NameWithOwner.IndexOf('/')] : string.Empty);

    public bool IsOwnedByOrganization =>
        IsInOrganization || string.Equals(Owner?.TypeName, "Organization", StringComparison.OrdinalIgnoreCase);

    /// <summary>WRITE or higher, and not archived. Archived repos are read-only on GitHub regardless of role.</summary>
    public bool CanPush => !IsArchived && ViewerPermission?.ToUpperInvariant() is "ADMIN" or "MAINTAIN" or "WRITE";

    public string AccessLabel
    {
        get
        {
            if (IsArchived) return "📦 Archived · read-only";
            return ViewerPermission?.ToUpperInvariant() switch
            {
                "ADMIN" => "🔑 Admin · push",
                "MAINTAIN" => "🔧 Maintain · push",
                "WRITE" => "✎ Push access",
                "TRIAGE" => "👁 Read-only · clone",
                "READ" => "👁 Read-only · clone",
                _ => "👁 Read-only · clone"
            };
        }
    }

    public string AccessTooltip => IsArchived
        ? $"{NameWithOwner} is archived on GitHub. It can be cloned and pulled but nobody can push."
        : CanPush
            ? $"Your GitHub role on {NameWithOwner} is {ViewerPermission}: you can pull and push."
            : $"Your GitHub role on {NameWithOwner} is {ViewerPermission ?? "READ"}: you can clone and pull, but pushes will be rejected.";

    public SolidColorBrush AccessBadgeBrush => IsArchived ? StatusNotClonedBg : CanPush ? StatusUpToDateBg : StatusBehindBg;
    public SolidColorBrush AccessTextBrush => IsArchived ? StatusNotClonedFg : CanPush ? StatusUpToDateFg : StatusBehindFg;

    public string OwnerLabel => IsOwnedByViewer
        ? "👤 Yours"
        : IsOwnedByOrganization ? $"🏢 {OwnerLogin}" : $"🤝 {OwnerLogin}";

    /// <summary>Group header used to categorise the list for whoever is signed in.</summary>
    public string Category
    {
        get
        {
            if (IsArchived) return "📦 Archived · clone or pull only";
            if (IsOwnedByViewer) return "👤 My repositories · full access";
            if (!IsOwnedByOrganization) return CanPush ? $"🤝 Shared with me by {OwnerLogin} · you can push" : $"🤝 Shared with me by {OwnerLogin} · clone only";
            return CanPush ? $"🏢 {OwnerLogin} · you can push" : $"🏢 {OwnerLogin} · clone only (read access)";
        }
    }

    /// <summary>Sort key so groups appear in a sensible order: mine, then pushable, then read-only, then archived.</summary>
    public int CategoryOrder
    {
        get
        {
            if (IsArchived) return 40;
            if (IsOwnedByViewer) return 0;
            if (CanPush) return IsOwnedByOrganization ? 10 : 11;
            return IsOwnedByOrganization ? 20 : 21;
        }
    }

    // UI state properties
    public bool IsAlreadyAdded { get; set; }
    public string? LocalPath { get; set; }
    public bool LocalExists { get; set; }
    public string? LocalBranch { get; set; }
    public int AheadCount { get; set; }
    public int BehindCount { get; set; }
    public int ChangedFilesCount { get; set; }

    public string VisibilityLabel => IsPrivate ? "Private" : "Public";
    public SolidColorBrush VisibilityBadgeBrush => IsPrivate ? PrivateBg : PublicBg;
    public SolidColorBrush VisibilityTextBrush => IsPrivate ? PrivateFg : PublicFg;

    public Visibility AddedBadgeVisibility => IsAlreadyAdded ? Visibility.Visible : Visibility.Collapsed;

    public string SyncStatusText
    {
        get
        {
            if (!IsAlreadyAdded || string.IsNullOrWhiteSpace(LocalPath)) return "Not Cloned Locally";
            if (!LocalExists) return "⚠️ Folder Missing";
            if (BehindCount > 0 && AheadCount > 0) return $"⇡ {AheadCount} ahead · ⇣ {BehindCount} behind";
            if (BehindCount > 0) return $"⇣ {BehindCount} commit(s) behind";
            if (AheadCount > 0 && ChangedFilesCount > 0) return $"⇡ {AheadCount} ahead · ✎ {ChangedFilesCount} modified";
            if (AheadCount > 0) return $"⇡ {AheadCount} commit(s) ahead";
            if (ChangedFilesCount > 0) return $"✎ {ChangedFilesCount} file(s) modified";
            return "✓ Up to date";
        }
    }

    public SolidColorBrush SyncStatusBadgeBrush
    {
        get
        {
            if (!IsAlreadyAdded || string.IsNullOrWhiteSpace(LocalPath)) return StatusNotClonedBg;
            if (!LocalExists) return StatusMissingBg;
            if (BehindCount > 0) return StatusBehindBg;
            if (AheadCount > 0) return StatusAheadBg;
            if (ChangedFilesCount > 0) return StatusDirtyBg;
            return StatusUpToDateBg;
        }
    }

    public SolidColorBrush SyncStatusTextBrush
    {
        get
        {
            if (!IsAlreadyAdded || string.IsNullOrWhiteSpace(LocalPath)) return StatusNotClonedFg;
            if (!LocalExists) return StatusMissingFg;
            if (BehindCount > 0) return StatusBehindFg;
            if (AheadCount > 0) return StatusAheadFg;
            if (ChangedFilesCount > 0) return StatusDirtyFg;
            return StatusUpToDateFg;
        }
    }

    public string ActionButtonText => IsAlreadyAdded ? "Open in Local Repos" : "⬇ Clone & Add";
    public SolidColorBrush ActionButtonBrush => IsAlreadyAdded ? AddedBtnBg : CloneBtnBg;

    public Visibility PullButtonVisibility => (IsAlreadyAdded && LocalExists && BehindCount > 0) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PushButtonVisibility => (IsAlreadyAdded && LocalExists && AheadCount > 0 && CanPush) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown instead of Push when there are local commits the signed-in account is not allowed to push.</summary>
    public Visibility PushBlockedVisibility => (IsAlreadyAdded && LocalExists && AheadCount > 0 && !CanPush) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CockpitButtonVisibility => (IsAlreadyAdded && LocalExists) ? Visibility.Visible : Visibility.Collapsed;

    public string LocalPathFormatted => IsAlreadyAdded && !string.IsNullOrWhiteSpace(LocalPath)
        ? (string.IsNullOrWhiteSpace(LocalBranch) ? $"📁 {LocalPath}" : $"📁 {LocalPath}  [{LocalBranch}]")
        : string.Empty;

    public string PushedAtFormatted => PushedAt.HasValue
        ? $"Updated {PushedAt.Value.ToLocalTime():d MMM yyyy, HH:mm}"
        : "No commits pushed";

    public string DisplayDescription => string.IsNullOrWhiteSpace(Description)
        ? "(No description provided)"
        : Description;
}

public sealed class GitHubRepositoryOwner
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    /// <summary>"Organization" or "User" (GraphQL __typename).</summary>
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }
}

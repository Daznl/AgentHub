using System.Text.Json.Serialization;

namespace AgentHub.Models;

public sealed class GitHubRepository
{
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

    public BadgeTone AccessTone => IsArchived ? BadgeTone.NotCloned : CanPush ? BadgeTone.UpToDate : BadgeTone.Behind;

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
    public BadgeTone VisibilityTone => IsPrivate ? BadgeTone.Private : BadgeTone.Public;

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

    public BadgeTone SyncTone
    {
        get
        {
            if (!IsAlreadyAdded || string.IsNullOrWhiteSpace(LocalPath)) return BadgeTone.NotCloned;
            if (!LocalExists) return BadgeTone.Missing;
            if (BehindCount > 0) return BadgeTone.Behind;
            if (AheadCount > 0) return BadgeTone.Ahead;
            if (ChangedFilesCount > 0) return BadgeTone.Dirty;
            return BadgeTone.UpToDate;
        }
    }

    public string ActionButtonText => IsAlreadyAdded ? "Open in Local Repos" : "⬇ Clone & Add";
    public BadgeTone ActionButtonTone => IsAlreadyAdded ? BadgeTone.AddedButton : BadgeTone.CloneButton;

    public bool CanPull => IsAlreadyAdded && LocalExists && BehindCount > 0;
    public bool CanPushNow => IsAlreadyAdded && LocalExists && AheadCount > 0 && CanPush;

    /// <summary>Local commits the signed-in account is not allowed to push: shown instead of Push.</summary>
    public bool IsPushBlocked => IsAlreadyAdded && LocalExists && AheadCount > 0 && !CanPush;
    public bool CanOpenInCockpit => IsAlreadyAdded && LocalExists;

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

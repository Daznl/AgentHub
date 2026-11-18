using AgentHub.Models;

namespace AgentHub.Tests;

/// <summary>
/// What each repository badge and button means, as the models say it. The Windows app draws these tones in the colours
/// the models used to hold (BadgeToneBrushConverterTests); these pin which tone each state gets.
/// </summary>
public class BadgeToneTests
{
    [Fact]
    public void A_local_repository_badge_follows_missing_then_behind_ahead_and_modified()
    {
        var folder = Directory.CreateTempSubdirectory("agenthub badge ").FullName;
        try
        {
            Assert.Equal(BadgeTone.Missing, new RepositoryDefinition { LocalPath = Path.Combine(folder, "gone"), Behind = 2 }.StatusTone);
            Assert.Equal(BadgeTone.Behind, new RepositoryDefinition { LocalPath = folder, Behind = 1, Ahead = 1, ChangedFiles = 1 }.StatusTone);
            Assert.Equal(BadgeTone.Ahead, new RepositoryDefinition { LocalPath = folder, Ahead = 1, ChangedFiles = 1 }.StatusTone);
            Assert.Equal(BadgeTone.Dirty, new RepositoryDefinition { LocalPath = folder, ChangedFiles = 3 }.StatusTone);
            Assert.Equal(BadgeTone.UpToDate, new RepositoryDefinition { LocalPath = folder }.StatusTone);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_GitHub_repository_sync_badge_follows_the_same_order_and_starts_at_not_cloned()
    {
        Assert.Equal(BadgeTone.NotCloned, new GitHubRepository { IsAlreadyAdded = false, LocalPath = "x", LocalExists = true }.SyncTone);
        Assert.Equal(BadgeTone.NotCloned, new GitHubRepository { IsAlreadyAdded = true, LocalPath = " " }.SyncTone);
        Assert.Equal(BadgeTone.Missing, Added(exists: false, behind: 1).SyncTone);
        Assert.Equal(BadgeTone.Behind, Added(behind: 1, ahead: 1, changed: 1).SyncTone);
        Assert.Equal(BadgeTone.Ahead, Added(ahead: 1, changed: 1).SyncTone);
        Assert.Equal(BadgeTone.Dirty, Added(changed: 1).SyncTone);
        Assert.Equal(BadgeTone.UpToDate, Added().SyncTone);
    }

    [Fact]
    public void Access_visibility_and_the_action_button_have_their_own_tones()
    {
        Assert.Equal(BadgeTone.NotCloned, new GitHubRepository { IsArchived = true, ViewerPermission = "ADMIN" }.AccessTone);
        Assert.Equal(BadgeTone.UpToDate, new GitHubRepository { ViewerPermission = "WRITE" }.AccessTone);
        Assert.Equal(BadgeTone.Behind, new GitHubRepository { ViewerPermission = "READ" }.AccessTone);
        Assert.Equal(BadgeTone.Private, new GitHubRepository { IsPrivate = true }.VisibilityTone);
        Assert.Equal(BadgeTone.Public, new GitHubRepository { IsPrivate = false }.VisibilityTone);
        Assert.Equal(BadgeTone.AddedButton, Added().ActionButtonTone);
        Assert.Equal(BadgeTone.CloneButton, new GitHubRepository().ActionButtonTone);
    }

    [Fact]
    public void Pull_push_blocked_and_cockpit_show_only_for_a_local_clone_that_needs_them()
    {
        var behind = Added(behind: 2);
        Assert.True(behind.CanPull);
        Assert.False(behind.CanPushNow);
        Assert.False(behind.IsPushBlocked);
        Assert.True(behind.CanOpenInCockpit);

        var aheadWithWrite = Added(ahead: 1, permission: "WRITE");
        Assert.True(aheadWithWrite.CanPushNow);
        Assert.False(aheadWithWrite.IsPushBlocked);

        var aheadReadOnly = Added(ahead: 1, permission: "READ");
        Assert.False(aheadReadOnly.CanPushNow);
        Assert.True(aheadReadOnly.IsPushBlocked);

        var missing = Added(exists: false, behind: 1, ahead: 1, permission: "WRITE");
        Assert.False(missing.CanPull);
        Assert.False(missing.CanPushNow);
        Assert.False(missing.IsPushBlocked);
        Assert.False(missing.CanOpenInCockpit);

        Assert.False(new GitHubRepository { BehindCount = 1, AheadCount = 1, ViewerPermission = "WRITE" }.CanPull);
    }

    private static GitHubRepository Added(bool exists = true, int behind = 0, int ahead = 0, int changed = 0, string permission = "WRITE") => new()
    {
        IsAlreadyAdded = true,
        LocalPath = "C:/work/repo",
        LocalExists = exists,
        BehindCount = behind,
        AheadCount = ahead,
        ChangedFilesCount = changed,
        ViewerPermission = permission,
    };
}

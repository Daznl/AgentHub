using AgentHub.Services;
using Xunit;

namespace AgentHub.Tests;

public class GitHubServiceTests
{
    [Fact]
    public void ParseAccounts_SingleHost_ReturnsActiveAccount()
    {
        const string json = """
            {"hosts":{"github.com":[{"state":"success","active":true,"host":"github.com","login":"octocat","tokenSource":"keyring","scopes":"gist, read:org, repo","gitProtocol":"https"}]}}
            """;

        var accounts = GitHubService.ParseAccounts(json);

        var account = Assert.Single(accounts);
        Assert.Equal("octocat", account.Login);
        Assert.Equal("github.com", account.Host);
        Assert.True(account.IsActive);
        Assert.True(account.IsHealthy);
        Assert.Equal("@octocat", account.DisplayName);
    }

    [Fact]
    public void ParseAccounts_MultipleAccountsAndHosts_ActiveFirstPerHost()
    {
        const string json = """
            {"hosts":{
              "github.com":[
                {"state":"success","active":false,"host":"github.com","login":"work-user"},
                {"state":"success","active":true,"host":"github.com","login":"personal-user"}
              ],
              "ghe.example.com":[
                {"state":"error","active":true,"host":"ghe.example.com","login":"ent-user"}
              ]}}
            """;

        var accounts = GitHubService.ParseAccounts(json);

        Assert.Equal(3, accounts.Count);
        Assert.Equal("ent-user", accounts[0].Login);
        Assert.False(accounts[0].IsHealthy);
        Assert.Equal("@ent-user (ghe.example.com) ⚠", accounts[0].DisplayName);
        Assert.Equal("personal-user", accounts[1].Login);
        Assert.True(accounts[1].IsActive);
        Assert.Equal("work-user", accounts[2].Login);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"hosts\":{}}")]
    [InlineData("{\"hosts\":[]}")]
    public void ParseAccounts_InvalidOrEmpty_ReturnsEmptyList(string json)
    {
        Assert.Empty(GitHubService.ParseAccounts(json));
    }

    [Fact]
    public void ParseRepositoryLine_OrgRepoWithReadPermission_IsCloneOnly()
    {
        const string line = """
            {"description":"Shared tools","isArchived":false,"isFork":false,"isInOrganization":true,"isPrivate":true,"name":"igo-ai-shared-tools","nameWithOwner":"IGOLimited/igo-ai-shared-tools","owner":{"__typename":"Organization","login":"IGOLimited"},"pushedAt":"2026-09-07T06:01:00Z","updatedAt":"2026-09-07T06:01:11Z","url":"https://github.com/IGOLimited/igo-ai-shared-tools","viewerPermission":"READ"}
            """;

        var repo = GitHubService.ParseRepositoryLine(line, "octocat");

        Assert.NotNull(repo);
        Assert.Equal("IGOLimited", repo!.OwnerLogin);
        Assert.True(repo.IsOwnedByOrganization);
        Assert.False(repo.IsOwnedByViewer);
        Assert.False(repo.CanPush);
        Assert.Equal("🏢 IGOLimited · clone only (read access)", repo.Category);
        Assert.Equal(20, repo.CategoryOrder);
        Assert.Equal(System.Windows.Visibility.Collapsed, repo.PushButtonVisibility);
    }

    [Theory]
    [InlineData("WRITE", true)]
    [InlineData("MAINTAIN", true)]
    [InlineData("ADMIN", true)]
    [InlineData("TRIAGE", false)]
    [InlineData("READ", false)]
    [InlineData(null, false)]
    public void ParseRepositoryLine_PermissionDeterminesPush(string? permission, bool expectedCanPush)
    {
        var permJson = permission is null ? "null" : $"\"{permission}\"";
        var line = $$"""
            {"name":"snowflake","nameWithOwner":"IGOLimited/snowflake","url":"https://github.com/IGOLimited/snowflake","isPrivate":true,"isArchived":false,"isInOrganization":true,"owner":{"__typename":"Organization","login":"IGOLimited"},"viewerPermission":{{permJson}}}
            """;

        var repo = GitHubService.ParseRepositoryLine(line, "octocat");

        Assert.NotNull(repo);
        Assert.Equal(expectedCanPush, repo!.CanPush);
        Assert.Equal(expectedCanPush ? 10 : 20, repo.CategoryOrder);
    }

    [Fact]
    public void ParseRepositoryLine_OwnRepo_IsMineEvenWithoutOwnerTypeName()
    {
        const string line = """
            {"name":"dotfiles","nameWithOwner":"octocat/dotfiles","url":"https://github.com/octocat/dotfiles","isPrivate":false,"owner":{"login":"octocat"},"viewerPermission":"ADMIN"}
            """;

        var repo = GitHubService.ParseRepositoryLine(line, "OctoCat");

        Assert.NotNull(repo);
        Assert.True(repo!.IsOwnedByViewer);
        Assert.False(repo.IsOwnedByOrganization);
        Assert.Equal(0, repo.CategoryOrder);
        Assert.Equal("👤 Yours", repo.OwnerLabel);
    }

    [Fact]
    public void ParseRepositoryLine_ArchivedRepo_NeverPushableEvenForAdmin()
    {
        const string line = """
            {"name":"legacy","nameWithOwner":"IGOLimited/legacy","url":"https://github.com/IGOLimited/legacy","isArchived":true,"isInOrganization":true,"owner":{"__typename":"Organization","login":"IGOLimited"},"viewerPermission":"ADMIN"}
            """;

        var repo = GitHubService.ParseRepositoryLine(line, "octocat");

        Assert.NotNull(repo);
        Assert.False(repo!.CanPush);
        Assert.Equal(40, repo.CategoryOrder);
        Assert.StartsWith("📦 Archived", repo.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gh: Not Found (HTTP 404)")]
    [InlineData("{\"name\":\"x\"}")]
    [InlineData("{not json")]
    public void ParseRepositoryLine_NoiseOrIncomplete_ReturnsNull(string line)
    {
        Assert.Null(GitHubService.ParseRepositoryLine(line, "octocat"));
    }

    [Fact]
    public void FirstMeaningfulLine_StripsGhStatusGlyphs()
    {
        var text = "\n! First copy your one-time code: ABCD-1234\n✓ Logged in as octocat\n";

        Assert.Equal("First copy your one-time code: ABCD-1234", GitHubService.FirstMeaningfulLine(text));
        Assert.Null(GitHubService.FirstMeaningfulLine("  \n\n"));
    }
}

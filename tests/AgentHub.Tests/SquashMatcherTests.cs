using AgentHub.Services.Timeline;

namespace AgentHub.Tests;

public class SquashMatcherTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(8));
    private static string Full(string hash) => hash.PadRight(40, '0');
    private static TimelineCommit Commit(string hash, double hours, string subject, params string[] parents) =>
        new(Full(hash), parents.Select(Full).ToList(), Day.AddHours(hours), "Daniel", subject);
    private static PullRequestFact Merged(int number, string head, string? mergeCommit) =>
        new(number, "title", head, "main", "daniel", "daniel", Day.AddHours(number), mergeCommit, 0, false, "MERGED");

    [Fact]
    public void Squash_subjects_are_the_pull_request_number_at_the_end()
    {
        Assert.True(SquashMatcher.LooksSquashed("feat: custom hostname bound (#21)"));
        Assert.True(SquashMatcher.LooksSquashed("Feat/stage r role scoped (#13) "));
        Assert.False(SquashMatcher.LooksSquashed("Merge pull request #21 from IGOLimited/feat/x"));
        Assert.False(SquashMatcher.LooksSquashed("fix (#21) then more"));
    }

    [Fact]
    public void Pull_requests_map_the_head_branch_to_a_squash_commit_in_the_log()
    {
        var c1 = Commit("1111111", 0, "initial");
        var squash = Commit("2222222", 2, "feat: the work (#21)", "1111111");
        var merge = Commit("3333333", 3, "Merge pull request #22 from IGOLimited/feat/y", "2222222", "f111111");
        var prs = new[]
        {
            Merged(21, "feat/x", squash.Hash),
            Merged(22, "feat/y", merge.Hash),           // a real merge commit: the graph follows it already
            Merged(23, "feat/gone", Full("9999999")),   // not in the log
            Merged(24, "", squash.Hash)
        };

        var map = SquashMatcher.FromPullRequests(prs, [merge, squash, c1]);

        Assert.Equal(new Dictionary<string, string> { ["feat/x"] = squash.Hash }, map);
        Assert.Empty(SquashMatcher.FromPullRequests(null, [c1]));
    }

    [Fact]
    public void Fingerprint_ignores_line_order_and_line_endings()
    {
        var a = "3\t1\tsrc/a.cs\r\n0\t2\tdocs/b.md\r\n";
        var b = "0\t2\tdocs/b.md\n3\t1\tsrc/a.cs\n";
        Assert.Equal(SquashMatcher.Fingerprint(a), SquashMatcher.Fingerprint(b));
        Assert.NotEqual(SquashMatcher.Fingerprint(a), SquashMatcher.Fingerprint("3\t2\tsrc/a.cs\n0\t2\tdocs/b.md\n"));
        Assert.Equal("", SquashMatcher.Fingerprint("\r\n"));
    }
}

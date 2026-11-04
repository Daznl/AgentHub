using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentHub.Services;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Instructions;
using AgentHub.Services.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace AgentHub.Tests;

public class TimelineReviewTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Day = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = Day.AddDays(1);

    private static DateTimeOffset At(double hours) => Day.AddHours(hours);

    private static TimelineSessionInput Session(string name, double startHours, double endHours, string folder = @"C:\repo", params string[] mentions) =>
        new(new SessionSummary($@"C:\sessions\{name}.jsonl", ContextSource.ClaudeCode, name, "first prompt of " + name, At(startHours), At(endHours), 100),
            At(endHours), mentions.ToHashSet(StringComparer.Ordinal), folder);

    private static string Full(string hash) => hash.PadRight(40, '0');

    private static TimelineCommit Commit(string hash, double hours, string subject = "work", params string[] parents) =>
        new(Full(hash), parents.Select(Full).ToList(), At(hours), "Daniel", subject);

    private static TimelineBranchTip Tip(string name, string hash, bool remote = false) => new(Full(hash), name, remote);

    private static TimelineReviewInput Input(TimelineModel model, IReadOnlyList<TimelineBranchTip> tips, IEnumerable<string>? touching = null,
        List<PullRequestFact>? prs = null, Dictionary<string, TranscriptText>? transcripts = null, string? userFile = "# Rules\r\n- be concise\r\n", bool pipeline = false) =>
        new(model, "repo", tips, (touching ?? []).Select(Full).ToHashSet(StringComparer.Ordinal), prs, transcripts ?? new(StringComparer.OrdinalIgnoreCase), userFile,
            [new RepoFileFact("CLAUDE.md", true, 1200, ["# Project", "## Where things stand"]), new RepoFileFact("AGENTS.md", false, 0, []),
             new RepoFileFact(".agenthub/handoff.md", false, 0, []), new RepoFileFact("docs/pipeline-status.md", pipeline, pipeline ? 10 : 0, [])], Now);

    private static HistoryEntry Prompt(string text, double hours) => new(HistoryKind.Prompt, text, At(hours));

    /// <summary>
    /// A small project: main with a root, a direct commit and a merge commit; feat/x merged by that merge commit and
    /// still having a tip; a session that made the feature commits, a long session with none, and an outside commit.
    /// </summary>
    private static (TimelineModel Model, List<TimelineBranchTip> Tips, TimelineSessionInput Feature, TimelineSessionInput Idle) Project()
    {
        var root = Commit("a000000", 0, "init");
        var direct = Commit("a100000", 1, "chore: tweak", "a000000");
        var f1 = Commit("f100000", 2.2, "feat: part one", "a100000");
        var f2 = Commit("f200000", 2.6, "feat: part two", "f100000");
        var merge = Commit("m000000", 3, "Merge pull request #7 from me/feat/x", "a100000", "f200000");
        var squash = Commit("s000000", 5, "feat: later thing (#8)", "m000000");
        var outside = Commit("o000000", 20, "docs: by hand", "s000000");
        var feature = Session("feature", 2, 2.8);
        var idle = Session("idle", 6, 12); // six hours, no commits
        var tips = new List<TimelineBranchTip> { Tip("main", "o000000"), Tip("main", "o000000", remote: true), Tip("feat/x", "f200000") };
        var model = TimelineBuilder.Build([feature, idle], [outside, squash, merge, f2, f1, direct, root], tips, Now);
        return (model, tips, feature, idle);
    }

    [Fact]
    public void Session_ids_count_from_the_newest()
    {
        var (model, _, feature, idle) = Project();
        var ids = TimelineReview.SessionIds(model);
        Assert.Equal("S1", ids[idle.Summary.FilePath]);
        Assert.Equal("S2", ids[feature.Summary.FilePath]);
    }

    [Fact]
    public void Checks_find_long_sessions_direct_commits_merge_commits_and_leftover_branches()
    {
        var (model, tips, _, _) = Project();
        var facts = TimelineReview.Compute(Input(model, tips, touching: ["f200000"]));
        var by = facts.Checks.ToDictionary(c => c.Id);

        Assert.Equal(["S1"], by["A1"].Offenders);                       // idle ran six hours
        Assert.Equal(0, by["A2"].Failing);                               // the feature session touched a context file
        Assert.Equal(["S1"], by["A3"].Offenders);                        // no commits
        Assert.Equal(0, by["A4"].Failing);
        Assert.Equal(0, by["B1"].Failing);                               // feat/x is well named
        Assert.Equal(["a100000"[..7], "o000000"[..7]], by["B2"].Offenders.OrderBy(o => o)); // tweak and the hand commit; the squash (#8) is not direct
        Assert.Equal(1, by["B3"].Failing);                               // one merge commit against one squash
        Assert.Equal(2, by["B3"].Total);
        Assert.Equal(["feat/x"], by["B4"].Offenders);                 // merged yet still a tip
        Assert.False(by["B5"].Applies);                                  // no pull requests known
        Assert.False(facts.HasPullRequests);
        Assert.NotNull(facts.GitHubScore);
        Assert.Equal(4, facts.GitHubChecks);
    }

    [Fact]
    public void Pull_request_facts_feed_the_review_checks()
    {
        var (model, tips, _, _) = Project();
        var prs = new List<PullRequestFact>
        {
            new(7, "Feature x", "feat/x", "main", "daniel", "daniel", At(3), Full("m000000"), 0, false, "MERGED"),
            new(8, "A very long title that goes on and on and on well past seventy characters in total", "Feature-Y", "feat/x", "daniel", "someone", At(5), Full("s000000"), 1, false, "MERGED"),
        };
        var facts = TimelineReview.Compute(Input(model, tips, prs: prs));
        var by = facts.Checks.ToDictionary(c => c.Id);

        Assert.Equal(["Feature-Y"], by["B1"].Offenders);
        Assert.Equal(["#7"], by["B5"].Offenders);
        Assert.Equal(["#7"], by["B6"].Offenders);
        Assert.Equal(["#8 → feat/x"], by["B7"].Offenders);
        Assert.Equal(["#8"], by["B8"].Offenders);
        Assert.True(facts.HasPullRequests);
        Assert.Equal(8, facts.GitHubChecks);
    }

    [Fact]
    public void Overlapping_sessions_in_one_folder_are_a_fact()
    {
        var a = Session("a", 0, 3);
        var b = Session("b", 1, 2);
        var c = Session("c", 1, 2, folder: @"C:\wt\other");
        var model = TimelineBuilder.Build([a, b, c], [Commit("a000000", 0)], [Tip("main", "a000000")], Now);
        var facts = TimelineReview.Compute(Input(model, [Tip("main", "a000000")]));
        var a4 = facts.Checks.Single(x => x.Id == "A4");
        Assert.Equal(2, a4.Failing);
        Assert.Single(a4.Offenders);
        Assert.Contains("+", a4.Offenders[0]);
    }

    [Fact]
    public void Digest_holds_project_facts_sessions_branches_rules_and_the_user_file()
    {
        var (model, tips, feature, _) = Project();
        var transcripts = new Dictionary<string, TranscriptText>(StringComparer.OrdinalIgnoreCase)
        {
            [feature.Summary.FilePath] = new([Prompt("build the thing\nplease", 2.1), Prompt("now test it", 2.5)], "Done: the thing is built and tested.")
        };
        var input = Input(model, tips, transcripts: transcripts);
        var digest = TimelineReview.Build(input, TimelineReview.Compute(input), ReviewKind.Interaction);
        var text = digest.Text;
        output.WriteLine(text);

        Assert.Contains("name: repo", text);
        Assert.Contains("trunk: main", text);
        Assert.Contains("docs/pipeline-status.md: absent", text);
        Assert.Contains("CLAUDE.md: 1.2 KB; headings: # Project | ## Where things stand", text);
        Assert.Contains("github facts: unavailable", text);
        Assert.Contains("A1 Sessions open longer than 4 h", text);
        Assert.DoesNotContain("B2 Commits made straight on main", text);
        Assert.Contains("S2 · 2026-11-03", text);
        Assert.Contains("lane feat/x · 2 commits · 2 prompts · did not touch", text);
        Assert.Contains("build the thing please", text);
        Assert.Contains("last reply: Done: the thing is built and tested.", text);
        Assert.Contains("S1 · ", text);
        Assert.Contains("about: idle", text);                     // no transcript read for it: title only
        var github = TimelineReview.Build(input, TimelineReview.Compute(input), ReviewKind.GitHub).Text;
        Assert.Contains("feat/x: 2 commits", github);
        Assert.Contains("merged by merge commit m000000", github);
        Assert.Contains("<outside-sessions>", text);
        Assert.Contains("o000000 docs: by hand", text);
        Assert.Contains("The user and Claude:", text);
        Assert.DoesNotContain("pipeline projects", text);
        Assert.Contains("<user-claude-md>\n# Rules\n- be concise", text.Replace("\r\n", "\n"));
        Assert.Equal(feature.Summary.FilePath, digest.SessionRowKeys["S2"]);
        Assert.True(text.Length < TimelineReview.DigestBudget);
    }

    [Fact]
    public void Digest_thins_older_sessions_to_stay_within_budget()
    {
        var sessions = Enumerable.Range(0, 60).Select(i => Session($"s{i:00}", i * 0.1, i * 0.1 + 0.05)).ToList();
        var model = TimelineBuilder.Build(sessions, [Commit("a000000", -1)], [Tip("main", "a000000")], Now);
        var prompts = Enumerable.Range(0, TimelineReview.MaxPromptsPerSession).Select(i => Prompt(new string('x', TimelineReview.PromptChars) + " " + i, 0)).ToList();
        var transcripts = sessions.ToDictionary(s => s.Summary.FilePath, _ => new TranscriptText(prompts, new string('r', TimelineReview.ReplyChars)), StringComparer.OrdinalIgnoreCase);
        var input = Input(model, [Tip("main", "a000000")], transcripts: transcripts);

        var digest = TimelineReview.Build(input, TimelineReview.Compute(input), ReviewKind.Interaction);

        Assert.True(digest.Text.Length <= TimelineReview.DigestBudget + 2000, $"digest is {digest.Text.Length} chars");
        Assert.Contains("S1 · ", digest.Text);
        Assert.Contains("S60 · ", digest.Text);
        Assert.Contains("  prompts:", digest.Text);                      // the newest got detail
        Assert.Contains("about: s00", digest.Text);        // the oldest did not
    }

    [Fact]
    public void Pipeline_rules_only_go_in_when_the_project_has_pipeline_status()
    {
        Assert.Contains("Pipeline projects", TimelineReview.RulesFor(ReviewKind.Interaction, hasPipeline: true));
        Assert.Contains("Pipeline projects", TimelineReview.RulesFor(ReviewKind.GitHub, hasPipeline: true));
        Assert.DoesNotContain("Pipeline projects", TimelineReview.RulesFor(ReviewKind.GitHub, hasPipeline: false));
        Assert.DoesNotContain("Pipeline projects", TimelineReview.RulesFor(ReviewKind.Interaction, hasPipeline: false));
        var (model, tips, _, _) = Project();
        var input = Input(model, tips, pipeline: true);
        Assert.Contains("present, so the pipeline-stage rules apply", TimelineReview.Build(input, TimelineReview.Compute(input), ReviewKind.Interaction).Text);
    }

    [Fact]
    public void Sessions_reply_is_parsed_with_claudes_score()
    {
        var (model, tips, _, _) = Project();
        var facts = TimelineReview.Compute(Input(model, tips));
        var json = """
            {"summary":"Decent.","score":7,"verdict":"Mostly fine.","findings":[
              {"what":"One session ran six hours.","evidence":"S1","instead":"Restart after the handoff.","who":"you"},
              {"what":"Bad who.","evidence":"","instead":"","who":"nobody"}],
             "additions":[{"title":"## Restart long sessions","markdown":"- After 4 hours, commit, rewrite the handoff and start a new session.","why":"S1 ran six hours."},
                          {"title":"Empty","markdown":"","why":"dropped"}]}
            """;
        using var doc = JsonDocument.Parse(json);

        var report = TimelineReview.Parse(doc.RootElement, facts, ReviewKind.Interaction);

        Assert.Equal(ReviewKind.Interaction, report.Kind);
        Assert.Equal("Decent.", report.Summary);
        Assert.Equal(7, report.Part.Score);
        Assert.Equal("Claude's judgement", report.Part.ScoreSource);
        Assert.Equal(2, report.Part.Findings.Count);
        Assert.Equal("both", report.Part.Findings[1].Who);
        var addition = Assert.Single(report.Additions);
        Assert.Equal("Restart long sessions", addition.Title);
    }

    [Fact]
    public void GitHub_reply_is_scored_from_the_checks_not_the_model()
    {
        var (model, tips, _, _) = Project();
        var facts = TimelineReview.Compute(Input(model, tips));
        var json = """{"summary":"Direct.","score":9,"verdict":"Direct commits.","findings":[{"what":"Commits straight on main.","evidence":"a100000, o000000","instead":"Branch and PR.","who":"both"}],"additions":[]}""";
        using var doc = JsonDocument.Parse(json);

        var report = TimelineReview.Parse(doc.RootElement, facts, ReviewKind.GitHub);

        Assert.Equal(ReviewKind.GitHub, report.Kind);
        Assert.Equal(facts.GitHubScore, report.Part.Score);
        Assert.NotEqual(9, report.Part.Score);
        Assert.Equal("from 4 checks", report.Part.ScoreSource);
        Assert.Equal("Direct commits.", report.Part.Verdict);
        Assert.Empty(report.Additions);
    }

    [Fact]
    public void GitHub_digest_has_branches_and_pull_requests_but_no_prompts()
    {
        var (model, tips, feature, _) = Project();
        var transcripts = new Dictionary<string, TranscriptText>(StringComparer.OrdinalIgnoreCase) { [feature.Summary.FilePath] = new([Prompt("secret prompt", 2.1)], "last reply text") };
        var prs = new List<PullRequestFact> { new(7, "Feature x", "feat/x", "main", "daniel", "daniel", At(3), Full("m000000"), 0, false, "MERGED") };
        var input = Input(model, tips, prs: prs, transcripts: transcripts);
        var facts = TimelineReview.Compute(input);

        var github = TimelineReview.Build(input, facts, ReviewKind.GitHub).Text;
        var sessions = TimelineReview.Build(input, facts, ReviewKind.Interaction).Text;

        Assert.Contains("<branches>", github);
        Assert.Contains("<pull-requests>", github);
        Assert.Contains("#7 \"Feature x\"", github);
        Assert.Contains("B5 Pull requests merged by their own author: 1 of 1", github);
        Assert.DoesNotContain("A1 Sessions open", github);
        Assert.DoesNotContain("secret prompt", github);
        Assert.Contains("The GitHub cycle:", github);
        Assert.DoesNotContain("The user and Claude:", github);

        Assert.DoesNotContain("<branches>", sessions);
        Assert.DoesNotContain("<pull-requests>", sessions);
        Assert.Contains("secret prompt", sessions);
        Assert.Contains("A1 Sessions open", sessions);
        Assert.DoesNotContain("B5 Pull requests", sessions);
        Assert.Contains("The user and Claude:", sessions);
    }

    [Fact]
    public void Additions_become_append_edits_that_skip_headings_the_file_already_has()
    {
        var facts = new TimelineFacts([], false, false, null, 0);
        var report = new TimelineReport(ReviewKind.Interaction, "", new ReviewPart(null, "", "", []),
            [new ReviewAddition("Verify before claiming", "- Run git status first.", "because"), new ReviewAddition("Rules", "- dup", "already there")], facts);
        const string file = "# Rules\r\n- be concise\r\n";

        var suggestion = TimelineReview.ToSuggestion(report, file);

        var edit = Assert.Single(suggestion.Edits);
        Assert.Equal("", edit.Start);
        Assert.Contains("1 already covered", suggestion.Summary);
        var plan = InstructionReview.Plan(file, suggestion.Edits);
        Assert.Equal(["because"], plan.Reasons);
        Assert.Equal("# Rules\r\n- be concise\r\n\r\n## Verify before claiming\r\n- Run git status first.\r\n", LineDiff.Merge(plan.Segments, _ => true));
    }

    [Fact]
    public void Markdown_report_lists_score_findings_checks_and_additions()
    {
        var (model, tips, _, _) = Project();
        var facts = TimelineReview.Compute(Input(model, tips));
        var report = new TimelineReport(ReviewKind.GitHub, "Summary.", new ReviewPart(facts.GitHubScore, "from 4 checks", "Verdict B.", [new ReviewFinding("What.", "S1", "Instead.", "you")]),
            [new ReviewAddition("Title", "- line", "why")], facts);

        var md = TimelineReview.ToMarkdown(report, "repo", Now);

        Assert.Contains("# Timeline review · GitHub cycle · repo", md);
        Assert.Contains($"## GitHub cycle · {facts.GitHubScore}/10 (from 4 checks)", md);
        Assert.Contains("1. **What.** · for you", md);
        Assert.Contains("   Evidence: S1", md);
        Assert.Contains("| B4 Branches still present after being merged | 1 | 1 | feat/x |", md);
        Assert.Contains("| B5 Pull requests merged by their own author | n/a | 0 |  |", md);
        Assert.DoesNotContain("| A1 ", md);
        Assert.Contains("### Title\r\n_why_\r\n\r\n- line", md.Replace("\n", "\r\n").Replace("\r\r", "\r"));
    }

    [Fact]
    public void Arguments_keep_the_same_lock_down_as_the_file_review()
    {
        foreach (var kind in new[] { ReviewKind.Interaction, ReviewKind.GitHub })
        {
            var args = ClaudePrint.Arguments(TimelineReview.PromptFor(kind), TimelineReview.SchemaFor(kind)).ToList();
            Assert.Equal("", args[args.IndexOf("--tools") + 1]);
            Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);
            Assert.Contains("--no-session-persistence", args);
            using var schema = JsonDocument.Parse(TimelineReview.SchemaFor(kind)); // well-formed
            var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(kind == ReviewKind.Interaction, required.Contains("score"));
        }
    }

    [LiveFact("claude", optIn: "AGENTHUB_LIVE_REVIEW")]
    public async Task Live_sessions_review_of_a_small_project_returns_a_score()
    {
        var (model, tips, feature, _) = Project();
        var transcripts = new Dictionary<string, TranscriptText>(StringComparer.OrdinalIgnoreCase)
        {
            [feature.Summary.FilePath] = new([Prompt("build the thing", 2.1), Prompt("did you commit? please commit now", 2.5), Prompt("I told you to always run the tests first", 2.6)], "Committed. Tests were not run.")
        };
        var input = Input(model, tips, transcripts: transcripts);
        var facts = TimelineReview.Compute(input);
        var digest = TimelineReview.Build(input, facts, ReviewKind.Interaction);

        using var reply = await ClaudePrint.RunAsync(new ProcessRunner(), "claude", TimelineReview.PromptFor(ReviewKind.Interaction), TimelineReview.SchemaFor(ReviewKind.Interaction), digest.Text, CancellationToken.None);
        var report = TimelineReview.Parse(reply.RootElement, facts, ReviewKind.Interaction);

        output.WriteLine(TimelineReview.ToMarkdown(report, "repo", Now));
        Assert.NotNull(report.Part.Score);
        Assert.NotEmpty(report.Part.Verdict);
    }
}

public class ReviewTimingsTests
{
    [Fact]
    public void Estimate_uses_a_built_in_rate_until_there_is_history()
    {
        var estimate = ReviewTimings.Estimate([], 22 * 1024);
        Assert.InRange(estimate.TotalSeconds, 110, 125); // 40 s + 3.5 s/KB × 22 KB
        Assert.Equal("a built-in rate", ReviewTimings.Basis([]));
    }

    [Fact]
    public void Estimate_takes_the_median_rate_of_earlier_runs()
    {
        List<ReviewTimings.Run> history =
        [
            new(22 * 1024, 118),  // 3.5 s/KB after the overhead
            new(10 * 1024, 140),  // 10 s/KB: a slow one
            new(40 * 1024, 200),  // 4 s/KB
            new(5 * 1024, 30),    // under the overhead: ignored
        ];
        var estimate = ReviewTimings.Estimate(history, 30 * 1024);
        Assert.InRange(estimate.TotalSeconds, 155, 165); // 40 + 4 × 30
        Assert.Equal("4 earlier reviews on this machine", ReviewTimings.Basis(history));
    }

    [Fact]
    public void Estimates_read_as_rounded_minutes()
    {
        Assert.Equal("about 45 s", ReviewTimings.Describe(TimeSpan.FromSeconds(44)));
        Assert.Equal("about 1 min", ReviewTimings.Describe(TimeSpan.FromSeconds(65)));
        Assert.Equal("about 1½ min", ReviewTimings.Describe(TimeSpan.FromSeconds(100)));
        Assert.Equal("about 2 min", ReviewTimings.Describe(TimeSpan.FromSeconds(118)));
        Assert.Equal("about 4 min", ReviewTimings.Describe(TimeSpan.FromSeconds(230)));
    }
}

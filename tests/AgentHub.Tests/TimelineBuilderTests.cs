using System;
using System.Collections.Generic;
using System.Linq;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Timeline;
using Xunit;

namespace AgentHub.Tests;

public class TimelineBuilderTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Now = Day.AddDays(1);

    private static DateTimeOffset At(double hours) => Day.AddHours(hours);

    private static TimelineSessionInput Session(string name, double startHours, double endHours, params string[] mentions) =>
        new(new SessionSummary($@"C:\sessions\{name}.jsonl", ContextSource.ClaudeCode, name, null, At(startHours), At(endHours), 100),
            At(endHours), mentions.ToHashSet(StringComparer.Ordinal));

    private static string Full(string hash) => hash.PadRight(40, '0');

    private static TimelineCommit Commit(string hash, double hours, string subject = "work", params string[] parents) =>
        new(Full(hash), parents.Select(Full).ToList(), At(hours), "Daniel", subject);

    private static TimelineBranchTip Tip(string name, string hash, bool remote = false) => new(Full(hash), name, remote);

    private static TimelineRow RowOf(TimelineModel model, TimelineSessionInput session) => model.Rows.Single(r => r.Session == session);

    private static TimelineModel Build(IReadOnlyList<TimelineSessionInput> sessions, IReadOnlyList<TimelineCommit> commits, params TimelineBranchTip[] tips)
    {
        var model = TimelineBuilder.Build(sessions, commits, tips, Now);
        TimelineGraphInvariantTests.AssertConnected(model);
        return model;
    }

    // ---- attribution ----

    [Fact]
    public void Attribute_GivesCommitToTheOnlySessionRunning()
    {
        var a = Session("a", 0, 2);
        var inWindow = Commit("aaaaaaa", 1);
        var justBefore = Commit("bbbbbbb", -1.0 / 60); // one minute before the session's first entry
        var justAfter = Commit("ccccccc", 2 + 4.0 / 60); // four minutes after its last
        var tooLate = Commit("ddddddd", 2 + 6.0 / 60);

        var (bySession, outside) = TimelineBuilder.Attribute([a], [inWindow, justBefore, justAfter, tooLate]);

        Assert.Equal([inWindow, justBefore, justAfter], bySession[a]);
        Assert.Equal([tooLate], outside);
    }

    [Fact]
    public void Attribute_WithTwoSessionsRunning_TheOneMentioningTheHashWins()
    {
        var a = Session("a", 0, 4);
        var b = Session("b", 1, 3, "bbbbbbb");
        var mentioned = Commit("bbbbbbb", 2);
        var unmentioned = Commit("ccccccc", 2.5);

        var (bySession, outside) = TimelineBuilder.Attribute([a, b], [mentioned, unmentioned]);

        Assert.Equal([mentioned], bySession[b]);
        Assert.Empty(bySession[a]);
        Assert.Equal([unmentioned], outside);
    }

    [Fact]
    public void Attribute_WithBothMentioning_TheLaterStartedSessionWins()
    {
        var a = Session("a", 0, 4, "abcdef0");
        var b = Session("b", 1, 3, "abcdef0");
        var commit = Commit("abcdef0", 2);

        var (bySession, _) = TimelineBuilder.Attribute([a, b], [commit]);

        Assert.Equal([commit], bySession[b]);
        Assert.Empty(bySession[a]);
    }

    [Fact]
    public void Attribute_MentionOutsideTheTimeWindowDoesNotCount()
    {
        // Sessions quote old hashes from git log; a mention alone never claims a commit.
        var a = Session("a", 5, 6, "abcdef0");
        var commit = Commit("abcdef0", 1);

        var (bySession, outside) = TimelineBuilder.Attribute([a], [commit]);

        Assert.Empty(bySession[a]);
        Assert.Equal([commit], outside);
    }

    [Fact]
    public void Attribute_ASessionOpenForDaysOnlyCountsWhileItIsActive()
    {
        // "idle" has the commit inside its window but wrote nothing near it; "busy" has a tool call seconds before.
        var idle = Session("idle", -48, 48) with { ActiveMinutes = Minutes(At(-48), At(-47)) };
        var busy = Session("busy", 0, 2) with { ActiveMinutes = Minutes(At(1).AddSeconds(-20), At(1).AddSeconds(5)) };
        var commit = Commit("aaaaaaa", 1);

        var (bySession, outside) = TimelineBuilder.Attribute([idle, busy], [commit]);

        Assert.Equal([commit], bySession[busy]);
        Assert.Empty(bySession[idle]);
        Assert.Empty(outside);
    }

    [Fact]
    public void Attribute_WithNoSessionActive_TheCommitStaysOutside()
    {
        // The session is open but idle: a commit then was made by hand or on GitHub, not by it.
        var a = Session("a", 0, 2) with { ActiveMinutes = Minutes(At(0)) };
        var byHand = Commit("aaaaaaa", 1);

        var (bySession, outside) = TimelineBuilder.Attribute([a], [byHand]);

        Assert.Empty(bySession[a]);
        Assert.Equal([byHand], outside);
    }

    [Fact]
    public void Attribute_TwoActiveSessionsStillNeedAMention()
    {
        var a = Session("a", 0, 2) with { ActiveMinutes = Minutes(At(1)) };
        var b = Session("b", 0, 2, "aaaaaaa") with { ActiveMinutes = Minutes(At(1)) };
        var commit = Commit("aaaaaaa", 1);
        var silent = Commit("bbbbbbb", 1.01);

        var (bySession, outside) = TimelineBuilder.Attribute([a, b], [commit, silent]);

        Assert.Equal([commit], bySession[b]);
        Assert.Equal([silent], outside);
    }

    [Fact]
    public void Attribute_TwoActiveSessionsWithoutAMention_TheOneWhoseTranscriptHasTheSubjectWins()
    {
        var a = Session("a", 0, 2) with { ActiveMinutes = Minutes(At(1)), MentionsBetween = (_, _, _) => false };
        var b = Session("b", 0, 2) with { ActiveMinutes = Minutes(At(1)), MentionsBetween = (text, from, to) => text == "feat(clusters): location clusters page" && from < At(1) && to > At(1) };
        var commit = Commit("aaaaaaa", 1, "feat(clusters): location clusters page");

        var (bySession, outside) = TimelineBuilder.Attribute([a, b], [commit]);

        Assert.Equal([commit], bySession[b]);
        Assert.Empty(outside);
    }

    [Theory]
    [InlineData("feat(maps): the picture — whiten the paper, contrast, brightness, colour, fade a colour, for every sheet", "feat(maps): the picture — whiten the paper, contrast, bright")]
    [InlineData("fix(maps): quote \"this\" properly", "fix(maps): quote")]
    [InlineData("fix: quote \"this\"", null)]
    [InlineData("fix(snow): every connection sets QUERY_TAG", "fix(snow): every connection sets QUERY_TAG")]
    [InlineData("wip", null)]
    public void SubjectFragment_StopsAtQuotesAndSixtyCharacters(string subject, string? expected) =>
        Assert.Equal(expected, TimelineBuilder.SubjectFragment(subject));

    private static HashSet<long> Minutes(params DateTimeOffset[] times) => times.Select(t => t.ToUnixTimeSeconds() / 60).ToHashSet();

    // ---- branch graph ----

    [Fact]
    public void Graph_TrunkIsMainsFirstParentChain_LocalAndRemoteTipsBoth()
    {
        var c1 = Commit("1111111", 0, "initial");
        var c2 = Commit("2222222", 1, "second", "1111111");
        var c3 = Commit("3333333", 2, "third (only on origin)", "2222222");

        var (branches, trunk) = BranchGraph.Assign([c3, c2, c1], [Tip("main", "2222222"), Tip("main", "3333333", remote: true)]);

        Assert.Equal("main", trunk);
        var main = Assert.Single(branches);
        Assert.True(main.IsTrunk);
        Assert.Equal(3, main.Commits.Count);
        Assert.All(new[] { c1, c2, c3 }, c => Assert.Equal(main.Key, c.Branch));
    }

    [Fact]
    public void Graph_MasterIsTheTrunkWhenThereIsNoMain()
    {
        var c1 = Commit("1111111", 0);
        var (_, trunk) = BranchGraph.Assign([c1], [Tip("master", "1111111"), Tip("feat/x", "1111111")]);
        Assert.Equal("master", trunk);
    }

    [Fact]
    public void Graph_AMergedBranchIsRecoveredFromTheMergeCommit_NamedFromTheSubjectWhenItsTipIsGone()
    {
        var c1 = Commit("1111111", 0, "initial");
        var f1 = Commit("f111111", 1, "feature work", "1111111");
        var f2 = Commit("f222222", 2, "more feature work", "f111111");
        var merge = Commit("3333333", 3, "Merge pull request #7 from IGOLimited/feat/x", "1111111", "f222222");

        var (branches, _) = BranchGraph.Assign([merge, f2, f1, c1], [Tip("main", "3333333")]);

        var feature = Assert.Single(branches, b => !b.IsTrunk);
        Assert.Equal("feat/x", feature.Name);
        Assert.Equal(merge.Hash, feature.MergeHash);
        Assert.Equal([f2, f1], feature.Commits);
        Assert.Equal(feature.Key, f1.Branch);
        Assert.Equal(branches[0].Key, merge.Branch); // the merge commit itself is on the trunk
    }

    [Fact]
    public void Graph_AMergedBranchKeepsItsTipNameWhenTheTipStillExists()
    {
        var c1 = Commit("1111111", 0);
        var f1 = Commit("f111111", 1, "work", "1111111");
        var merge = Commit("3333333", 3, "Merge branch 'whatever'", "1111111", "f111111");

        var (branches, _) = BranchGraph.Assign([merge, f1, c1], [Tip("main", "3333333"), Tip("feat/kept", "f111111")]);

        Assert.Equal("feat/kept", Assert.Single(branches, b => !b.IsTrunk).Name);
    }

    [Fact]
    public void Graph_AMergeOfMainIntoAFeatureClaimsNothingNew()
    {
        var c1 = Commit("1111111", 0);
        var c2 = Commit("2222222", 1, "on main", "1111111");
        var f1 = Commit("f111111", 0.5, "work", "1111111");
        var back = Commit("f222222", 2, "Merge remote-tracking branch 'origin/main' into feat/x", "f111111", "2222222");
        var merge = Commit("3333333", 3, "Merge pull request #8 from IGOLimited/feat/x", "2222222", "f222222");

        var (branches, _) = BranchGraph.Assign([merge, back, f1, c2, c1], [Tip("main", "3333333")]);

        var feature = Assert.Single(branches, b => !b.IsTrunk);
        Assert.Equal([back, f1], feature.Commits);
        Assert.Equal(branches[0].Key, c2.Branch);
    }

    [Fact]
    public void Graph_AnUnmergedTipIsAnOpenBranch()
    {
        var c1 = Commit("1111111", 0);
        var f1 = Commit("f111111", 1, "work", "1111111");
        var f2 = Commit("f222222", 2, "more", "f111111");

        var (branches, _) = BranchGraph.Assign([f2, f1, c1], [Tip("main", "1111111"), Tip("feat/open", "f222222", remote: true)]);

        var feature = Assert.Single(branches, b => !b.IsTrunk);
        Assert.Equal("feat/open", feature.Name);
        Assert.Null(feature.MergeHash);
        Assert.Equal([f2, f1], feature.Commits);
    }

    [Fact]
    public void Graph_ABranchStackedOnAnotherOnlyClaimsItsOwnCommits()
    {
        var c1 = Commit("1111111", 0);
        var a1 = Commit("a111111", 1, "a", "1111111");
        var b1 = Commit("b111111", 2, "b on top of a", "a111111");

        var (branches, _) = BranchGraph.Assign([b1, a1, c1], [Tip("main", "1111111"), Tip("feat/a", "a111111"), Tip("feat/b", "b111111")]);

        Assert.Equal([a1], branches.Single(b => b.Name == "feat/a").Commits);
        Assert.Equal([b1], branches.Single(b => b.Name == "feat/b").Commits);
    }

    [Fact]
    public void Graph_HistoryWithNoCommonAncestorIsAnImportedLineage_JoinedWhereTheTwoMeet()
    {
        // Two repositories: ours (root c1) and one brought in whole (root x1), joined by a merge on a feature branch
        // that then reached main through a pull request.
        var c1 = Commit("1111111", 0, "initial");
        var x1 = Commit("x111111", -200, "Initial commit (other repo)");
        var x2 = Commit("x222222", -100, "other repo work", "x111111");
        var f1 = Commit("f111111", 1, "feature", "1111111");
        var join = Commit("f222222", 2, "chore: bring the other repo in with its history", "f111111", "x222222");
        var merge = Commit("3333333", 3, "Merge pull request #9 from IGOLimited/chore/bring-in", "1111111", "f222222");

        var (branches, _) = BranchGraph.Assign([merge, join, f1, x2, x1, c1], [Tip("main", "3333333")]);

        var imported = Assert.Single(branches, b => b.IsImported);
        Assert.Equal("imported history", imported.Name);
        Assert.Equal([x2, x1], imported.Commits);
        Assert.Equal(join.Hash, imported.MergeHash);
        var feature = Assert.Single(branches, b => !b.IsTrunk && !b.IsImported);
        Assert.Equal("chore/bring-in", feature.Name);
        Assert.Equal([join, f1], feature.Commits);

        var model = Build([], [merge, join, f1, x2, x1, c1], Tip("main", "3333333"));
        var rowX = model.Rows.Single(r => r.Commits.Contains(x2)); // x1, the earliest commit, is the Start row, on the imported lane
        Assert.True(rowX.Branch!.IsImported);
        Assert.True(model.Rows[0].Branch!.IsImported);
        var lane = Assert.Single(rowX.Lines, l => l.Key == imported.Key);
        Assert.False(lane.Fork); // no common ancestor: nothing to fork from
        Assert.True(model.Branches.Single(b => b.IsImported).MergesBack);
    }

    [Fact]
    public void Graph_TheProjectsRootIsTheOneWithTheMostDescendants_NotGitHubsInitialCommit()
    {
        // GitHub made "Initial commit"; the real work (root w1) was merged into it, so main's first-parent chain ends at
        // the one-commit root. The work is still the project, not imported history.
        var gh = Commit("9999999", 0, "Initial commit");
        var w1 = Commit("a111111", -10, "feat: the real start");
        var w2 = Commit("a222222", -5, "more", "a111111");
        var join = Commit("3333333", 1, "Merge branch 'main' of github", "9999999", "a222222");
        var later = Commit("4444444", 2, "later", "3333333");

        var (branches, _) = BranchGraph.Assign([later, join, w2, w1, gh], [Tip("main", "4444444")]);

        Assert.DoesNotContain(branches, b => b.IsImported);
        Assert.Equal(branches[0].Key, w1.Branch); // reached from the trunk's merge, filed under the trunk's merged branch or trunk
    }

    [Fact]
    public void Graph_WithoutTipsEverythingIsTrunk()
    {
        var c1 = Commit("1111111", 0);
        var c2 = Commit("2222222", 1, "x", "1111111");
        var (branches, _) = BranchGraph.Assign([c2, c1], []);
        Assert.Equal(2, Assert.Single(branches).Commits.Count);
    }

    [Theory]
    [InlineData("Merge pull request #94 from IGOLimited/feat/file-sure-sheets", "feat/file-sure-sheets")]
    [InlineData("Merge branch 'fix/thing'", "fix/thing")]
    [InlineData("Merge branch 'fix/thing' into main", "fix/thing")]
    [InlineData("Merge remote-tracking branch 'origin/feat/x'", "feat/x")]
    [InlineData("chore: ordinary commit", null)]
    public void Graph_MergedBranchNameComesFromTheSubject(string subject, string? expected) =>
        Assert.Equal(expected, BranchGraph.MergedBranchName(subject));

    // ---- rows ----

    [Fact]
    public void Build_StartsWithTheFirstCommitThenSessionsAndOutsideCommitsInTimeOrder()
    {
        var first = Commit("1111111", -24, "initial");
        var a = Session("a", 0, 1);
        var outside = Commit("2222222", 2, "later", "1111111");

        var model = Build([a], [outside, first], Tip("main", "2222222"));

        Assert.Collection(model.Rows,
            r => { Assert.Equal(TimelineRowKind.Start, r.Kind); Assert.Equal(At(-24), r.Time); Assert.Equal([first], r.Commits); },
            r => { Assert.Equal(TimelineRowKind.Session, r.Kind); Assert.Same(a, r.Session); },
            r => { Assert.Equal(TimelineRowKind.Commits, r.Kind); Assert.Equal([outside], r.Commits); });
        Assert.Equal(1, model.SessionCount);
        Assert.Equal(2, model.CommitCount);
        Assert.Equal(1, model.Columns);
        Assert.Equal("main", model.TrunkName);
    }

    [Fact]
    public void Build_WithoutCommits_StartsAtTheFirstSession()
    {
        var a = Session("a", 0, 1);
        var model = Build([a], []);

        Assert.Equal(TimelineRowKind.Start, model.Rows[0].Kind);
        Assert.Equal(At(0), model.Rows[0].Time);
        Assert.Empty(model.Rows[0].Commits);
    }

    [Fact]
    public void Build_ALoneFirstCommitIsTheStartRow()
    {
        var only = Commit("1111111", 0, "initial");
        var model = Build([], [only], Tip("main", "1111111"));

        var row = Assert.Single(model.Rows);
        Assert.Equal(TimelineRowKind.Start, row.Kind);
        Assert.Equal([only], row.Commits);
    }

    [Fact]
    public void Build_AFirstCommitMadeInASessionStaysWithTheSession()
    {
        var a = Session("a", 0, 2);
        var first = Commit("1111111", 1, "initial");
        var model = Build([a], [first], Tip("main", "1111111"));

        Assert.Empty(model.Rows[0].Commits);
        Assert.Equal(At(0), model.Rows[0].Time);
        Assert.Equal([first], RowOf(model, a).Commits);
    }

    [Fact]
    public void Build_NothingGivesNoRows()
    {
        var model = Build([], []);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public void Build_SessionRowHoldsItsCommitsInTimeOrder()
    {
        var a = Session("a", 0, 2);
        var earlier = Commit("1111111", 0.5);
        var later = Commit("2222222", 1.5, "x", "1111111");

        var model = Build([a], [later, earlier], Tip("main", "2222222"));

        Assert.Equal([earlier, later], RowOf(model, a).Commits);
    }

    [Fact]
    public void Build_OutsideCommitsOnTheSameDayAndBranchShareOneRow()
    {
        var c0 = Commit("0000000", -24, "initial");
        var c1 = Commit("1111111", 0, "a", "0000000");
        var c2 = Commit("2222222", 3, "b", "1111111");
        var nextDay = Commit("3333333", 26, "c", "2222222");
        var feature = Commit("f111111", 4, "on a branch", "0000000");

        var model = Build([], [c1, c2, nextDay, c0, feature], Tip("main", "3333333"), Tip("feat/x", "f111111"));

        Assert.Collection(model.Rows,
            r => Assert.Equal([c0], r.Commits),
            r => Assert.Equal([c1, c2], r.Commits),
            r => { Assert.Equal([feature], r.Commits); Assert.Equal("feat/x", r.Branch!.Name); },
            r => Assert.Equal([nextDay], r.Commits));
        Assert.Equal("commits:" + c1.Hash, model.Rows[1].Key);
    }

    [Fact]
    public void Build_LiveFlagComesFromTheLastEntryTime()
    {
        var old = Session("old", 0, 1);
        var live = Session("live", 2, 3);
        var model = TimelineBuilder.Build([old, live], [], [], At(3).AddMinutes(1));

        Assert.False(model.Rows.Single(r => r.Session == old).IsLive);
        Assert.True(model.Rows.Single(r => r.Session == live).IsLive);
    }

    // ---- lanes ----

    [Fact]
    public void Layout_SessionsOnTheTrunkStayInColumnZero_WithoutCommitsTheyAreHollow()
    {
        var c0 = Commit("0000000", -24, "initial");
        var a = Session("a", 0, 1);
        var b = Session("b", 2, 3);
        var c1 = Commit("1111111", 2.5, "on main", "0000000");
        var model = Build([a, b], [c0, c1], Tip("main", "1111111"));

        Assert.Equal(0, RowOf(model, a).Column);
        Assert.True(RowOf(model, a).IsHollow);
        Assert.Equal(0, RowOf(model, b).Column);
        Assert.False(RowOf(model, b).IsHollow);
        Assert.Equal(1, model.Columns);
        Assert.All(model.Rows, r => Assert.Single(r.Lines));
    }

    [Fact]
    public void Layout_AMergedBranchForksAtItsFirstRowAndCurvesBackBeforeTheMergeRow()
    {
        var c0 = Commit("0000000", -24, "initial");
        var a = Session("a", 0, 1);           // works on feat/x
        var f1 = Commit("f111111", 0.5, "feature", "0000000");
        var b = Session("b", 2, 3);           // merges it (on the trunk)
        var merge = Commit("3333333", 2.5, "Merge pull request #1 from IGOLimited/feat/x", "0000000", "f111111");
        var later = Commit("4444444", 5, "after", "3333333");

        var model = Build([a, b], [c0, f1, merge, later], Tip("main", "4444444"));

        var rowA = RowOf(model, a);
        Assert.Equal("feat/x", rowA.Branch!.Name);
        Assert.Equal(1, rowA.Column);
        var lane = Assert.Single(rowA.Lines, l => l.Column == 1);
        Assert.True(lane.Fork);
        Assert.False(lane.FromTop);
        Assert.True(lane.MergeAtBottom);

        // b made the merge commit, but the feature lane wins for a session only when it has feature commits: b has none.
        var rowB = RowOf(model, b);
        Assert.Equal(0, rowB.Column);
        Assert.Single(rowB.Lines);
        Assert.Equal(2, model.Columns);
    }

    [Fact]
    public void Layout_ASessionThatMergesItsOwnBranchSitsOnTheBranchLaneAndClosesIt()
    {
        var c0 = Commit("0000000", -24, "initial");
        var a = Session("a", 0, 3);
        var f1 = Commit("f111111", 1, "feature", "0000000");
        var merge = Commit("3333333", 2, "Merge pull request #1 from IGOLimited/feat/x", "0000000", "f111111");
        var later = Commit("4444444", 5, "after", "3333333");

        var model = Build([a], [c0, f1, merge, later], Tip("main", "4444444"));

        var rowA = RowOf(model, a);
        Assert.Equal("feat/x", rowA.Branch!.Name);
        var lane = Assert.Single(rowA.Lines, l => l.Column == 1);
        Assert.True(lane.Fork && lane.MergeAtBottom);
        var laterRow = model.Rows.Single(r => r.Commits.Contains(later));
        Assert.Single(laterRow.Lines); // the lane is closed by then
    }

    [Fact]
    public void Layout_AMergeMadeByALongSessionStartedEarlierStillClosesTheLane()
    {
        // The long session's row (at its start) is above the branch's rows, yet it holds the merge commit.
        var c0 = Commit("0000000", -24, "initial");
        var longSession = Session("long", 0, 30, "3333333");
        var worker = Session("worker", 1, 2, "f111111");
        var f1 = Commit("f111111", 1.5, "feature", "0000000");
        var merge = Commit("3333333", 5, "Merge pull request #1 from IGOLimited/feat/x", "0000000", "f111111");

        var model = Build([longSession, worker], [c0, f1, merge], Tip("main", "3333333"));

        Assert.Contains(merge, RowOf(model, longSession).Commits);
        var rowWorker = RowOf(model, worker);
        Assert.Equal("feat/x", rowWorker.Branch!.Name);
        var lane = Assert.Single(rowWorker.Lines, l => l.Column == 1);
        Assert.True(lane.Fork && lane.MergeAtBottom);
        Assert.True(rowWorker.Branch.MergesBack);
    }

    [Fact]
    public void Layout_ABranchRowBelowTheMergeRowStillEndsWithTheMerge()
    {
        // Session a started first and merged at the end; session b started later but committed to the branch before
        // the merge. b's row comes after a's, and the lane must run down to it and curve in there.
        var c0 = Commit("0000000", -24, "initial");
        var a = Session("a", 0, 6, "f111111", "3333333");
        var b = Session("b", 1, 2, "f222222");
        var f1 = Commit("f111111", 0.5, "feature by a", "0000000");
        var f2 = Commit("f222222", 1.5, "feature by b", "f111111");
        var merge = Commit("3333333", 5, "Merge pull request #1 from IGOLimited/feat/x", "0000000", "f222222");
        var later = Commit("4444444", 8, "after", "3333333");

        var model = Build([a, b], [c0, f1, f2, merge, later], Tip("main", "4444444"));

        var rowA = RowOf(model, a);
        var rowB = RowOf(model, b);
        Assert.Equal("feat/x", rowA.Branch!.Name);
        Assert.Same(rowA.Branch, rowB.Branch);
        Assert.True(Assert.Single(rowA.Lines, l => l.Column == 1).Fork);
        var laneB = Assert.Single(rowB.Lines, l => l.Column == 1);
        Assert.True(laneB.FromTop && laneB.MergeAtBottom);
        Assert.Single(model.Rows.Single(r => r.Commits.Contains(later)).Lines);
    }

    [Fact]
    public void Layout_AnOpenBranchRunsThroughTheRowsBetweenItsCommitsAndStopsAtItsLast()
    {
        var c0 = Commit("0000000", -24, "initial");
        var f1 = Commit("f111111", 0, "feature start", "0000000");
        var m1 = Commit("1111111", 1, "on main meanwhile", "0000000");
        var f2 = Commit("f222222", 26, "feature next day", "f111111");
        var m2 = Commit("2222222", 30, "main later", "1111111");

        var model = Build([], [c0, f1, m1, f2, m2], Tip("main", "2222222"), Tip("feat/open", "f222222"));

        Assert.Collection(model.Rows,
            r => Assert.Single(r.Lines),
            r => { Assert.Equal(1, r.Column); var l = Assert.Single(r.Lines, l => l.Column == 1); Assert.True(l.Fork && l.ToBottom && !l.MergeAtBottom); },
            r => { Assert.Equal(0, r.Column); var l = Assert.Single(r.Lines, l => l.Column == 1); Assert.True(l.FromTop && l.ToBottom && !l.Fork); },
            r => { Assert.Equal(1, r.Column); var l = Assert.Single(r.Lines, l => l.Column == 1); Assert.True(l.FromTop && !l.ToBottom && !l.MergeAtBottom); },
            r => Assert.Single(r.Lines));
    }

    [Fact]
    public void Layout_TwoBranchesAliveAtOnceTakeTwoColumns_AFreedColumnIsReused()
    {
        var c0 = Commit("0000000", -24, "initial");
        var a1 = Commit("a111111", 0, "a", "0000000");
        var b1 = Commit("b111111", 1, "b", "0000000");
        var mergeA = Commit("1111111", 2, "Merge pull request #1 from IGOLimited/feat/a", "0000000", "a111111");
        var b2 = Commit("b222222", 26, "b again", "b111111");
        var c1 = Commit("c111111", 27, "c", "1111111");

        var model = Build([], [c0, a1, b1, mergeA, b2, c1], Tip("main", "1111111"), Tip("feat/b", "b222222"), Tip("feat/c", "c111111"));

        var rowA = model.Rows.Single(r => r.Commits.Contains(a1));
        var rowB = model.Rows.Single(r => r.Commits.Contains(b1));
        var rowC = model.Rows.Single(r => r.Commits.Contains(c1));
        Assert.Equal(1, rowA.Column);
        Assert.Equal(2, rowB.Column);
        Assert.Equal(1, rowC.Column); // feat/a's column, free since its merge
        Assert.Equal(3, model.Columns);
    }

    [Fact]
    public void Keys_AreStableAcrossRebuilds()
    {
        var c0 = Commit("0000000", -24, "initial");
        var a = Session("a", 0, 1);
        var c = Commit("1111111", 3, "x", "0000000");
        var first = Build([a], [c0, c], Tip("main", "1111111"));
        var second = Build([a], [c0, c, Commit("2222222", 3.5, "y", "1111111")], Tip("main", "2222222"));

        Assert.Equal(first.Rows.Select(r => r.Key), second.Rows.Select(r => r.Key));
        Assert.Equal(a.Summary.FilePath, first.Rows[1].Key);
        Assert.Equal("start", first.Rows[0].Key);
    }
}

public class TimelineBranchFactsTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double hours) => Day.AddHours(hours);
    private static string Full(string hash) => hash.PadRight(40, '0');
    private static TimelineCommit Commit(string hash, double hours, string subject = "work", params string[] parents) =>
        new(Full(hash), parents.Select(Full).ToList(), At(hours), "Daniel", subject);
    private static TimelineBranchTip Tip(string name, string hash, bool remote = false) => new(Full(hash), name, remote);

    [Fact]
    public void Graph_AnImportWithMoreHistoryThanTheProjectHadBeforeGitHub_IsStillTheImportedOne()
    {
        // The project: two commits, then GitHub's "Initial commit" merged in, then more work. Later a whole other
        // repository (five commits, more than the project had before its GitHub join) is merged in. The project's
        // root must stay the project's, and the other repository must be the imported lane that joins main.
        var p1 = Commit("a100000", -10, "feat: start");
        var p2 = Commit("a200000", -9, "feat: more", "a100000");
        var gh = Commit("9900000", -8, "Initial commit");
        var join1 = Commit("a300000", -7, "Merge branch 'main' of github", "9900000", "a200000");
        var p3 = Commit("a400000", -6, "feat: after the join", "a300000");
        var x1 = Commit("f100000", -50, "other: one");
        var x2 = Commit("f200000", -49, "other: two", "f100000");
        var x3 = Commit("f300000", -48, "other: three", "f200000");
        var x4 = Commit("f400000", -47, "other: four", "f300000");
        var x5 = Commit("f500000", -46, "other: five", "f400000");
        var joinX = Commit("a500000", 0, "chore: bring the other repository in", "a400000", "f500000");
        var later = Commit("a600000", 1, "feat: later", "a500000");

        var (branches, _) = BranchGraph.Assign([later, joinX, p3, join1, gh, p2, p1, x5, x4, x3, x2, x1], [Tip("main", "a600000")]);

        var imported = Assert.Single(branches, b => b.IsImported);
        Assert.Equal(5, imported.Commits.Count);
        Assert.Contains(x1, imported.Commits);
        Assert.Equal(joinX.Hash, imported.MergeHash);
        var trunk = branches.Single(b => b.IsTrunk);
        Assert.Contains(p1, trunk.Commits);
        Assert.Contains(gh, trunk.Commits);
    }

    [Fact]
    public void Graph_ALocalBranchWithoutARemoteCounterpartIsNotPushed()
    {
        var root = Commit("a100000", 0, "init");
        var a1 = Commit("b100000", 1, "a", "a100000");
        var b1 = Commit("c100000", 2, "b", "a100000");
        var tips = new[] { Tip("main", "a100000"), Tip("main", "a100000", remote: true), Tip("feat/a", "b100000"), Tip("feat/b", "c100000"), Tip("feat/b", "c100000", remote: true) };

        var (branches, _) = BranchGraph.Assign([b1, a1, root], tips);

        Assert.True(branches.Single(b => b.Name == "feat/a").IsLocalOnly);
        Assert.False(branches.Single(b => b.Name == "feat/b").IsLocalOnly);

        // With no remote at all nothing is called unpushed.
        var (local, _) = BranchGraph.Assign([Commit("c100000", 2, "b", "a100000"), Commit("b100000", 1, "a", "a100000"), Commit("a100000", 0, "init")],
            [Tip("main", "a100000"), Tip("feat/a", "b100000")]);
        Assert.False(local.Single(b => b.Name == "feat/a").IsLocalOnly);
    }

    [Fact]
    public void Layout_BranchesThatReuseAColumnGetTheirOwnColours()
    {
        // Three branches one after another, each merged before the next starts: all in column 1, three colours.
        var root = Commit("a000000", 0, "init");
        var f1 = Commit("f100000", 1, "one", "a000000");
        var m1 = Commit("a100000", 2, "Merge pull request #1 from me/feat/one", "a000000", "f100000");
        var f2 = Commit("f200000", 3, "two", "a100000");
        var m2 = Commit("a200000", 4, "Merge pull request #2 from me/feat/two", "a100000", "f200000");
        var f3 = Commit("f300000", 5, "three", "a200000");
        var m3 = Commit("a300000", 6, "Merge pull request #3 from me/feat/three", "a200000", "f300000");

        var model = TimelineBuilder.Build([], [m3, f3, m2, f2, m1, f1, root], [Tip("main", "a300000")], Day.AddDays(1));

        var lanes = model.Branches.Where(b => !b.IsTrunk).OrderBy(b => b.StartRow).ToList();
        Assert.Equal(3, lanes.Count);
        Assert.All(lanes, l => Assert.Equal(1, l.Column));
        Assert.Equal([1, 2, 3], lanes.Select(l => l.ColourIndex));
        Assert.Equal(0, model.Branches.Single(b => b.IsTrunk).ColourIndex);
        var line = model.Rows.SelectMany(r => r.Lines).First(l => l.Key == lanes[2].Key);
        Assert.Equal(3, line.Colour);
    }
}

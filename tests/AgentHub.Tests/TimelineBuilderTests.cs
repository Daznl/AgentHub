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

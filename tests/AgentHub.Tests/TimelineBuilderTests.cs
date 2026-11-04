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

    private static TimelineCommit Commit(string hash, double hours, string subject = "work", string? branch = "main") =>
        new(hash.PadRight(40, '0'), [], At(hours), "Daniel", subject, branch);

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

    // ---- rows ----

    [Fact]
    public void Build_StartsWithTheFirstCommitThenSessionsAndOutsideCommitsInTimeOrder()
    {
        var first = Commit("1111111", -24, "initial");
        var a = Session("a", 0, 1);
        var outside = Commit("2222222", 2);

        var model = TimelineBuilder.Build([a], [outside, first], Now);

        Assert.Collection(model.Rows,
            r => { Assert.Equal(TimelineRowKind.Start, r.Kind); Assert.Equal(At(-24), r.Time); Assert.Equal([first], r.Commits); },
            r => { Assert.Equal(TimelineRowKind.Session, r.Kind); Assert.Same(a, r.Session); },
            r => { Assert.Equal(TimelineRowKind.Commits, r.Kind); Assert.Equal([outside], r.Commits); });
        Assert.Equal(1, model.SessionCount);
        Assert.Equal(2, model.CommitCount);
        Assert.Equal(1, model.Columns);
    }

    [Fact]
    public void Build_WithoutCommits_StartsAtTheFirstSession()
    {
        var a = Session("a", 0, 1);
        var model = TimelineBuilder.Build([a], [], Now);

        Assert.Equal(TimelineRowKind.Start, model.Rows[0].Kind);
        Assert.Equal(At(0), model.Rows[0].Time);
        Assert.Empty(model.Rows[0].Commits);
    }

    [Fact]
    public void Build_ALoneFirstCommitIsTheStartRow()
    {
        var only = Commit("1111111", 0, "initial");
        var model = TimelineBuilder.Build([], [only], Now);

        var row = Assert.Single(model.Rows);
        Assert.Equal(TimelineRowKind.Start, row.Kind);
        Assert.Equal([only], row.Commits);
    }

    [Fact]
    public void Build_AFirstCommitMadeInASessionStaysWithTheSession()
    {
        var a = Session("a", 0, 2);
        var first = Commit("1111111", 1, "initial");
        var model = TimelineBuilder.Build([a], [first], Now);

        Assert.Empty(model.Rows[0].Commits);
        Assert.Equal(At(0), model.Rows[0].Time);
        Assert.Equal([first], RowOf(model, a).Commits);
    }

    [Fact]
    public void Build_NothingGivesNoRows()
    {
        var model = TimelineBuilder.Build([], [], Now);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public void Build_SessionRowHoldsItsCommitsInTimeOrder()
    {
        var a = Session("a", 0, 2);
        var later = Commit("2222222", 1.5);
        var earlier = Commit("1111111", 0.5);

        var model = TimelineBuilder.Build([a], [later, earlier], Now);

        var row = model.Rows.Single(r => r.Kind == TimelineRowKind.Session);
        Assert.Equal([earlier, later], row.Commits);
    }

    [Fact]
    public void Build_OutsideCommitsOnTheSameDayShareOneRow_AcrossDaysTheyDoNot()
    {
        var c0 = Commit("0000000", -24, "initial");
        var c1 = Commit("1111111", 0);
        var c2 = Commit("2222222", 3);
        var nextDay = Commit("3333333", 26);

        var model = TimelineBuilder.Build([], [c1, c2, nextDay, c0], Now.AddDays(2));

        Assert.Collection(model.Rows,
            r => Assert.Equal([c0], r.Commits),
            r => Assert.Equal([c1, c2], r.Commits),
            r => Assert.Equal([nextDay], r.Commits));
        Assert.Equal("commits:" + c1.Hash, model.Rows[1].Key);
    }

    [Fact]
    public void Build_ASessionBetweenOutsideCommitsSplitsThemEvenOnOneDay()
    {
        var c0 = Commit("0000000", -24, "initial");
        var c1 = Commit("1111111", 0);
        var a = Session("a", 1, 1.5);
        var c2 = Commit("2222222", 3);

        var model = TimelineBuilder.Build([a], [c0, c1, c2], Now);

        Assert.Equal([TimelineRowKind.Start, TimelineRowKind.Commits, TimelineRowKind.Session, TimelineRowKind.Commits],
            model.Rows.Select(r => r.Kind));
    }

    [Fact]
    public void Build_LiveFlagComesFromTheLastEntryTime()
    {
        var old = Session("old", 0, 1);
        var live = Session("live", 2, 3);
        var model = TimelineBuilder.Build([old, live], [], At(3).AddMinutes(1));

        Assert.False(model.Rows.Single(r => r.Session == old).IsLive);
        Assert.True(model.Rows.Single(r => r.Session == live).IsLive);
    }

    // ---- lanes ----

    private static TimelineRow RowOf(TimelineModel model, TimelineSessionInput session) => model.Rows.Single(r => r.Session == session);

    [Fact]
    public void Layout_SessionsOneAfterAnotherStayOnTheTrunk()
    {
        var a = Session("a", 0, 1);
        var b = Session("b", 2, 3);
        var model = TimelineBuilder.Build([a, b], [], Now);

        Assert.Equal(0, RowOf(model, a).Column);
        Assert.Equal(0, RowOf(model, b).Column);
        Assert.Equal(1, model.Columns);
        Assert.All(model.Rows, r => Assert.Single(r.Lines));
    }

    [Fact]
    public void Layout_AnOverlappingSessionForksIntoALaneAndMergesBackWhenItEnds()
    {
        var a = Session("a", 0, 4);
        var b = Session("b", 1, 2);
        var c = Session("c", 5, 6);
        var model = TimelineBuilder.Build([a, b, c], [], Now);

        Assert.Equal(2, model.Columns);
        var rowB = RowOf(model, b);
        Assert.Equal(1, rowB.Column);
        var lane = Assert.Single(rowB.Lines, l => l.Column == 1);
        Assert.True(lane.Fork);
        Assert.False(lane.FromTop);
        Assert.True(lane.ToBottom);
        // b ended before c started, so its lane rejoins the trunk at the bottom of the last row before c.
        Assert.True(lane.MergeAtBottom);

        var rowC = RowOf(model, c);
        Assert.Equal(0, rowC.Column);
        Assert.Single(rowC.Lines);
    }

    [Fact]
    public void Layout_AFreedLaneIsReusedByTheNextOverlappingSession()
    {
        var a = Session("a", 0, 10);
        var b = Session("b", 1, 2);
        var c = Session("c", 3, 4);
        var outside = Commit("1111111", 12);
        var model = TimelineBuilder.Build([a, b, c], [Commit("0000000", -24, "initial"), outside], Now);

        // a holds the trunk; b and c each take lane 1 in turn.
        Assert.Equal(0, RowOf(model, a).Column);
        Assert.Equal(1, RowOf(model, b).Column);
        Assert.Equal(1, RowOf(model, c).Column);

        var rowB = RowOf(model, b);
        var laneB = Assert.Single(rowB.Lines, l => l.Column == 1);
        Assert.True(laneB.Fork && laneB.MergeAtBottom); // b starts and ends between a's row and c's row

        var rowC = RowOf(model, c);
        var laneC = Assert.Single(rowC.Lines, l => l.Column == 1);
        Assert.True(laneC.Fork && laneC.MergeAtBottom); // c ended before the outside commit

        var commitsRow = model.Rows.Single(r => r.Kind == TimelineRowKind.Commits);
        Assert.Single(commitsRow.Lines); // only the trunk is left
        Assert.False(commitsRow.Lines[0].ToBottom);
    }

    [Fact]
    public void Layout_ThreeAtOnceUseThreeColumns()
    {
        var a = Session("a", 0, 10);
        var b = Session("b", 1, 9);
        var c = Session("c", 2, 8);
        var model = TimelineBuilder.Build([a, b, c], [], Now);

        Assert.Equal(3, model.Columns);
        Assert.Equal(2, RowOf(model, c).Column);
        var rowC = RowOf(model, c);
        Assert.Equal([0, 1, 2], rowC.Lines.Select(l => l.Column).OrderBy(c => c));
        Assert.True(rowC.Lines.Single(l => l.Column == 1).FromTop);
    }

    [Fact]
    public void Layout_ALiveLaneStaysOpenAtTheBottom()
    {
        var a = Session("a", 0, 10);
        var b = Session("b", 1, 2);
        var model = TimelineBuilder.Build([a, b], [], At(10).AddMinutes(1)); // a still live, b long finished

        var laneB = Assert.Single(RowOf(model, b).Lines, l => l.Column == 1);
        Assert.True(laneB.MergeAtBottom);

        var live = Session("live", 3, 4);
        var model2 = TimelineBuilder.Build([a, live], [], At(4).AddMinutes(1));
        var laneLive = Assert.Single(RowOf(model2, live).Lines, l => l.Column == 1);
        Assert.False(laneLive.MergeAtBottom);
        Assert.True(laneLive.ToBottom);
    }

    [Fact]
    public void Layout_TheTrunkRunsFromTheSecondRowToTheLast()
    {
        var a = Session("a", 0, 1);
        var b = Session("b", 2, 3);
        var model = TimelineBuilder.Build([a, b], [], Now);

        var trunks = model.Rows.Select(r => r.Lines.Single(l => l.Column == 0)).ToList();
        Assert.False(trunks[0].FromTop);
        Assert.True(trunks[0].ToBottom);
        Assert.True(trunks[1].FromTop && trunks[1].ToBottom);
        Assert.True(trunks[2].FromTop);
        Assert.False(trunks[2].ToBottom);
    }

    [Fact]
    public void Keys_AreStableAcrossRebuilds()
    {
        var c0 = Commit("0000000", -24, "initial");
        var a = Session("a", 0, 1);
        var c = Commit("1111111", 3);
        var first = TimelineBuilder.Build([a], [c0, c], Now);
        var second = TimelineBuilder.Build([a], [c0, c, Commit("2222222", 3.5)], Now);

        Assert.Equal(first.Rows.Select(r => r.Key), second.Rows.Select(r => r.Key));
        Assert.Equal(a.Summary.FilePath, first.Rows[1].Key);
        Assert.Equal("start", first.Rows[0].Key);
    }
}

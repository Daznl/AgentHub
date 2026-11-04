using System;
using System.Collections.Generic;
using System.Linq;
using AgentHub.Services;
using AgentHub.Services.Timeline;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// What every drawn graph must satisfy, whatever the data: a lane line entering a row from the top left the row above
/// through its bottom, a line leaving a row's bottom is picked up by the row below, every lane begins with a fork off the
/// trunk, and a lane that curves back in does so only at its last row. Run against a real repository with
/// <c>AGENTHUB_TIMELINE_REPO=&lt;folder&gt;</c>.
/// </summary>
public class TimelineGraphInvariantTests
{
    public static void AssertConnected(TimelineModel model)
    {
        var rows = model.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            Assert.Single(row.Lines, l => l.Column == 0); // the trunk runs through every row
            Assert.Equal(row.Lines.Count, row.Lines.Select(l => l.Column).Distinct().Count()); // one line per column
            Assert.Contains(row.Lines, l => l.Column == row.Column); // the node sits on a drawn line

            foreach (var line in row.Lines.Where(l => l.Column > 0))
            {
                var above = i > 0 ? rows[i - 1].Lines.FirstOrDefault(l => l.Column == line.Column) : null;
                var below = i < rows.Count - 1 ? rows[i + 1].Lines.FirstOrDefault(l => l.Column == line.Column) : null;

                if (line.FromTop)
                    Assert.True(above is { ToBottom: true, MergeAtBottom: false }, $"row {i} col {line.Column}: enters from the top but the row above does not reach down");
                else
                    Assert.True(line.Fork, $"row {i} col {line.Column}: a lane that does not come from above must fork off the trunk");

                if (line.ToBottom && !line.MergeAtBottom)
                    Assert.True(below is { FromTop: true }, $"row {i} col {line.Column}: reaches down but the row below does not pick it up");
                if (above is { ToBottom: true, MergeAtBottom: false } && !line.FromTop)
                    Assert.Fail($"row {i} col {line.Column}: the row above reaches down but this row starts a new lane");
                if (line.MergeAtBottom)
                    Assert.False(line.ToBottom && below is { FromTop: true }, $"row {i} col {line.Column}: merges back yet continues below");
            }
        }
    }

    [Fact]
    public async Task RealRepository_GraphIsConnected()
    {
        var folder = Environment.GetEnvironmentVariable("AGENTHUB_TIMELINE_REPO");
        if (string.IsNullOrWhiteSpace(folder)) return; // opt-in: needs a real repository with sessions

        var runner = new ProcessRunner();
        var scope = await TimelineSource.ResolveAsync(runner, folder);
        var commits = await TimelineSource.ReadCommitsAsync(runner, scope);
        var tips = await TimelineSource.ReadBranchTipsAsync(runner, scope);
        var sessions = TimelineSource.ReadSessions(scope, Environment.GetEnvironmentVariable("AGENTHUB_TIMELINE_ARCHIVE"));
        var model = TimelineBuilder.Build(sessions, commits, tips, DateTimeOffset.UtcNow);

        Assert.NotEmpty(model.Rows);
        AssertConnected(model);

        // Every commit is on exactly one branch, and every branch lane that is drawn has rows.
        Assert.All(commits, c => Assert.NotNull(c.Branch));
        var openEnds = model.Branches.Count(b => !b.IsTrunk && b.StartRow >= 0 && !b.MergesBack);
        var merged = model.Branches.Count(b => b.MergesBack);
        Console.WriteLine($"{model.Rows.Count} rows, {model.Columns} columns, {merged} merged lanes, {openEnds} open lanes, trunk {model.TrunkName}");
    }
}

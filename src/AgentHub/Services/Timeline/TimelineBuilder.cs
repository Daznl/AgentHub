using AgentHub.Services.History;

namespace AgentHub.Services.Timeline;

/// <summary>One commit from <c>git log --all</c>. <see cref="Branch"/> is the ref it was reached from, when known.</summary>
public sealed record TimelineCommit(string Hash, IReadOnlyList<string> Parents, DateTimeOffset Time, string Author, string Subject, string? Branch)
{
    /// <summary>The 7-character prefix transcripts quote (from <c>git log --oneline</c>, PR merges and so on).</summary>
    public string Short => Hash.Length > 7 ? Hash[..7] : Hash;
    public bool IsMerge => Parents.Count > 1;
}

/// <summary>
/// A session as the timeline sees it: its summary, when it really ended (last transcript entry, which an archived copy's
/// file time does not show), and the commit hash prefixes its transcript mentions.
/// </summary>
public sealed record TimelineSessionInput(SessionSummary Summary, DateTimeOffset EndedAt, IReadOnlySet<string> MentionedHashes);

public enum TimelineRowKind
{
    /// <summary>The starting point: first commit or first session.</summary>
    Start,
    Session,
    /// <summary>Commits made while no session was running (by hand, on GitHub, from another machine).</summary>
    Commits
}

/// <summary>
/// One lane line through a row of the graph. <see cref="FromTop"/> false: the line starts at this row's node (a session
/// branching off the trunk, drawn with a fork curve when <see cref="Fork"/>). <see cref="MergeAtBottom"/>: the session
/// ended, and its lane curves back into the trunk at the bottom of the row.
/// </summary>
public sealed record TimelineLine(int Column, bool FromTop, bool ToBottom, bool Fork, bool MergeAtBottom);

/// <summary>A row of the timeline: a node in <see cref="Column"/> and the lane lines running through it.</summary>
public sealed class TimelineRow
{
    public required TimelineRowKind Kind { get; init; }
    public required DateTimeOffset Time { get; init; }
    public TimelineSessionInput? Session { get; init; }
    public List<TimelineCommit> Commits { get; } = [];
    public int Column { get; set; }
    public List<TimelineLine> Lines { get; } = [];
    public bool IsLive { get; set; }

    /// <summary>Stable across rebuilds, so a refreshed timeline keeps what the user had expanded.</summary>
    public string Key => Kind switch
    {
        TimelineRowKind.Session => Session!.Summary.FilePath,
        TimelineRowKind.Commits => "commits:" + (Commits.Count > 0 ? Commits[0].Hash : Time.ToString("O")),
        _ => "start"
    };
}

public sealed record TimelineModel(IReadOnlyList<TimelineRow> Rows, int Columns, int SessionCount, int CommitCount);

/// <summary>
/// Turns sessions and commits into a top-to-bottom graph: a trunk from the project's start, every session and every run
/// of commits made outside sessions in time order, and a lane of its own for a session that overlaps another, branching
/// off the trunk when it starts and rejoining it when it ends.
/// </summary>
public static class TimelineBuilder
{
    /// <summary>A commit this long before a session's first entry, or after its last, still counts as the session's.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan After = TimeSpan.FromMinutes(5);
    /// <summary>A session written to this recently is shown as live, its lane left open.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(3);

    public static TimelineModel Build(IReadOnlyList<TimelineSessionInput> sessions, IReadOnlyList<TimelineCommit> commits, DateTimeOffset now)
    {
        var (bySession, outside) = Attribute(sessions, commits);

        // Events in time order: each session at its start, each commit made outside sessions at its time.
        var events = sessions.Select(s => (Time: s.Summary.StartedAt, Session: (TimelineSessionInput?)s, Commit: (TimelineCommit?)null))
            .Concat(outside.Select(c => (Time: c.Time, Session: (TimelineSessionInput?)null, Commit: (TimelineCommit?)c)))
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Session is null ? 1 : 0)
            .ToList();

        var rows = new List<TimelineRow>();
        if (events.Count == 0) return new TimelineModel(rows, 1, 0, 0);

        var start = new TimelineRow { Kind = TimelineRowKind.Start, Time = events[0].Time };
        if (commits.Count > 0) start.Commits.Add(commits.MinBy(c => c.Time)!);
        rows.Add(start);

        foreach (var e in events)
        {
            if (e.Session is { } session)
            {
                var row = new TimelineRow { Kind = TimelineRowKind.Session, Time = e.Time, Session = session, IsLive = now - session.EndedAt < LiveWindow };
                row.Commits.AddRange(bySession[session].OrderBy(c => c.Time));
                rows.Add(row);
            }
            else
            {
                // Consecutive outside commits on the same day share one row.
                var last = rows[^1];
                if (last.Kind == TimelineRowKind.Commits && last.Commits[^1].Time.ToLocalTime().Date == e.Commit!.Time.ToLocalTime().Date)
                    last.Commits.Add(e.Commit);
                else
                {
                    var row = new TimelineRow { Kind = TimelineRowKind.Commits, Time = e.Time };
                    row.Commits.Add(e.Commit!);
                    rows.Add(row);
                }
            }
        }

        var columns = Layout(rows);
        return new TimelineModel(rows, columns, sessions.Count, commits.Count);
    }

    /// <summary>
    /// Gives each commit to the session running when it was made. With several running, the one whose transcript
    /// mentions the hash wins (the latest started, if more than one does); with none mentioning it, it stays outside.
    /// </summary>
    public static (Dictionary<TimelineSessionInput, List<TimelineCommit>> BySession, List<TimelineCommit> Outside) Attribute(
        IReadOnlyList<TimelineSessionInput> sessions, IReadOnlyList<TimelineCommit> commits)
    {
        var bySession = sessions.ToDictionary(s => s, _ => new List<TimelineCommit>());
        var outside = new List<TimelineCommit>();
        foreach (var commit in commits)
        {
            var running = sessions.Where(s => commit.Time >= s.Summary.StartedAt - Before && commit.Time <= s.EndedAt + After).ToList();
            var mentioning = running.Where(s => s.MentionedHashes.Contains(commit.Short)).ToList();
            var owner = mentioning.Count > 0 ? mentioning.MaxBy(s => s.Summary.StartedAt)
                : running.Count == 1 ? running[0]
                : null;
            if (owner is null) outside.Add(commit);
            else bySession[owner].Add(commit);
        }
        return (bySession, outside);
    }

    // Column 0 is the trunk. A session takes the lowest column no running session holds, so one running alone sits on the
    // trunk and an overlapping one gets a lane. Returns the number of columns used.
    private static int Layout(List<TimelineRow> rows)
    {
        var running = new List<(TimelineRow Row, int StartIndex)>();
        var columns = 1;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            foreach (var ended in running.Where(r => r.Row.Session!.EndedAt < row.Time).ToList())
            {
                running.Remove(ended);
                MarkMerge(rows[i - 1], ended.Row.Column);
            }

            if (row.Kind == TimelineRowKind.Session)
            {
                var used = running.Select(r => r.Row.Column).ToHashSet();
                var column = 0;
                while (used.Contains(column)) column++;
                row.Column = column;
                running.Add((row, i));
                columns = Math.Max(columns, column + 1);
            }

            row.Lines.Add(new TimelineLine(0, FromTop: i > 0, ToBottom: i < rows.Count - 1, Fork: false, MergeAtBottom: false));
            foreach (var (session, startIndex) in running.Where(r => r.Row.Column > 0))
                row.Lines.Add(new TimelineLine(session.Column, FromTop: startIndex < i, ToBottom: true, Fork: startIndex == i, MergeAtBottom: false));
        }

        // Sessions still running stay open at the bottom; finished ones rejoin the trunk in the last row.
        foreach (var (row, _) in running.Where(r => !r.Row.IsLive))
            MarkMerge(rows[^1], row.Column);
        return columns;
    }

    private static void MarkMerge(TimelineRow row, int column)
    {
        if (column == 0) return;
        var index = row.Lines.FindIndex(l => l.Column == column);
        if (index >= 0) row.Lines[index] = row.Lines[index] with { MergeAtBottom = true };
    }
}

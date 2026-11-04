using System.Text.RegularExpressions;
using AgentHub.Services.History;

namespace AgentHub.Services.Timeline;

/// <summary>One commit from <c>git log</c>. <see cref="Branch"/> is filled in by <see cref="BranchGraph"/>.</summary>
public sealed record TimelineCommit(string Hash, IReadOnlyList<string> Parents, DateTimeOffset Time, string Author, string Subject)
{
    /// <summary>The 7-character prefix transcripts quote (from <c>git log --oneline</c>, PR merges and so on).</summary>
    public string Short => Hash.Length > 7 ? Hash[..7] : Hash;
    public bool IsMerge => Parents.Count > 1;
    /// <summary>The branch this commit belongs to in the graph; the trunk's name for commits on the main line.</summary>
    public string? Branch { get; set; }
}

/// <summary>A branch head from <c>git for-each-ref</c>: local, or remote with the remote prefix removed.</summary>
public sealed record TimelineBranchTip(string Hash, string Name, bool IsRemote);

/// <summary>
/// A session as the timeline sees it: its summary, when it really ended (last transcript entry, which an archived copy's
/// file time does not show), the commit hash prefixes its transcript mentions, and the folder it ran in.
/// </summary>
public sealed record TimelineSessionInput(SessionSummary Summary, DateTimeOffset EndedAt, IReadOnlySet<string> MentionedHashes, string? Folder = null,
    IReadOnlySet<long>? ActiveMinutes = null, Func<string, DateTimeOffset, DateTimeOffset, bool>? MentionsBetween = null)
{
    /// <summary>True when the transcript wrote an entry within <see cref="TimelineBuilder.ActiveSlack"/> of <paramref name="time"/>; null when activity is unknown.</summary>
    public bool? ActiveAt(DateTimeOffset time)
    {
        if (ActiveMinutes is null) return null;
        var minute = time.ToUnixTimeSeconds() / 60;
        var slack = (long)TimelineBuilder.ActiveSlack.TotalMinutes;
        for (var m = minute - slack; m <= minute + slack; m++)
            if (ActiveMinutes.Contains(m)) return true;
        return false;
    }
}

public enum TimelineRowKind
{
    /// <summary>The starting point: first commit or first session.</summary>
    Start,
    Session,
    /// <summary>Commits made while no session was running (by hand, on GitHub, from another machine).</summary>
    Commits
}

/// <summary>
/// One lane line through a row of the graph. <see cref="FromTop"/> false: the line starts at this row's node (a branch
/// leaving the trunk, drawn with a fork curve when <see cref="Fork"/>). <see cref="MergeAtBottom"/>: the branch was merged,
/// and its lane curves back into the trunk at the bottom of the row.
/// </summary>
public sealed record TimelineLine(int Column, bool FromTop, bool ToBottom, bool Fork, bool MergeAtBottom, string Key = TimelineBranch.TrunkKey);

/// <summary>A branch as a lane of the graph: which rows it spans and whether it rejoined the trunk.</summary>
public sealed class TimelineBranch
{
    public required string Name { get; init; }
    /// <summary>Unique even when a branch name was merged and reused; rows refer to lanes by this.</summary>
    public required string Key { get; init; }
    public const string TrunkKey = "trunk";
    public bool IsTrunk { get; init; }
    /// <summary>The merge commit on the trunk that brought this branch in, when it was merged.</summary>
    public string? MergeHash { get; init; }
    public List<TimelineCommit> Commits { get; } = [];
    public int Column { get; set; }
    public int StartRow { get; set; } = -1;
    public int EndRow { get; set; } = -1;
    /// <summary>True when the lane is drawn curving back into the trunk at its last row.</summary>
    public bool MergesBack { get; set; }
}

/// <summary>A row of the timeline: a node in <see cref="Column"/>, on <see cref="Branch"/>'s lane, and the lane lines running through it.</summary>
public sealed class TimelineRow
{
    public required TimelineRowKind Kind { get; init; }
    public required DateTimeOffset Time { get; init; }
    public TimelineSessionInput? Session { get; init; }
    public List<TimelineCommit> Commits { get; } = [];
    /// <summary>The lane the node sits on; null is the trunk.</summary>
    public TimelineBranch? Branch { get; set; }
    public int Column { get; set; }
    public List<TimelineLine> Lines { get; } = [];
    public bool IsLive { get; set; }
    /// <summary>A session that made no commits: nothing ties it to a branch, so it sits on the trunk with a hollow node.</summary>
    public bool IsHollow => Kind == TimelineRowKind.Session && Commits.Count == 0;
    /// <summary>The lane this row belongs to for highlighting: its branch, the trunk for trunk work, nothing for a hollow session.</summary>
    public string? LaneKey => Branch?.Key ?? (IsHollow ? null : TimelineBranch.TrunkKey);

    /// <summary>Stable across rebuilds, so a refreshed timeline keeps what the user had expanded.</summary>
    public string Key => Kind switch
    {
        TimelineRowKind.Session => Session!.Summary.FilePath,
        TimelineRowKind.Commits => "commits:" + (Commits.Count > 0 ? Commits[0].Hash : Time.ToString("O")),
        _ => "start"
    };
}

public sealed record TimelineModel(IReadOnlyList<TimelineRow> Rows, int Columns, int SessionCount, int CommitCount, IReadOnlyList<TimelineBranch> Branches, string TrunkName);

/// <summary>
/// Turns sessions and commits into a top-to-bottom graph on Git's branches: the main branch is the trunk, every other
/// branch a lane that forks off where its work starts and curves back in at the commit that merged it (or stops open if
/// it never was). Sessions sit on the lane of the branch they committed to; sessions without commits sit on the trunk.
/// </summary>
public static class TimelineBuilder
{
    /// <summary>A commit this long before a session's first entry, or after its last, still counts as the session's.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan After = TimeSpan.FromMinutes(5);
    /// <summary>A session with a transcript entry this close to a commit was doing something when it was made.</summary>
    public static readonly TimeSpan ActiveSlack = TimeSpan.FromMinutes(4);
    /// <summary>How far before and after a commit its subject is looked for in a transcript (the message is written just before).</summary>
    public static readonly TimeSpan SubjectBefore = TimeSpan.FromMinutes(15), SubjectAfter = TimeSpan.FromMinutes(2);
    /// <summary>A session written to this recently is shown as live.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(3);

    public static TimelineModel Build(IReadOnlyList<TimelineSessionInput> sessions, IReadOnlyList<TimelineCommit> commits,
        IReadOnlyList<TimelineBranchTip> tips, DateTimeOffset now)
    {
        var (bySession, outside) = Attribute(sessions, commits);
        var (branches, trunkName) = BranchGraph.Assign(commits, tips);
        var byKey = branches.ToDictionary(b => b.Key);
        TimelineBranch? LaneOf(TimelineCommit c) => c.Branch is { } key && byKey.TryGetValue(key, out var b) && !b.IsTrunk ? b : null;

        // The project starts at its first commit or its first session, whichever came first. A first commit made outside
        // any session is the Start row itself rather than a commits row of its own.
        var firstCommit = commits.Count > 0 ? commits.MinBy(c => c.Time) : null;
        var startOwnsCommit = firstCommit is not null && outside.Remove(firstCommit);

        // Events in time order: each session at its start, each commit made outside sessions at its time.
        var events = sessions.Select(s => (Time: s.Summary.StartedAt, Session: (TimelineSessionInput?)s, Commit: (TimelineCommit?)null))
            .Concat(outside.Select(c => (Time: c.Time, Session: (TimelineSessionInput?)null, Commit: (TimelineCommit?)c)))
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Session is null ? 1 : 0)
            .ToList();

        var rows = new List<TimelineRow>();
        var startTime = events.Count > 0 ? events[0].Time : (DateTimeOffset?)null;
        if (firstCommit is not null && (startTime is null || firstCommit.Time < startTime)) startTime = firstCommit.Time;
        if (startTime is null) return new TimelineModel(rows, 1, 0, 0, branches, trunkName);

        var start = new TimelineRow { Kind = TimelineRowKind.Start, Time = startTime.Value };
        if (startOwnsCommit) start.Commits.Add(firstCommit!);
        rows.Add(start);

        foreach (var e in events)
        {
            if (e.Session is { } session)
            {
                var row = new TimelineRow { Kind = TimelineRowKind.Session, Time = e.Time, Session = session, IsLive = now - session.EndedAt < LiveWindow };
                row.Commits.AddRange(bySession[session].OrderBy(c => c.Time));
                // The branch most of its commits went to; a feature branch wins over the trunk (a session that merged its
                // own branch belongs to that branch's story).
                row.Branch = row.Commits.Select(LaneOf).Where(b => b is not null).GroupBy(b => b!)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.StartRow).Select(g => g.Key).FirstOrDefault();
                rows.Add(row);
            }
            else
            {
                // Consecutive outside commits on the same day and the same branch share one row.
                var commit = e.Commit!;
                var lane = LaneOf(commit);
                var last = rows[^1];
                if (last.Kind == TimelineRowKind.Commits && last.Branch == lane
                    && last.Commits[^1].Time.ToLocalTime().Date == commit.Time.ToLocalTime().Date)
                    last.Commits.Add(commit);
                else
                {
                    var row = new TimelineRow { Kind = TimelineRowKind.Commits, Time = e.Time, Branch = lane };
                    row.Commits.Add(commit);
                    rows.Add(row);
                }
            }
        }

        var columns = Layout(rows, branches);
        return new TimelineModel(rows, columns, sessions.Count, commits.Count, branches, trunkName);
    }

    /// <summary>
    /// Gives each commit to the session that made it. Candidates are the sessions whose window covers the commit time and
    /// that wrote a transcript entry within <see cref="ActiveSlack"/> of it (a session left open for days only counts while
    /// it is doing something); with no activity known, every session whose window covers it. With several candidates the
    /// one whose transcript mentions the hash wins (the latest started, if more than one does); otherwise a single
    /// candidate wins and more than one leaves the commit outside.
    /// </summary>
    public static (Dictionary<TimelineSessionInput, List<TimelineCommit>> BySession, List<TimelineCommit> Outside) Attribute(
        IReadOnlyList<TimelineSessionInput> sessions, IReadOnlyList<TimelineCommit> commits)
    {
        var bySession = sessions.ToDictionary(s => s, _ => new List<TimelineCommit>());
        var outside = new List<TimelineCommit>();
        foreach (var commit in commits)
        {
            var inWindow = sessions.Where(s => commit.Time >= s.Summary.StartedAt - Before && commit.Time <= s.EndedAt + After).ToList();
            var active = inWindow.Where(s => s.ActiveAt(commit.Time) == true).ToList();
            var running = active.Count > 0 ? active : inWindow.Where(s => s.ActiveAt(commit.Time) is null).ToList();
            var mentioning = running.Where(s => s.MentionedHashes.Contains(commit.Short)).ToList();
            // Still tied: the session that wrote the commit message has its subject in the git commit it ran just before.
            if (mentioning.Count == 0 && running.Count > 1 && SubjectFragment(commit.Subject) is { } fragment)
                mentioning = running.Where(s => s.MentionsBetween?.Invoke(fragment, commit.Time - SubjectBefore, commit.Time + SubjectAfter) == true).ToList();
            var owner = mentioning.Count > 0 ? mentioning.MaxBy(s => s.Summary.StartedAt)
                : running.Count == 1 ? running[0]
                : null;
            if (owner is null) outside.Add(commit);
            else bySession[owner].Add(commit);
        }
        return (bySession, outside);
    }

    /// <summary>
    /// The part of a commit subject searched for in transcripts: up to the first quote or backslash (JSON escapes them) and
    /// at most 60 characters; null when too short to be telling.
    /// </summary>
    public static string? SubjectFragment(string subject)
    {
        var cut = subject.IndexOfAny(['"', '\\', '\n']);
        var fragment = (cut < 0 ? subject : subject[..cut]).Trim();
        if (fragment.Length > 60) fragment = fragment[..60];
        return fragment.Length >= 12 ? fragment : null;
    }

    // Column 0 is the trunk. Each branch lane spans from its first row to its last (or to the row before the one holding
    // its merge commit), taking the lowest column free over that span. Returns the number of columns used.
    private static int Layout(List<TimelineRow> rows, List<TimelineBranch> branches)
    {
        var rowOfCommit = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
            foreach (var c in rows[i].Commits) rowOfCommit[c.Hash] = i;

        var lanes = new List<TimelineBranch>();
        foreach (var branch in branches.Where(b => !b.IsTrunk))
        {
            var own = rows.Select((r, i) => (r, i)).Where(x => x.r.Branch == branch).Select(x => x.i).ToList();
            if (own.Count == 0) continue; // every commit of this branch sits in a card on another lane; nothing to draw
            branch.StartRow = own[0];
            branch.EndRow = own[^1];
            if (branch.MergeHash is { } merge)
            {
                // Merged: the lane curves back into the trunk at its last row, which is the row above the merge commit
                // when that is lower. Rows are ordered by session start, not commit time, so the merge commit can sit in a
                // session card above the branch's rows (a long-running session merging later work) or a row of the branch
                // can come below the merge row (a session that started later but committed before the merge); the lane
                // still ends with its merge.
                branch.MergesBack = true;
                if (rowOfCommit.TryGetValue(merge, out var mergeRow))
                {
                    var mergeEnd = rows[mergeRow].Branch == branch ? mergeRow : mergeRow - 1;
                    if (mergeEnd > branch.EndRow) branch.EndRow = mergeEnd;
                }
            }
            lanes.Add(branch);
        }

        var busyUntil = new List<int> { int.MaxValue }; // column 0 is the trunk, never free
        foreach (var lane in lanes.OrderBy(l => l.StartRow).ThenBy(l => l.EndRow))
        {
            var column = 1;
            while (column < busyUntil.Count && busyUntil[column] >= lane.StartRow) column++;
            if (column == busyUntil.Count) busyUntil.Add(0);
            busyUntil[column] = lane.EndRow;
            lane.Column = column;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            row.Column = row.Branch?.Column ?? 0;
            row.Lines.Add(new TimelineLine(0, FromTop: i > 0, ToBottom: i < rows.Count - 1, Fork: false, MergeAtBottom: false));
            foreach (var lane in lanes.Where(l => l.StartRow <= i && i <= l.EndRow))
                row.Lines.Add(new TimelineLine(lane.Column, FromTop: i > lane.StartRow, ToBottom: i < lane.EndRow,
                    Fork: i == lane.StartRow, MergeAtBottom: lane.MergesBack && i == lane.EndRow, Key: lane.Key));
        }
        return busyUntil.Count;
    }
}

/// <summary>
/// Assigns every commit to a branch of the graph. The trunk is the main branch's first-parent chain (local and remote
/// tips both, whichever is ahead). Each merge into the trunk defines a branch: the commits reachable from the merge's
/// other parents that nothing claimed yet, named after a branch tip among them or from the merge subject. Unmerged tips
/// claim what is left reachable from them.
/// </summary>
public static partial class BranchGraph
{
    private static readonly string[] TrunkNames = ["main", "master", "trunk", "develop"];

    public static (List<TimelineBranch> Branches, string TrunkName) Assign(IReadOnlyList<TimelineCommit> commits, IReadOnlyList<TimelineBranchTip> tips)
    {
        var byHash = new Dictionary<string, TimelineCommit>(StringComparer.Ordinal);
        foreach (var c in commits) byHash[c.Hash] = c;
        var branches = new List<TimelineBranch>();
        var known = tips.Where(t => byHash.ContainsKey(t.Hash)).ToList();

        var trunkName = TrunkNames.FirstOrDefault(n => known.Any(t => t.Name == n))
            ?? known.OrderByDescending(t => byHash[t.Hash].Time).Select(t => t.Name).FirstOrDefault()
            ?? "main";
        var trunk = new TimelineBranch { Name = trunkName, Key = TimelineBranch.TrunkKey, IsTrunk = true };
        branches.Add(trunk);
        var trunkTips = known.Where(t => t.Name == trunkName).Select(t => t.Hash).ToList();
        if (trunkTips.Count == 0 && commits.Count > 0) trunkTips.Add(commits.MaxBy(c => c.Time)!.Hash);
        foreach (var tip in trunkTips)
            for (var c = byHash.GetValueOrDefault(tip); c is not null && c.Branch is null; c = c.Parents.Count > 0 ? byHash.GetValueOrDefault(c.Parents[0]) : null)
                Claim(trunk, c);

        // Branches brought in by merge commits on the trunk, oldest merge first so the earliest branch claims shared history.
        foreach (var merge in trunk.Commits.Where(c => c.IsMerge).OrderBy(c => c.Time).ToList())
        {
            var set = Reach(merge.Parents.Skip(1), byHash);
            if (set.Count == 0) continue;
            var name = known.Where(t => set.Any(c => c.Hash == t.Hash)).Select(t => t.Name).FirstOrDefault()
                ?? MergedBranchName(merge.Subject)
                ?? "merged " + merge.Short;
            AddBranch(branches, name, merge.Hash, set);
        }

        // What the remaining tips reach that nothing claimed is still open on that branch.
        foreach (var tip in known.Where(t => t.Name != trunkName).OrderBy(t => byHash[t.Hash].Time))
        {
            var set = Reach([tip.Hash], byHash);
            if (set.Count > 0) AddBranch(branches, tip.Name, null, set);
        }

        // Anything left (dangling history no ref reaches) goes with the trunk rather than vanishing.
        foreach (var c in commits.Where(c => c.Branch is null)) Claim(trunk, c);
        return (branches, trunkName);
    }

    private static void AddBranch(List<TimelineBranch> branches, string name, string? mergeHash, List<TimelineCommit> set)
    {
        var branch = new TimelineBranch { Name = name, Key = name + "@" + set.MinBy(c => c.Time)!.Hash, MergeHash = mergeHash };
        foreach (var c in set) Claim(branch, c);
        branches.Add(branch);
    }

    private static void Claim(TimelineBranch branch, TimelineCommit commit)
    {
        commit.Branch = branch.Key;
        branch.Commits.Add(commit);
    }

    // Everything reachable from the given commits through any parent, stopping at commits already on a branch.
    private static List<TimelineCommit> Reach(IEnumerable<string> from, Dictionary<string, TimelineCommit> byHash)
    {
        var found = new List<TimelineCommit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(from);
        while (stack.Count > 0)
        {
            var hash = stack.Pop();
            if (!seen.Add(hash) || !byHash.TryGetValue(hash, out var c) || c.Branch is not null) continue;
            found.Add(c);
            foreach (var p in c.Parents) stack.Push(p);
        }
        return found;
    }

    [GeneratedRegex(@"^Merge pull request #\d+ from [^/\s]+/(\S+)")]
    private static partial Regex PullRequestMerge();
    [GeneratedRegex(@"^Merge (?:remote-tracking )?branch '(?:origin/)?([^']+)'")]
    private static partial Regex BranchMerge();

    /// <summary>The branch a merge commit's subject names, for branches deleted after merging.</summary>
    public static string? MergedBranchName(string subject)
    {
        var m = PullRequestMerge().Match(subject);
        if (m.Success) return m.Groups[1].Value;
        m = BranchMerge().Match(subject);
        return m.Success ? m.Groups[1].Value : null;
    }
}

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
/// leaving another lane, drawn with a fork curve from column <see cref="ForkFrom"/> when <see cref="Fork"/>; 0 is the
/// trunk). <see cref="MergeAtBottom"/>: the branch was merged, and its lane curves back into the trunk at the bottom of the row.
/// </summary>
public sealed record TimelineLine(int Column, bool FromTop, bool ToBottom, bool Fork, bool MergeAtBottom, string Key = TimelineBranch.TrunkKey, int Colour = 0, int ForkFrom = 0);

/// <summary>A branch as a lane of the graph: which rows it spans and whether it rejoined the trunk.</summary>
public sealed class TimelineBranch
{
    public required string Name { get; init; }
    /// <summary>Unique even when a branch name was merged and reused; rows refer to lanes by this.</summary>
    public required string Key { get; init; }
    public const string TrunkKey = "trunk";
    public bool IsTrunk { get; init; }
    /// <summary>
    /// History that shares no ancestor with the trunk: another repository's commits brought in whole (a subtree import,
    /// a merge with unrelated histories). Its lane starts on its own rather than forking off the trunk.
    /// </summary>
    public bool IsImported { get; init; }
    /// <summary>
    /// The trunk commit that brought this branch in, when it was merged: a merge commit, or the squash commit when
    /// <see cref="IsSquash"/> (then the branch's own commits are not in the trunk's history).
    /// </summary>
    public string? MergeHash { get; set; }
    /// <summary>Merged by a squash (GitHub's "Squash and merge"), found from the pull request or by matching the change.</summary>
    public bool IsSquash { get; set; }
    /// <summary>A local branch with no remote counterpart: its commits have not been pushed.</summary>
    public bool IsLocalOnly { get; set; }
    public List<TimelineCommit> Commits { get; } = [];
    public int Column { get; set; }
    /// <summary>
    /// Which colour the lane is drawn in: the Nth lane to appear gets the Nth colour, so branches that reuse a freed
    /// column still look different from each other. 0 is the trunk.
    /// </summary>
    public int ColourIndex { get; set; }
    public int StartRow { get; set; } = -1;
    public int EndRow { get; set; } = -1;
    /// <summary>True when the lane is drawn curving back into the trunk at its last row.</summary>
    public bool MergesBack { get; set; }
    /// <summary>
    /// The branch this one was started from: the one holding the parent of its first commit. The trunk key for a branch
    /// off the main line; another lane's key for a branch stacked on a branch. Null for imported history.
    /// </summary>
    public string? ParentKey { get; set; }
    /// <summary>The lane the fork curve leaves from: the parent lane when it is drawn through the start row, else the trunk.</summary>
    public TimelineBranch? ForkLane { get; set; }
    /// <summary>
    /// The lane that carries this one on: a branch started at this one's tip while this one was never merged. This lane
    /// is drawn down to that lane's first row and hands over there, so stacked work reads as one stream.
    /// </summary>
    public TimelineBranch? ContinuesInto { get; set; }
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

    /// <param name="squashes">Branch name → the trunk commit that squash-merged it (see <see cref="SquashMatcher"/>); null for none.</param>
    public static TimelineModel Build(IReadOnlyList<TimelineSessionInput> sessions, IReadOnlyList<TimelineCommit> commits,
        IReadOnlyList<TimelineBranchTip> tips, DateTimeOffset now, IReadOnlyDictionary<string, string>? squashes = null)
    {
        var (bySession, outside) = Attribute(sessions, commits);
        var (branches, trunkName) = BranchGraph.Assign(commits, tips, squashes);
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

        // The Start row sits on its commit's lane: imported history that predates the project starts on its own lane.
        var start = new TimelineRow { Kind = TimelineRowKind.Start, Time = startTime.Value, Branch = startOwnsCommit ? LaneOf(firstCommit!) : null };
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

        // Where each lane forks from. A branch stacked on another forks from the lane drawn through its start row: the
        // parent, or whichever lane carries the parent on by then. A branch started at an unmerged branch's tip carries
        // it on: the parent lane is drawn down to the child's first row and hands over there. A parent merged before the
        // child's first row is in the trunk by then, so the child forks from the trunk.
        var byKey = lanes.ToDictionary(l => l.Key, StringComparer.Ordinal);
        foreach (var lane in lanes.OrderBy(l => l.StartRow).ThenBy(l => l.EndRow))
        {
            if (lane.IsImported || lane.ParentKey is null || !byKey.TryGetValue(lane.ParentKey, out var parent) || parent == lane) continue;
            var carrier = parent;
            while (carrier.ContinuesInto is { } next && lane.StartRow > carrier.EndRow) carrier = next;
            if (carrier.StartRow <= lane.StartRow && lane.StartRow <= carrier.EndRow)
                lane.ForkLane = carrier;
            else if (carrier == parent && lane.StartRow > parent.EndRow && !parent.MergesBack && parent.ContinuesInto is null)
            {
                parent.EndRow = lane.StartRow;
                parent.ContinuesInto = lane;
                lane.ForkLane = parent;
            }
        }

        var busyUntil = new List<int> { int.MaxValue }; // column 0 is the trunk, never free
        var colour = 0;
        foreach (var lane in lanes.OrderBy(l => l.StartRow).ThenBy(l => l.EndRow))
        {
            if (lane.ForkLane is { } carried && carried.ContinuesInto == lane)
            {
                // Carrying a lane on: the same column and the same colour, so the stream runs straight through the
                // hand-over as one line.
                lane.Column = carried.Column;
                lane.ColourIndex = carried.ColourIndex;
            }
            else
            {
                var column = 1;
                while (column < busyUntil.Count && busyUntil[column] >= lane.StartRow) column++;
                if (column == busyUntil.Count) busyUntil.Add(0);
                lane.Column = column;
                lane.ColourIndex = ++colour; // by order of appearance, not by column: a reused column gets a new colour
            }
            busyUntil[lane.Column] = lane.EndRow;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            row.Column = row.Branch?.Column ?? 0;
            row.Lines.Add(new TimelineLine(0, FromTop: i > 0, ToBottom: i < rows.Count - 1, Fork: false, MergeAtBottom: false));
            foreach (var lane in lanes.Where(l => l.StartRow <= i && i <= l.EndRow))
            {
                // The row a lane hands over in is drawn by the lane carrying it on, which comes in from the top like any
                // continuing line; no fork curve, the colour changes at the node.
                if (lane.ContinuesInto is not null && i == lane.EndRow) continue;
                var carriesOn = i == lane.StartRow && lane.ForkLane is { } from && from.ContinuesInto == lane;
                row.Lines.Add(new TimelineLine(lane.Column, FromTop: i > lane.StartRow || carriesOn, ToBottom: i < lane.EndRow,
                    Fork: i == lane.StartRow && !lane.IsImported && !carriesOn, MergeAtBottom: lane.MergesBack && i == lane.EndRow,
                    Key: lane.Key, Colour: lane.ColourIndex, ForkFrom: lane.ForkLane?.Column ?? 0));
            }
        }
        return busyUntil.Count;
    }
}

/// <summary>
/// Assigns every commit to a branch of the graph. The trunk is the main branch's first-parent chain (local and remote
/// tips both, whichever is ahead). Each merge into the trunk defines a branch: the commits reachable from the merge's
/// other parents that nothing claimed yet, named after a branch tip among them or from the merge subject. Unmerged tips
/// claim what is left reachable from them (a branch's local and remote tips together); a branch among them that a
/// squash merge brought in (<paramref name="squashes"/>) is marked merged at that trunk commit.
/// </summary>
public static partial class BranchGraph
{
    private static readonly string[] TrunkNames = ["main", "master", "trunk", "develop"];

    /// <summary>The trunk's name among the tips: the first of main, master, trunk, develop that exists, else the newest tip's.</summary>
    public static string TrunkNameOf(IReadOnlyList<TimelineCommit> commits, IReadOnlyList<TimelineBranchTip> tips)
    {
        var byHash = commits.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);
        var times = commits.ToDictionary(c => c.Hash, c => c.Time, StringComparer.Ordinal);
        var known = tips.Where(t => byHash.Contains(t.Hash)).ToList();
        return TrunkNames.FirstOrDefault(n => known.Any(t => t.Name == n))
            ?? known.OrderByDescending(t => times[t.Hash]).Select(t => t.Name).FirstOrDefault()
            ?? "main";
    }

    /// <summary>
    /// Tips of branches other than the trunk whose commit the trunk does not reach: the branches git alone calls unmerged.
    /// Those are the ones a squash merge may have brought in without a trace in the graph. One tip per name (the newest).
    /// </summary>
    public static List<TimelineBranchTip> OpenTips(IReadOnlyList<TimelineCommit> commits, IReadOnlyList<TimelineBranchTip> tips)
    {
        var byHash = commits.ToDictionary(c => c.Hash, StringComparer.Ordinal);
        var trunkName = TrunkNameOf(commits, tips);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(tips.Where(t => t.Name == trunkName).Select(t => t.Hash));
        while (stack.Count > 0)
        {
            var hash = stack.Pop();
            if (!reached.Add(hash) || !byHash.TryGetValue(hash, out var c)) continue;
            foreach (var p in c.Parents) stack.Push(p);
        }
        return tips.Where(t => t.Name != trunkName && byHash.ContainsKey(t.Hash) && !reached.Contains(t.Hash))
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(t => byHash[t.Hash].Time).First())
            .ToList();
    }

    public static (List<TimelineBranch> Branches, string TrunkName) Assign(IReadOnlyList<TimelineCommit> commits, IReadOnlyList<TimelineBranchTip> tips,
        IReadOnlyDictionary<string, string>? squashes = null)
    {
        var byHash = new Dictionary<string, TimelineCommit>(StringComparer.Ordinal);
        foreach (var c in commits)
        {
            c.Branch = null; // a rebuild starts clean
            byHash[c.Hash] = c;
        }
        var branches = new List<TimelineBranch>();
        var known = tips.Where(t => byHash.ContainsKey(t.Hash)).ToList();

        var trunkName = TrunkNameOf(commits, tips);
        var trunk = new TimelineBranch { Name = trunkName, Key = TimelineBranch.TrunkKey, IsTrunk = true };
        branches.Add(trunk);
        var trunkTips = known.Where(t => t.Name == trunkName).Select(t => t.Hash).ToList();
        if (trunkTips.Count == 0 && commits.Count > 0) trunkTips.Add(commits.MaxBy(c => c.Time)!.Hash);
        foreach (var tip in trunkTips)
            for (var c = byHash.GetValueOrDefault(tip); c is not null && c.Branch is null; c = c.Parents.Count > 0 ? byHash.GetValueOrDefault(c.Parents[0]) : null)
                Claim(trunk, c);

        // History with no ancestor in common with the trunk's root was brought in from elsewhere. Each such root's
        // descendants that never touch the trunk's root form an imported lineage, joined at the first commit descending
        // from both. Claimed before the merges, or the merge that brought it in would file it under that branch.
        // The project's own root is the one with the most descendants, not the end of main's first-parent chain, which
        // can be GitHub's one-commit "Initial commit" that the real work was merged into.
        var roots = RootsOf(commits, byHash);
        // The end of the first-parent chain: the trunk commit with no parent in the log. Not Commits[^1]: when a second
        // tip (a remote main ahead of the local one) was walked, its newer commits were claimed after the root.
        var trunkRoot = trunk.Commits.FirstOrDefault(c => c.Parents.Count == 0 || !byHash.ContainsKey(c.Parents[0]));
        // The project's root is where the trunk's own first-parent line begins. One exception: a placeholder root with
        // nothing of its own but itself (GitHub's one-commit "Initial commit" that the real work was then merged into)
        // is not the project; the project is what the first merge on the trunk brought into it. Counting descendants
        // does not work here: a repository brought in whole can have more history of its own than the project had.
        int Exclusive(TimelineCommit r) => commits.Count(c => roots[c.Hash].Count == 1 && roots[c.Hash].Contains(r.Hash));
        var primaryRoot = trunkRoot;
        if (primaryRoot is not null && Exclusive(primaryRoot) <= 1)
        {
            var placeholder = primaryRoot;
            var firstMerge = trunk.Commits.Where(c => c.IsMerge && roots[c.Hash].Contains(placeholder.Hash)).MinBy(c => c.Time);
            var broughtIn = firstMerge?.Parents.Skip(1)
                .Select(p => byHash.GetValueOrDefault(p)).Where(p => p is not null)
                .SelectMany(p => roots[p!.Hash]).Distinct()
                .Select(h => byHash[h]).Where(r => r != placeholder)
                .MaxBy(Exclusive);
            if (broughtIn is not null) primaryRoot = broughtIn;
        }
        primaryRoot ??= commits.Where(c => c.Parents.Count == 0).OrderByDescending(Exclusive).ThenBy(r => r.Time).FirstOrDefault();
        if (primaryRoot is not null)
        {
            var foreignRoots = commits.Where(c => c.Parents.Count == 0 && c != primaryRoot).OrderBy(c => c.Time).ToList();
            foreach (var root in foreignRoots)
            {
                var lineage = commits.Where(c => c.Branch is null && roots[c.Hash].Contains(root.Hash) && !roots[c.Hash].Contains(primaryRoot.Hash)).OrderByDescending(c => c.Time).ToList();
                if (lineage.Count == 0) continue;
                var join = commits.Where(c => roots[c.Hash].Contains(root.Hash) && roots[c.Hash].Contains(primaryRoot.Hash)).MinBy(c => c.Time);
                var name = foreignRoots.Count == 1 ? "imported history" : $"imported history · {root.Short}";
                var branch = new TimelineBranch { Name = name, Key = "imported@" + root.Hash, IsImported = true, MergeHash = join?.Hash };
                foreach (var c in lineage) Claim(branch, c);
                branches.Add(branch);
            }
        }

        // Branches brought in by merge commits on the trunk, oldest merge first so the earliest branch claims shared history.
        foreach (var merge in trunk.Commits.Where(c => c.IsMerge).OrderBy(c => c.Time).ToList())
        {
            var set = Reach(merge.Parents.Skip(1), byHash);
            if (set.Count == 0) continue;
            var name = known.Where(t => set.Any(c => c.Hash == t.Hash)).Select(t => t.Name).FirstOrDefault()
                ?? MergedBranchName(merge.Subject)
                ?? "merged " + merge.Short;
            // "Merge branch 'main' of github": the other line of main itself (the work GitHub's initial commit was joined
            // with, or a pull from the remote); that is trunk, not a branch beside it.
            if (name == trunkName) { foreach (var c in set) Claim(trunk, c); continue; }
            AddBranch(branches, name, merge.Hash, set, byHash);
        }

        // What the remaining tips reach that nothing claimed is still open on that branch. A branch's local and remote
        // tips are one branch (one ahead of the other), not two lanes of the same name.
        foreach (var group in known.Where(t => t.Name != trunkName).GroupBy(t => t.Name, StringComparer.Ordinal).OrderBy(g => g.Min(t => byHash[t.Hash].Time)))
        {
            var set = Reach(group.Select(t => t.Hash), byHash);
            if (set.Count > 0) AddBranch(branches, group.Key, null, set, byHash);
        }

        // A squash merge leaves no merge commit: git sees the branch as open. The pull request (or a matching change on
        // the trunk) says which trunk commit brought it in; the lane curves back there like any merged branch.
        if (squashes is not null)
            foreach (var b in branches.Where(b => !b.IsTrunk && !b.IsImported && b.MergeHash is null))
                if (squashes.TryGetValue(b.Name, out var squash) && byHash.TryGetValue(squash, out var at) && at.Branch == trunk.Key)
                {
                    b.MergeHash = squash;
                    b.IsSquash = true;
                }

        // Anything left (dangling history no ref reaches) goes with the trunk rather than vanishing.
        foreach (var c in commits.Where(c => c.Branch is null)) Claim(trunk, c);

        // A branch with a local tip and no remote one has not been pushed (only meaningful when there is a remote at all).
        var remoteNames = tips.Where(t => t.IsRemote).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var localNames = tips.Where(t => !t.IsRemote).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (remoteNames.Count > 0)
            foreach (var b in branches.Where(b => !b.IsTrunk && !b.IsImported))
                b.IsLocalOnly = localNames.Contains(b.Name) && !remoteNames.Contains(b.Name);
        return (branches, trunkName);
    }

    // The root commits (by hash) each commit descends from, found parents-first so every commit's parents are done before
    // it. Keyed by hash: TimelineCommit is a record whose Branch changes as branches claim it, so it is no dictionary key.
    private static Dictionary<string, HashSet<string>> RootsOf(IReadOnlyList<TimelineCommit> commits, Dictionary<string, TimelineCommit> byHash)
    {
        var roots = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var stack = new Stack<TimelineCommit>();
        foreach (var start in commits)
        {
            if (roots.ContainsKey(start.Hash)) continue;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Peek();
                if (roots.ContainsKey(c.Hash)) { stack.Pop(); continue; }
                var parents = c.Parents.Select(p => byHash.GetValueOrDefault(p)).Where(p => p is not null).Select(p => p!).ToList();
                var pending = parents.Where(p => !roots.ContainsKey(p.Hash)).ToList();
                if (pending.Count > 0)
                {
                    foreach (var p in pending) stack.Push(p);
                    continue;
                }
                stack.Pop();
                var set = new HashSet<string>(StringComparer.Ordinal);
                if (parents.Count == 0) set.Add(c.Hash);
                foreach (var p in parents) set.UnionWith(roots[p.Hash]);
                roots[c.Hash] = set;
            }
        }
        return roots;
    }

    private static void AddBranch(List<TimelineBranch> branches, string name, string? mergeHash, List<TimelineCommit> set, Dictionary<string, TimelineCommit> byHash)
    {
        var branch = new TimelineBranch { Name = name, Key = name + "@" + set.MinBy(c => c.Time)!.Hash, MergeHash = mergeHash };
        foreach (var c in set) Claim(branch, c);
        // Where the branch started: the branch holding the first parent of its oldest commit that has a parent outside
        // the set. Whatever that parent belongs to was claimed before this branch reached it (or it is off the log: trunk).
        var inSet = set.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);
        var basis = set.Where(c => c.Parents.Count > 0 && !inSet.Contains(c.Parents[0])).MinBy(c => c.Time)
                    ?? set.Where(c => c.Parents.Count == 0).MinBy(c => c.Time);
        branch.ParentKey = basis is { Parents.Count: > 0 } && byHash.TryGetValue(basis.Parents[0], out var parentCommit)
            ? parentCommit.Branch ?? TimelineBranch.TrunkKey
            : basis is null ? null : TimelineBranch.TrunkKey;
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

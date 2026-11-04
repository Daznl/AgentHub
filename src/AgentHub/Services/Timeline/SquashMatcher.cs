using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AgentHub.Services.Timeline;

/// <summary>
/// Finds the branches a squash merge brought into the trunk. A squash leaves no merge commit, so to git the branch is
/// still open and its lane would never curve back. Two sources, the sure one first: the pull requests (GitHub names the
/// head branch and the commit it made), and, for what they do not cover, the change itself: a trunk commit whose subject
/// ends in "(#N)" and whose files and line counts (<c>git diff --numstat</c> against its parent) equal the branch's
/// (against where it left the trunk). Results are remembered per branch tip and candidate, so the 20-second refresh
/// repeats no git calls for an unchanged repository.
/// </summary>
public sealed partial class SquashMatcher
{
    private readonly ProcessRunner _runner;
    private readonly TimelineScope _scope;
    private readonly ConcurrentDictionary<string, string?> _fingerprints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _matches = new(StringComparer.Ordinal);

    public SquashMatcher(ProcessRunner runner, TimelineScope scope)
    {
        _runner = runner;
        _scope = scope;
    }

    [GeneratedRegex(@"\(#\d+\)\s*$")]
    private static partial Regex SquashSubject();

    /// <summary>A trunk commit subject GitHub gives a squash merge: the pull request title followed by its number.</summary>
    public static bool LooksSquashed(string subject) => SquashSubject().IsMatch(subject);

    /// <summary>
    /// Branch name → the commit that squash-merged it, from merged pull requests whose merge commit is in the log and is
    /// not a merge commit (those the graph already follows). Null pull requests (no gh) give an empty map.
    /// </summary>
    public static Dictionary<string, string> FromPullRequests(IReadOnlyList<PullRequestFact>? pullRequests, IReadOnlyList<TimelineCommit> commits)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pullRequests is null) return result;
        var byHash = commits.ToDictionary(c => c.Hash, StringComparer.Ordinal);
        foreach (var pr in pullRequests.Where(p => p.IsMerged && p.MergeCommit is not null && p.Head.Length > 0).OrderBy(p => p.MergedAt))
            if (byHash.TryGetValue(pr.MergeCommit!, out var at) && !at.IsMerge && !result.ContainsKey(pr.Head))
                result[pr.Head] = at.Hash;
        return result;
    }

    /// <summary>
    /// For each open tip not already in <paramref name="known"/>, the trunk commit whose change equals the branch's, if
    /// one of the squash-looking commits made after the branch's last commit does. Returns the merged map.
    /// </summary>
    public async Task<Dictionary<string, string>> MatchAsync(IReadOnlyList<TimelineCommit> commits, IReadOnlyList<TimelineBranchTip> openTips,
        IReadOnlyDictionary<string, string> known, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(known, StringComparer.Ordinal);
        if (!_scope.IsGit) return result;
        var byHash = commits.ToDictionary(c => c.Hash, StringComparer.Ordinal);
        var candidates = commits.Where(c => !c.IsMerge && c.Parents.Count == 1 && LooksSquashed(c.Subject)).OrderBy(c => c.Time).ToList();
        if (candidates.Count == 0) return result;

        foreach (var tip in openTips.Where(t => !result.ContainsKey(t.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byHash.TryGetValue(tip.Hash, out var tipCommit)) continue;
            // A branch is merged after its last commit; a little slack for clocks that disagree.
            var after = tipCommit.Time - TimeSpan.FromHours(1);
            string? match = null;
            foreach (var candidate in candidates.Where(c => c.Time >= after))
            {
                var key = tip.Hash + "|" + candidate.Hash;
                if (!_matches.TryGetValue(key, out var cached))
                {
                    cached = await SameChangeAsync(tipCommit, candidate, cancellationToken) ? candidate.Hash : null;
                    _matches[key] = cached;
                }
                if (cached is not null) { match = cached; break; }
            }
            if (match is not null) result[tip.Name] = match;
        }
        return result;
    }

    // The branch's change is what GitHub squashed: the diff from where it left the trunk (the merge base with the squash
    // commit's parent) to its tip. Equal files and line counts on both sides, and at least one file, is a match.
    private async Task<bool> SameChangeAsync(TimelineCommit tip, TimelineCommit candidate, CancellationToken cancellationToken)
    {
        var parent = candidate.Parents[0];
        var theirs = await FingerprintAsync(parent, candidate.Hash, cancellationToken);
        if (string.IsNullOrEmpty(theirs)) return false;
        var mergeBase = (await GitAsync(["merge-base", parent, tip.Hash], cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(mergeBase)) return false;
        var ours = await FingerprintAsync(mergeBase, tip.Hash, cancellationToken);
        return ours == theirs;
    }

    private async Task<string?> FingerprintAsync(string from, string to, CancellationToken cancellationToken)
    {
        var key = from + ".." + to;
        if (_fingerprints.TryGetValue(key, out var cached)) return cached;
        var output = await GitAsync(["diff", "--numstat", "--no-renames", from, to], cancellationToken);
        var fingerprint = output is null ? null : Fingerprint(output);
        _fingerprints[key] = fingerprint;
        return fingerprint;
    }

    /// <summary>The numstat lines sorted and joined, so two diffs compare as strings; empty for an empty diff.</summary>
    public static string Fingerprint(string numstat) =>
        string.Join("\n", numstat.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).OrderBy(l => l, StringComparer.Ordinal));

    private async Task<string?> GitAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var (exit, stdout, _) = await _runner.RunAsync("git", ["-C", _scope.Root, .. args], _scope.Root, cancellationToken, TimeSpan.FromSeconds(30));
            return exit == 0 ? stdout : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Services.History;
using AgentHub.Services.Instructions;

namespace AgentHub.Services.Timeline;

/// <summary>
/// One mechanical check over the project's history: how many of <see cref="Total"/> items fail it, and which. A check
/// with no items does not apply (a repository without pull requests has nothing to say about reviewers).
/// </summary>
public sealed record TimelineCheck(string Id, string Name, int Failing, int Total, IReadOnlyList<string> Offenders)
{
    /// <summary>A checks (Id "A…") describe the sessions, B checks the GitHub cycle.</summary>
    public ReviewKind Kind => Id.StartsWith('B') ? ReviewKind.GitHub : ReviewKind.Interaction;
    public bool Applies => Total > 0;
    public double Compliance => Total == 0 ? 1 : 1 - (double)Failing / Total;
}

/// <summary>
/// The two reviews, each with its own button, digest, prompt and panel: how the user and Claude worked across the
/// sessions (scored by the model), and the GitHub cycle of branches, pull requests and merges (scored by AgentHub's checks).
/// </summary>
public enum ReviewKind { Interaction, GitHub }

/// <summary>Everything AgentHub worked out itself before asking the model; <see cref="GitHubScore"/> is null when no GitHub check applies.</summary>
public sealed record TimelineFacts(IReadOnlyList<TimelineCheck> Checks, bool HasPipeline, bool HasPullRequests, int? GitHubScore, int GitHubChecks);

public sealed record ReviewFinding(string What, string Evidence, string Instead, string Who);

/// <summary><see cref="ScoreSource"/> says where the score came from: the model's judgement, or AgentHub's checks.</summary>
public sealed record ReviewPart(int? Score, string ScoreSource, string Verdict, IReadOnlyList<ReviewFinding> Findings);

public sealed record ReviewAddition(string Title, string Markdown, string Why);

public sealed record TimelineReport(ReviewKind Kind, string Summary, ReviewPart Part, IReadOnlyList<ReviewAddition> Additions, TimelineFacts Facts);

/// <summary>The text sent to the model, and which timeline row each session id (S1, S2, …) stands for.</summary>
public sealed record TimelineDigest(string Text, IReadOnlyDictionary<string, string> SessionRowKeys);

/// <summary>A repository instruction file as the review describes it: present or not, its size and headings.</summary>
public sealed record RepoFileFact(string Name, bool Exists, long Bytes, IReadOnlyList<string> Headings);

/// <summary>What the review is built from: the timeline model plus the extra facts gathered for it.</summary>
public sealed record TimelineReviewInput(
    TimelineModel Model,
    string Name,
    IReadOnlyList<TimelineBranchTip> Tips,
    IReadOnlySet<string> Touching,
    IReadOnlyList<PullRequestFact>? PullRequests,
    IReadOnlyDictionary<string, TranscriptText> Transcripts,
    string? UserClaudeMd,
    IReadOnlyList<RepoFileFact> RepoFiles,
    DateTimeOffset Now)
{
    public bool HasPipeline => RepoFiles.Any(f => f.Name == "docs/pipeline-status.md" && f.Exists);
}

public sealed record TimelineReviewResult(TimelineReport Report, TimelineDigest Digest, string ReportPath, string DigestPath, TimeSpan ClaudeTime);

/// <summary>
/// Where the review is up to. Once the digest is built, <see cref="DigestChars"/> and <see cref="Estimate"/> say how big
/// it is and how long Claude Code should take on it (see <see cref="ReviewTimings"/>).
/// </summary>
public sealed record ReviewProgress(string Stage, int? DigestChars = null, TimeSpan? Estimate = null, string? EstimateBasis = null);

/// <summary>
/// How long Claude Code took per size of digest in earlier reviews, kept in a small file so the estimate shown while a
/// review runs comes from this machine's own history. Time is modelled as a fixed start-up cost plus a rate per KB; the
/// rate is the median of the recorded runs, and a built-in rate (from the first real run: 22 KB in 118 s) stands in
/// until there are any.
/// </summary>
public static class ReviewTimings
{
    public sealed record Run(int Chars, double Seconds);

    public static readonly string Path = System.IO.Path.Combine(TimelineReview.ReportRoot, "timings.json");
    public static readonly TimeSpan Overhead = TimeSpan.FromSeconds(40);
    public const double DefaultSecondsPerKb = 3.5;
    public const int Keep = 20;

    public static TimeSpan Estimate(IReadOnlyList<Run> history, int chars)
    {
        var rates = history.Where(r => r.Chars > 0 && r.Seconds > Overhead.TotalSeconds).Select(r => (r.Seconds - Overhead.TotalSeconds) / (r.Chars / 1024.0)).OrderBy(r => r).ToList();
        var rate = rates.Count == 0 ? DefaultSecondsPerKb : rates[rates.Count / 2];
        return Overhead + TimeSpan.FromSeconds(rate * chars / 1024.0);
    }

    public static string Basis(IReadOnlyList<Run> history) =>
        history.Count == 0 ? "a built-in rate" : $"{history.Count} earlier review{(history.Count == 1 ? "" : "s")} on this machine";

    public static List<Run> Load()
    {
        try
        {
            if (!File.Exists(Path)) return [];
            return JsonSerializer.Deserialize<List<Run>>(File.ReadAllText(Path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Appends a run, keeping the newest <see cref="Keep"/>. Never throws: a timing not saved only costs a less exact estimate.</summary>
    public static void Record(int chars, TimeSpan took)
    {
        try
        {
            var runs = Load();
            runs.Add(new Run(chars, took.TotalSeconds));
            if (runs.Count > Keep) runs.RemoveRange(0, runs.Count - Keep);
            Directory.CreateDirectory(TimelineReview.ReportRoot);
            File.WriteAllText(Path, JsonSerializer.Serialize(runs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>"about 2 min", "about 1½ min", "about 45 s".</summary>
    public static string Describe(TimeSpan estimate)
    {
        var halves = Math.Max(1, (int)Math.Round(estimate.TotalMinutes * 2));
        if (estimate.TotalSeconds < 50) return $"about {(int)Math.Round(estimate.TotalSeconds / 5.0) * 5} s";
        var whole = halves / 2;
        return halves % 2 == 0 ? $"about {whole} min" : whole == 0 ? "about ½ min" : $"about {whole}½ min";
    }
}

/// <summary>
/// Asks Claude Code, in print mode on the user's own login, how the project was built: the sessions, prompts, commits,
/// branches and pull requests of the timeline, condensed, against the rules the project should follow. Two parts, each
/// scored out of 10: the user's work with the agent (scored by the model) and the GitHub cycle (scored by AgentHub's own
/// checks), plus standing rules to add to the user-wide CLAUDE.md. Nothing is sent but the digest built here.
/// </summary>
public static partial class TimelineReview
{
    public static string ReportRoot { get; } = AgentHub.Hosting.AgentHubData.Folder("timeline");

    /// <summary>Newest sessions whose prompts and last reply go in full; older ones are summarised from their first prompt.</summary>
    public const int DetailedSessions = 25;
    public const int MaxPromptsPerSession = 40, PromptChars = 160, ReplyChars = 400, MaxOutsideRows = 40;
    public const int DigestBudget = 60 * 1024, UserFileBudget = 30 * 1024;
    public static readonly TimeSpan LongSession = TimeSpan.FromHours(4);
    public const int MaxFindings = 8, MaxAdditions = 5;

    /// <summary>Files whose commits show a session kept the project's written context up to date.</summary>
    public static readonly string[] ContextPaths = ["CLAUDE.md", ".agenthub/handoff.md", "docs"];

    private static readonly string[] RepoFileNames = ["CLAUDE.md", "AGENTS.md", ".agenthub/handoff.md", "docs/pipeline-status.md"];

    // ---- gathering ----

    /// <summary>Reads what the review needs beyond the model: touched files, pull requests, the newest transcripts, the user's CLAUDE.md.</summary>
    public static async Task<TimelineReviewInput> GatherAsync(ProcessRunner runner, TimelineScope scope, TimelineModel model, IProgress<ReviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ReviewProgress("Reading git and GitHub…"));
        var tips = await TimelineSource.ReadBranchTipsAsync(runner, scope, cancellationToken);
        var touching = await TimelineSource.ReadTouchingAsync(runner, scope, ContextPaths, cancellationToken);
        var prs = await TimelineSource.ReadPullRequestsAsync(runner, scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var sessions = model.Rows.Where(r => r.Kind == TimelineRowKind.Session).Select(r => r.Session!)
            .OrderByDescending(s => s.EndedAt).Take(DetailedSessions).ToList();
        var transcripts = new Dictionary<string, TranscriptText>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ReviewProgress($"Reading transcript {i + 1} of {sessions.Count}…"));
                var s = sessions[i];
                transcripts[s.Summary.FilePath] = TranscriptTexts.TryRead(s.Summary.FilePath, s.Summary.Agent);
            }
        }, cancellationToken);

        var userFile = InstructionFiles.GlobalFiles().First(f => f.Cli == InstructionCli.ClaudeCode).Path;
        string? userText = null;
        try { if (File.Exists(userFile)) userText = File.ReadAllText(userFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var repoFiles = RepoFileNames.Select(name => ReadRepoFile(scope.Root, name)).ToList();
        return new TimelineReviewInput(model, scope.Name, tips, touching, prs, transcripts, userText, repoFiles, DateTimeOffset.UtcNow);
    }

    private static RepoFileFact ReadRepoFile(string root, string name)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(path)) return new RepoFileFact(name, false, 0, []);
            var headings = File.ReadLines(path).Where(l => l.StartsWith('#')).Select(l => l.Trim()).Take(15).ToList();
            return new RepoFileFact(name, true, new FileInfo(path).Length, headings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RepoFileFact(name, false, 0, []);
        }
    }

    // ---- facts ----

    [GeneratedRegex(@"^(feat|fix|hotfix|chore|refactor|docs)/[a-z0-9][a-z0-9._/-]*$")]
    private static partial Regex BranchNameRegex();

    [GeneratedRegex(@"\(#\d+\)\s*$")]
    private static partial Regex SquashSubjectRegex();

    /// <summary>The mechanical checks, from the model, tips, touched files and pull requests. Deterministic, so a repeat review scores Part B the same.</summary>
    public static TimelineFacts Compute(TimelineReviewInput input)
    {
        var model = input.Model;
        var ids = SessionIds(model);
        var sessions = model.Rows.Where(r => r.Kind == TimelineRowKind.Session).ToList();
        var checks = new List<TimelineCheck>();

        // Part A: facts for the model to weigh; not scored here.
        var longOnes = sessions.Where(r => r.Session!.EndedAt - r.Session.Summary.StartedAt > LongSession).ToList();
        checks.Add(new TimelineCheck("A1", $"Sessions open longer than {LongSession.TotalHours:0} h", longOnes.Count, sessions.Count, longOnes.Select(r => ids[r.Key]).ToList()));

        var withCommits = sessions.Where(r => r.Commits.Count > 0).ToList();
        var noContext = withCommits.Where(r => !r.Commits.Any(c => input.Touching.Contains(c.Hash))).ToList();
        checks.Add(new TimelineCheck("A2", "Sessions that committed without touching CLAUDE.md, the handoff or docs", noContext.Count, withCommits.Count, noContext.Select(r => ids[r.Key]).ToList()));

        var noCommits = sessions.Where(r => r.Commits.Count == 0).ToList();
        checks.Add(new TimelineCheck("A3", "Sessions that made no commits", noCommits.Count, sessions.Count, noCommits.Select(r => ids[r.Key]).ToList()));

        var overlapping = new List<string>();
        var involved = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sessions.Count; i++)
            for (var j = i + 1; j < sessions.Count; j++)
            {
                var a = sessions[i].Session!;
                var b = sessions[j].Session!;
                if (a.Folder is null || !string.Equals(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.Summary.StartedAt < b.EndedAt && b.Summary.StartedAt < a.EndedAt)
                {
                    overlapping.Add($"{ids[sessions[i].Key]}+{ids[sessions[j].Key]}");
                    involved.Add(sessions[i].Key);
                    involved.Add(sessions[j].Key);
                }
            }
        checks.Add(new TimelineCheck("A4", "Sessions open at the same time in the same folder", involved.Count, sessions.Count, overlapping));

        // Part B: scored.
        var trunk = model.Branches.FirstOrDefault(b => b.IsTrunk);
        var trunkName = model.TrunkName;
        var prs = input.PullRequests ?? [];
        var mergedPrs = prs.Where(p => p.IsMerged).ToList();
        var prMergeHashes = prs.Where(p => p.MergeCommit is not null).Select(p => p.MergeCommit!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = model.Branches.Where(b => !b.IsTrunk && !b.IsImported).Select(b => b.Name)
            .Concat(prs.Select(p => p.Head)).Where(n => n.Length > 0 && n != trunkName).Distinct(StringComparer.Ordinal).ToList();
        var badNames = names.Where(n => !BranchNameRegex().IsMatch(n)).ToList();
        checks.Add(new TimelineCheck("B1", "Branch names not <type>/<short-description> (feat, fix, hotfix, chore, refactor)", badNames.Count, names.Count, badNames));

        var trunkCommits = (trunk?.Commits ?? []).Where(c => !c.IsMerge && c.Parents.Count > 0).ToList();
        var direct = trunkCommits.Where(c => !SquashSubjectRegex().IsMatch(c.Subject) && !prMergeHashes.Contains(c.Hash)).ToList();
        checks.Add(new TimelineCheck("B2", $"Commits made straight on {trunkName} (no pull request)", direct.Count, trunkCommits.Count, direct.Select(c => c.Short).ToList()));

        var mergeCommits = (trunk?.Commits ?? []).Where(c => c.IsMerge).ToList();
        var squashes = (trunk?.Commits ?? []).Where(c => !c.IsMerge && (SquashSubjectRegex().IsMatch(c.Subject) || prMergeHashes.Contains(c.Hash))).ToList();
        checks.Add(new TimelineCheck("B3", "Merges into the trunk as merge commits rather than squash merges", mergeCommits.Count, mergeCommits.Count + squashes.Count, mergeCommits.Select(c => c.Short).ToList()));

        var tipNames = input.Tips.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var mergedNames = model.Branches.Where(b => !b.IsTrunk && !b.IsImported && b.MergeHash is not null).Select(b => b.Name)
            .Concat(mergedPrs.Select(p => p.Head)).Where(n => n.Length > 0 && n != trunkName).Distinct(StringComparer.Ordinal).ToList();
        var leftover = mergedNames.Where(tipNames.Contains).ToList();
        checks.Add(new TimelineCheck("B4", "Branches still present after being merged", leftover.Count, mergedNames.Count, leftover));

        var selfMerged = mergedPrs.Where(p => p.Author is { Length: > 0 } && string.Equals(p.Author, p.MergedBy, StringComparison.OrdinalIgnoreCase)).ToList();
        checks.Add(new TimelineCheck("B5", "Pull requests merged by their own author", selfMerged.Count, mergedPrs.Count, selfMerged.Select(p => $"#{p.Number}").ToList()));

        var unreviewed = mergedPrs.Where(p => p.Reviews == 0).ToList();
        checks.Add(new TimelineCheck("B6", "Pull requests merged without a review", unreviewed.Count, mergedPrs.Count, unreviewed.Select(p => $"#{p.Number}").ToList()));

        var stacked = prs.Where(p => p.Base.Length > 0 && p.Base != trunkName).ToList();
        checks.Add(new TimelineCheck("B7", $"Pull requests based on a branch other than {trunkName}", stacked.Count, prs.Count, stacked.Select(p => $"#{p.Number} → {p.Base}").ToList()));

        var longTitles = prs.Where(p => p.Title.Length > 70).ToList();
        checks.Add(new TimelineCheck("B8", "Pull request titles over 70 characters", longTitles.Count, prs.Count, longTitles.Select(p => $"#{p.Number}").ToList()));

        var scored = checks.Where(c => c.Kind == ReviewKind.GitHub && c.Applies).ToList();
        int? score = scored.Count == 0 ? null : (int)Math.Round(10 * scored.Average(c => c.Compliance), MidpointRounding.AwayFromZero);
        return new TimelineFacts(checks, input.HasPipeline, input.PullRequests is not null, score, scored.Count);
    }

    /// <summary>Session ids by row key: S1 is the newest session, so the ids the model cites stay short.</summary>
    public static IReadOnlyDictionary<string, string> SessionIds(TimelineModel model)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var n = 1;
        foreach (var row in model.Rows.Where(r => r.Kind == TimelineRowKind.Session).OrderByDescending(r => r.Session!.EndedAt))
            ids[row.Key] = "S" + n++;
        return ids;
    }

    // ---- digest ----

    /// <summary>
    /// The text the model reads: the project's shape, the checks, the sessions newest first (prompts and last reply for the
    /// newest <see cref="DetailedSessions"/>, as far as the budget allows), commits outside sessions, branches, pull
    /// requests, the rules, and the user's CLAUDE.md. Kept under <see cref="DigestBudget"/> by thinning the older sessions.
    /// </summary>
    public static string Title(ReviewKind kind) => kind == ReviewKind.GitHub ? "GitHub cycle" : "You and Claude";

    public static TimelineDigest Build(TimelineReviewInput input, TimelineFacts facts, ReviewKind kind)
    {
        var model = input.Model;
        var ids = SessionIds(model);
        var github = kind == ReviewKind.GitHub;
        var fixedPart = new StringBuilder();

        // Project
        var branches = model.Branches.Where(b => !b.IsTrunk).ToList();
        var first = model.Rows.Count > 0 ? model.Rows[0].Time : input.Now;
        fixedPart.AppendLine("<project>");
        fixedPart.AppendLine($"name: {input.Name}");
        fixedPart.AppendLine($"trunk: {model.TrunkName}");
        fixedPart.AppendLine($"sessions: {model.SessionCount}; commits: {model.CommitCount}; branches besides the trunk: {branches.Count} " +
                             $"(merged {branches.Count(b => b.MergeHash is not null && !b.IsImported)}, open {branches.Count(b => b.MergeHash is null && !b.IsImported)}, imported {branches.Count(b => b.IsImported)})");
        fixedPart.AppendLine($"span: {Date(first)} → {Date(input.Now)}");
        fixedPart.AppendLine($"docs/pipeline-status.md: {(input.HasPipeline ? "present, so the pipeline-stage rules apply" : "absent, so the pipeline-stage rules do not apply")}");
        foreach (var f in input.RepoFiles.Where(f => f.Name != "docs/pipeline-status.md"))
            fixedPart.AppendLine($"{f.Name}: {(f.Exists ? $"{f.Bytes / 1024.0:0.#} KB" + (f.Headings.Count > 0 ? "; headings: " + string.Join(" | ", f.Headings) : "") : "absent")}");
        fixedPart.AppendLine($"github facts: {(input.PullRequests is { } prs ? $"{prs.Count} pull requests read through gh" : "unavailable (gh failed or no GitHub remote), so reviewer and base-branch checks could not run")}");
        fixedPart.AppendLine("</project>");
        fixedPart.AppendLine();

        // Facts
        fixedPart.AppendLine("<facts>");
        fixedPart.AppendLine("Checks AgentHub computed from git, the transcripts and GitHub. They are authoritative: reason about them, do not recount.");
        foreach (var c in facts.Checks.Where(c => c.Kind == kind))
        {
            if (!c.Applies) { fixedPart.AppendLine($"{c.Id} {c.Name}: not applicable (nothing to check)"); continue; }
            var who = c.Offenders.Count == 0 ? "" : " (" + string.Join(", ", c.Offenders.Take(12)) + (c.Offenders.Count > 12 ? $", +{c.Offenders.Count - 12} more" : "") + ")";
            fixedPart.AppendLine($"{c.Id} {c.Name}: {c.Failing} of {c.Total}{who}");
        }
        if (github) fixedPart.AppendLine(facts.GitHubScore is { } sc ? $"Score from these checks: {sc}/10 over {facts.GitHubChecks} applicable checks" : "Score: no applicable check");
        fixedPart.AppendLine("</facts>");
        fixedPart.AppendLine();

        // Branches: the GitHub review's subject; the sessions review only needs the lane named on each session.
        var byHash = model.Branches.SelectMany(b => b.Commits).GroupBy(c => c.Hash).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var prByHead = (input.PullRequests ?? []).GroupBy(p => p.Head).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Number).First(), StringComparer.Ordinal);
        if (github) fixedPart.AppendLine("<branches>");
        foreach (var b in github ? model.Branches : [])
        {
            var commits = b.Commits.OrderBy(c => c.Time).ToList();
            var span = commits.Count > 0 ? $"{Date(commits[0].Time)} → {Date(commits[^1].Time)}" : "no commits";
            var how = b.IsTrunk ? "trunk"
                : b.IsImported ? "imported history" + (b.MergeHash is not null ? $", joined at {Short(b.MergeHash)}" : "")
                : b.MergeHash is not null ? (byHash.TryGetValue(b.MergeHash, out var m) && m.IsMerge ? $"merged by merge commit {m.Short}" : $"merged at {Short(b.MergeHash)}")
                : prByHead.TryGetValue(b.Name, out var pr) && pr.IsMerged ? $"merged through PR #{pr.Number}" + (pr.MergeCommit is null ? ""
                    : byHash.TryGetValue(pr.MergeCommit, out var pm) && pm.IsMerge ? $" (merge commit {pm.Short})" : $" (squash {Short(pr.MergeCommit)})")
                : "not merged";
            fixedPart.AppendLine($"{b.Name}: {commits.Count} commits, {span}, {how}{(b.IsLocalOnly ? ", not pushed (local only)" : "")}");
        }
        if (github)
        {
            fixedPart.AppendLine("</branches>");
            fixedPart.AppendLine();
        }

        // Pull requests
        if (github && input.PullRequests is { Count: > 0 } all)
        {
            fixedPart.AppendLine("<pull-requests>");
            foreach (var p in all.OrderByDescending(p => p.Number))
            {
                var state = p.IsMerged ? $"merged {(p.MergedAt is { } at ? Date(at) : "")} by {p.MergedBy ?? "?"}" : p.State.ToLowerInvariant();
                fixedPart.AppendLine($"#{p.Number} \"{p.Title}\" {p.Head} → {p.Base} · author {p.Author ?? "?"} · {state} · {p.Reviews} review{(p.Reviews == 1 ? "" : "s")}{(p.IsDraft ? " · draft" : "")}");
            }
            fixedPart.AppendLine("</pull-requests>");
            fixedPart.AppendLine();
        }

        // Outside commits
        var outside = model.Rows.Where(r => r.Kind == TimelineRowKind.Commits).OrderByDescending(r => r.Time).ToList();
        if (outside.Count > 0)
        {
            fixedPart.AppendLine("<outside-sessions>");
            fixedPart.AppendLine($"Commits made while no session was active (by hand, on GitHub or elsewhere): {outside.Sum(r => r.Commits.Count)} in {outside.Count} runs, newest first.");
            foreach (var r in outside.Take(MaxOutsideRows))
            {
                var lane = r.Branch?.Name ?? model.TrunkName;
                var subjects = string.Join("; ", r.Commits.Take(3).Select(c => $"{c.Short} {Cut(c.Subject, 70)}"));
                fixedPart.AppendLine($"{Date(r.Time)} · {r.Commits.Count} commit{(r.Commits.Count == 1 ? "" : "s")} on {lane}{(r.Branch is { IsImported: true } ? " (imported)" : "")}: {subjects}{(r.Commits.Count > 3 ? "; …" : "")}");
            }
            if (outside.Count > MaxOutsideRows) fixedPart.AppendLine($"… {outside.Count - MaxOutsideRows} older runs left out");
            fixedPart.AppendLine("</outside-sessions>");
            fixedPart.AppendLine();
        }

        // Rules and the user's file
        var tail = new StringBuilder();
        tail.AppendLine("<rules>");
        tail.AppendLine(RulesFor(kind, input.HasPipeline));
        tail.AppendLine("</rules>");
        tail.AppendLine();
        tail.AppendLine("<user-claude-md>");
        if (input.UserClaudeMd is { } user)
        {
            var userText = user.Replace("\r\n", "\n");
            tail.AppendLine(userText.Length > UserFileBudget ? userText[..UserFileBudget] + "\n… (cut)" : userText);
        }
        else tail.AppendLine("(the user-wide CLAUDE.md does not exist yet)");
        tail.AppendLine("</user-claude-md>");

        // Sessions, newest first. The sessions review gets them in full (prompts, last reply) while the budget allows,
        // then one line each; the GitHub review only needs the one-line form (when, which lane, which commits).
        var sessions = new StringBuilder();
        sessions.AppendLine("<sessions>");
        sessions.AppendLine("Newest first. A session's lane is the branch most of its commits went to. Times are local to the user.");
        var remaining = DigestBudget - fixedPart.Length - tail.Length - 200;
        var rows = model.Rows.Where(r => r.Kind == TimelineRowKind.Session).OrderByDescending(r => r.Session!.EndedAt).ToList();
        // Every session gets at least its one-line form, so that is reserved first; a session gets its full form only
        // while the full form plus the one-liners of everything older still fits.
        var briefs = rows.Select(r => SessionBlock(r, ids[r.Key], input, model.TrunkName, detailed: false)).ToList();
        var olderBriefs = briefs.Sum(b => b.Length);
        for (var i = 0; i < rows.Count; i++)
        {
            olderBriefs -= briefs[i].Length;
            var full = github ? briefs[i] : SessionBlock(rows[i], ids[rows[i].Key], input, model.TrunkName, detailed: true);
            var block = full.Length + olderBriefs <= remaining ? full : briefs[i];
            sessions.Append(block);
            remaining -= block.Length;
        }
        sessions.AppendLine("</sessions>");
        sessions.AppendLine();

        var text = new StringBuilder().Append(fixedPart).Append(sessions).Append(tail).ToString();
        return new TimelineDigest(text, ids.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal));
    }

    private static string SessionBlock(TimelineRow row, string id, TimelineReviewInput input, string trunkName, bool detailed)
    {
        var s = row.Session!;
        var sb = new StringBuilder();
        var folder = s.Folder is { } f ? Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar)) : "?";
        var lane = row.Branch?.Name ?? (row.Commits.Count > 0 ? trunkName : "none (no commits)");
        var touched = row.Commits.Count == 0 ? "" : row.Commits.Any(c => input.Touching.Contains(c.Hash)) ? " · updated CLAUDE.md/handoff/docs" : " · did not touch CLAUDE.md, handoff or docs";
        input.Transcripts.TryGetValue(s.Summary.FilePath, out var transcript);
        var promptCount = transcript is not null ? transcript.Prompts.Count.ToString(CultureInfo.InvariantCulture) : "?";
        sb.AppendLine($"{id} · {Stamp(s.Summary.StartedAt)} → {Stamp(s.EndedAt)} ({Hours(s.EndedAt - s.Summary.StartedAt)}) · {(s.Summary.Agent == Context.ContextSource.Codex ? "Codex" : "Claude Code")} · folder {folder} · lane {lane} · {row.Commits.Count} commits · {promptCount} prompts{touched}");
        if (!detailed || transcript is null)
        {
            if (FirstLine(s.Summary.Title ?? s.Summary.FirstPrompt) is { } title) sb.AppendLine($"  about: {Cut(title, PromptChars)}");
            if (row.Commits.Count > 0) sb.AppendLine("  commits: " + string.Join("; ", row.Commits.Take(4).Select(c => $"{c.Short} {Cut(c.Subject, 60)}")) + (row.Commits.Count > 4 ? "; …" : ""));
            return sb.ToString();
        }
        if (row.Commits.Count > 0) sb.AppendLine("  commits: " + string.Join("; ", row.Commits.Select(c => $"{c.Short} {Cut(c.Subject, 70)}")));
        if (transcript.Prompts.Count > 0)
        {
            sb.AppendLine("  prompts:");
            foreach (var p in transcript.Prompts.Take(MaxPromptsPerSession))
                sb.AppendLine($"    - {(p.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " " : "")}{Cut(OneLine(p.Text), PromptChars)}");
            if (transcript.Prompts.Count > MaxPromptsPerSession) sb.AppendLine($"    … {transcript.Prompts.Count - MaxPromptsPerSession} more prompts");
        }
        if (transcript.LastReply is { } reply) sb.AppendLine($"  last reply: {Cut(OneLine(reply), ReplyChars)}");
        return sb.ToString();
    }

    /// <summary>
    /// The rules the project is judged against, as data in the request rather than the system prompt, so they can be
    /// revised without touching the prompt. Source: IGO's engineering standards (engineering-at-igo: git-workflow,
    /// pull-requests, rollback-recovery) and the IGO D&amp;D build pipeline (commit gate, stage guides, lessons), as cached
    /// in the dev kit on 2026-11-04. The pipeline-stage rules go in only for projects that have docs/pipeline-status.md.
    /// </summary>
    public static string RulesFor(ReviewKind kind, bool hasPipeline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Source: IGO engineering standards and the IGO D&D build pipeline. Judge against these, not personal taste.");
        sb.AppendLine();
        if (kind == ReviewKind.GitHub)
        {
            sb.AppendLine("The GitHub cycle:");
            sb.AppendLine("- Never commit or push straight to the trunk: branch first, open a pull request, merge through it.");
            sb.AppendLine("- Branch names: <type>/<short-description>, lowercase with hyphens, type in feat, fix, hotfix, chore, refactor (docs tolerated).");
            sb.AppendLine("- Commit messages: Conventional Commits, extremely concise.");
            sb.AppendLine("- Squash-merge only; delete the branch after the merge.");
            sb.AppendLine("- Every pull request's base is the trunk, never a sibling branch (a stacked PR merged into its sibling has lost a stage before).");
            sb.AppendLine("- At least one human reviewer; nobody merges their own PR; reply to every comment before merging. A bot review is additional, not a replacement.");
            sb.AppendLine("- PR title verb-led and under 70 characters; one logical change per PR; every template section filled; Draft while in progress.");
            sb.AppendLine("- Never rewrite pushed history: fix forward, and revert through GitHub when a bad commit reached the trunk.");
            sb.AppendLine("- CI runs on pull_request; dev deploys on push to the trunk; prod is a manual workflow_dispatch that promotes one SHA.");
            if (hasPipeline)
                sb.AppendLine("- Pipeline projects (this one has docs/pipeline-status.md): branches chore/stage-N-slug for design stages and feat/stage-N-slug for builds; one PR per stage; the agent opens the PR and does not merge it; a stage is not finished until it is on the trunk.");
            return sb.ToString().TrimEnd();
        }
        sb.AppendLine("The user and Claude:");
        sb.AppendLine("- One piece of work per session; start a fresh session for the next one. A session that runs long is restarted: commit, rewrite the handoff, start again. Long sessions pile up half-remembered decisions, superseded plans and troubleshooting noise.");
        sb.AppendLine("- Context lives in the repository, not the conversation: at the end of every session the handoff (CLAUDE.md \"Where things stand\" or .agenthub/handoff.md) is rewritten, newest work first: what is merged, what runs where, what is next.");
        sb.AppendLine("- Verify, never ask: before saying work is committed or pushed, the agent runs git status, git log @{u}..HEAD and the branch name itself and reads the output. \"I committed\" without evidence does not pass. A merged badge is not evidence that the work is on the trunk.");
        sb.AppendLine("- Unit tests and lint green before a commit; hand verification written into the docs.");
        sb.AppendLine("- Settled decisions are not re-asked. A prompt the user has to repeat across sessions is a standing rule that belongs in CLAUDE.md.");
        sb.AppendLine("- Personal standing preferences live in the user-wide ~/.claude/CLAUDE.md; project facts in the repository's own CLAUDE.md, which outranks the standards. Never retrofit an existing repository as a side effect.");
        sb.AppendLine("- Two autonomous agents never edit the same working tree at once; parallel work goes in git worktrees.");
        sb.AppendLine("- Handoffs never say \"live\", \"real\" or \"in the cloud\" unqualified; they name the environment.");
        if (hasPipeline)
        {
            sb.AppendLine();
            sb.AppendLine("Pipeline projects (this one has docs/pipeline-status.md):");
            sb.AppendLine("- Stages run in fresh sessions: fake UI, architecture, build order, stakeholder releases, implementation contract, then build sessions. \"Not in this stage\" is binding; change docs/build-plan.md first (dated, with the reason) before building something else.");
            sb.AppendLine("- A stage is not finished until it is on the trunk; nothing of the next stage starts before that. pipeline-status.md gets a commit-gate row (branch, PR, merge SHA, date).");
            sb.AppendLine("- Branches: chore/stage-N-slug for design stages, feat/stage-N-slug for builds; one PR per stage; the agent opens the PR and does not merge it.");
            sb.AppendLine("- Read lessons/stage-N.md at the start of a stage; record a lesson at the gate.");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- the model ----

    private const string PromptCommon = """
        The request holds the project's shape; the checks AgentHub computed (authoritative: reason about them, never recount);
        the sessions newest first; commits made outside sessions; the rules the project is judged against; and the user's
        user-wide CLAUDE.md.

        Also return additions: at most 5 standing rules for the user-wide CLAUDE.md that would have prevented a finding. Each
        has a short title and a few Markdown lines an agent can follow in any project: never project facts, paths, names or
        dates, and nothing the user's file already says. Return none when the file already covers what went wrong.

        Findings: at most 8, most valuable first. "what" is one sentence naming the pattern. "evidence" cites only ids from
        the request: session ids such as S3, short commit hashes, branch names, PR numbers like #12, with dates where useful.
        "instead" says concretely what to do next time. "who" is "you" when the user should change something, "claude" when
        the agent should, "both" when both. Never invent events, people or files. If the practice is already good, say so
        with few or no findings. Plain, concise sentences; no Markdown headings inside strings.
        """;

    public const string InteractionPrompt = """
        You review how a user and their coding agent (Claude Code, Codex) worked together on a software project, from the
        story of the sessions, and advise both of them on what to do differently next time. Each session comes with the
        prompts the user typed and the session's last reply. Judge against the rules in the request: session length and
        scope, handoffs kept up to date, verifying rather than asking, tests before commits, decisions re-asked, prompts
        that repeat a standing instruction, parallel work in one folder, work left uncommitted. Give a score from 0 to 10:
        10 means nothing worth changing, and every point lost must be explained by a finding.

        """ + PromptCommon;

    public const string GitHubPrompt = """
        You review a software project's GitHub cycle, branches, pull requests and merges, from its Git history, its pull
        requests and the sessions that made the commits, and advise both the user and their coding agent on what to do
        differently next time. Judge against the rules in the request. AgentHub has already scored this cycle from its
        checks; you write the verdict and the findings, using the checks' evidence, and say what to do instead.

        """ + PromptCommon;

    private const string FindingSchema = """
        {"type":"array","items":{"type":"object","additionalProperties":false,"required":["what","evidence","instead","who"],"properties":{
          "what":{"type":"string"},"evidence":{"type":"string"},"instead":{"type":"string"},"who":{"type":"string","enum":["you","claude","both"]}}}}
        """;

    private const string AdditionsSchema = """
        {"type":"array","items":{"type":"object","additionalProperties":false,"required":["title","markdown","why"],"properties":{
          "title":{"type":"string"},"markdown":{"type":"string"},"why":{"type":"string"}}}}
        """;

    /// <summary>The reply shape: a score only for the sessions review (the GitHub score is AgentHub's).</summary>
    public static string SchemaFor(ReviewKind kind)
    {
        var score = kind == ReviewKind.Interaction ? "\"score\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10}," : "";
        var required = kind == ReviewKind.Interaction ? "\"summary\",\"score\",\"verdict\",\"findings\",\"additions\"" : "\"summary\",\"verdict\",\"findings\",\"additions\"";
        return "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[" + required + "],\"properties\":{" +
               "\"summary\":{\"type\":\"string\",\"description\":\"Two or three sentences: the overall verdict.\"}," + score +
               "\"verdict\":{\"type\":\"string\"},\"findings\":" + FindingSchema.Trim() + ",\"additions\":" + AdditionsSchema.Trim() + "}}";
    }

    public static string PromptFor(ReviewKind kind) => kind == ReviewKind.Interaction ? InteractionPrompt : GitHubPrompt;

    /// <summary>Reads the model's reply into a report; a GitHub review is scored from <paramref name="facts"/>, not the reply.</summary>
    public static TimelineReport Parse(JsonElement root, TimelineFacts facts, ReviewKind kind)
    {
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

        var findings = new List<ReviewFinding>();
        if (root.TryGetProperty("findings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var f in arr.EnumerateArray().Take(MaxFindings))
            {
                var w = Str(f, "who"); var who = w is "you" or "claude" or "both" ? w : "both";
                if (Str(f, "what").Length > 0) findings.Add(new ReviewFinding(Str(f, "what"), Str(f, "evidence"), Str(f, "instead"), who));
            }

        ReviewPart part;
        if (kind == ReviewKind.Interaction)
        {
            int? score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? Math.Clamp(s.GetInt32(), 0, 10) : null;
            part = new ReviewPart(score, "Claude's judgement", Str(root, "verdict"), findings);
        }
        else
            part = new ReviewPart(facts.GitHubScore, facts.GitHubScore is null ? "no applicable check" : $"from {facts.GitHubChecks} check{(facts.GitHubChecks == 1 ? "" : "s")}", Str(root, "verdict"), findings);

        var additions = new List<ReviewAddition>();
        if (root.TryGetProperty("additions", out var adds) && adds.ValueKind == JsonValueKind.Array)
            foreach (var add in adds.EnumerateArray().Take(MaxAdditions))
                if (Str(add, "title").Length > 0 && Str(add, "markdown").Length > 0)
                    additions.Add(new ReviewAddition(Str(add, "title").TrimStart('#', ' '), Str(add, "markdown"), Str(add, "why")));

        return new TimelineReport(kind, Str(root, "summary"), part, additions, facts);
    }

    /// <summary>
    /// The additions as review edits for the Context editor: each appended to the user-wide file as a section, skipping a
    /// title the file already has as a heading. Shown with Apply boxes; nothing reaches disk until the user saves.
    /// </summary>
    public static ReviewSuggestion ToSuggestion(TimelineReport report, string userFileText)
    {
        var headings = LineDiff.SplitLines(userFileText).Where(l => l.StartsWith('#')).Select(l => l.TrimStart('#', ' ').Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edits = report.Additions.Where(a => !headings.Contains(a.Title))
            .Select(a => new ReviewEdit(a.Why, "", "", $"\n\n## {a.Title}\n{a.Markdown.Replace("\r\n", "\n").Trim()}"))
            .ToList();
        var skipped = report.Additions.Count - edits.Count;
        var summary = edits.Count == 0
            ? "Nothing to add: the review suggested no rule the file does not already have."
            : $"From the timeline review of the project: {edits.Count} standing rule{(edits.Count == 1 ? "" : "s")} to add" + (skipped > 0 ? $" ({skipped} already covered by a heading here)." : ".");
        return new ReviewSuggestion(summary, edits);
    }

    // ---- output ----

    public static string ToMarkdown(TimelineReport report, string name, DateTimeOffset when)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Timeline review · {Title(report.Kind)} · {name}");
        sb.AppendLine();
        sb.AppendLine($"Reviewed {when.ToLocalTime():ddd d MMM yyyy HH:mm} by Claude Code from AgentHub's timeline (git, transcripts and GitHub). " +
                      (report.Kind == ReviewKind.Interaction ? "The score is Claude's judgement." : "The score is computed from AgentHub's checks; the findings are Claude's."));
        sb.AppendLine();
        if (report.Summary.Length > 0) { sb.AppendLine(report.Summary); sb.AppendLine(); }
        Part(sb, Title(report.Kind), report.Part);

        sb.AppendLine("## Checks");
        sb.AppendLine();
        sb.AppendLine("| Check | Failing | Of | Which |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in report.Facts.Checks.Where(c => c.Kind == report.Kind))
            sb.AppendLine($"| {c.Id} {c.Name} | {(c.Applies ? c.Failing.ToString(CultureInfo.InvariantCulture) : "n/a")} | {c.Total} | {string.Join(", ", c.Offenders.Take(12))}{(c.Offenders.Count > 12 ? $", +{c.Offenders.Count - 12} more" : "")} |");
        sb.AppendLine();

        sb.AppendLine("## Suggested additions to ~/.claude/CLAUDE.md");
        sb.AppendLine();
        if (report.Additions.Count == 0) sb.AppendLine("None.");
        foreach (var a in report.Additions)
        {
            sb.AppendLine($"### {a.Title}");
            if (a.Why.Length > 0) sb.AppendLine($"_{a.Why}_");
            sb.AppendLine();
            sb.AppendLine(a.Markdown.Trim());
            sb.AppendLine();
        }
        return sb.ToString();

        static void Part(StringBuilder sb, string title, ReviewPart part)
        {
            sb.AppendLine($"## {title} · {(part.Score is { } s ? $"{s}/10" : "not scored")} ({part.ScoreSource})");
            sb.AppendLine();
            if (part.Verdict.Length > 0) { sb.AppendLine(part.Verdict); sb.AppendLine(); }
            var n = 1;
            foreach (var f in part.Findings)
            {
                sb.AppendLine($"{n++}. **{f.What}** · {Who(f.Who)}");
                if (f.Evidence.Length > 0) sb.AppendLine($"   Evidence: {f.Evidence}");
                if (f.Instead.Length > 0) sb.AppendLine($"   Instead: {f.Instead}");
            }
            if (part.Findings.Count == 0) sb.AppendLine("No findings.");
            sb.AppendLine();
        }
    }

    public static string Who(string who) => who switch { "you" => "for you", "claude" => "for Claude", _ => "for both" };

    /// <summary>Writes the report and the digest beside it under <see cref="ReportRoot"/>\name, and returns both paths.</summary>
    public static (string ReportPath, string DigestPath) Save(string name, TimelineReport report, TimelineDigest digest, DateTimeOffset when)
    {
        var safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var folder = Path.Combine(ReportRoot, safe.Length > 0 ? safe : "repo");
        Directory.CreateDirectory(folder);
        var stamp = when.ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var kind = report.Kind == ReviewKind.GitHub ? "github" : "sessions";
        var reportPath = Path.Combine(folder, $"review-{kind}-{stamp}.md");
        var digestPath = Path.Combine(folder, $"digest-{kind}-{stamp}.txt");
        File.WriteAllText(reportPath, ToMarkdown(report, name, when), new UTF8Encoding(false));
        File.WriteAllText(digestPath, digest.Text, new UTF8Encoding(false));
        return (reportPath, digestPath);
    }

    /// <summary>Gathers, asks Claude Code and saves. Throws <see cref="OperationCanceledException"/> when cancelled, <see cref="InvalidOperationException"/> on failure.</summary>
    public static async Task<TimelineReviewResult> RunAsync(ProcessRunner runner, string claudeCommand, TimelineScope scope, TimelineModel model, ReviewKind kind,
        IProgress<ReviewProgress>? progress, CancellationToken cancellationToken)
    {
        var input = await GatherAsync(runner, scope, model, progress, cancellationToken);
        var facts = Compute(input);
        var digest = Build(input, facts, kind);
        var history = ReviewTimings.Load();
        progress?.Report(new ReviewProgress("Asking Claude Code…", digest.Text.Length, ReviewTimings.Estimate(history, digest.Text.Length), ReviewTimings.Basis(history)));
        var started = DateTime.UtcNow;
        using var reply = await ClaudePrint.RunAsync(runner, claudeCommand, PromptFor(kind), SchemaFor(kind), digest.Text, cancellationToken);
        var took = DateTime.UtcNow - started;
        ReviewTimings.Record(digest.Text.Length, took);
        var report = Parse(reply.RootElement, facts, kind);
        var (reportPath, digestPath) = Save(scope.Name, report, digest, input.Now);
        return new TimelineReviewResult(report, digest, reportPath, digestPath, took);
    }

    // ---- text helpers ----

    private static string Date(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Stamp(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static string Hours(TimeSpan span) => span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min" : span.TotalDays < 1 ? $"{span.TotalHours:0.#} h" : $"{span.TotalDays:0.#} d";
    private static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;
    private static string Cut(string text, int max) => text.Length > max ? text[..max].TrimEnd() + "…" : text;
    private static string OneLine(string text) => Regex.Replace(text.Trim(), @"\s+", " ");
    private static string? FirstLine(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Split('\n')[0].Trim();
}

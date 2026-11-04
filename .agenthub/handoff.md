# AgentHub Handoff Context

**Updated:** 2026-11-04 (Timeline review session, afternoon)
**Branch with the newest work:** `feature/timeline-review` (local, **not pushed**, built on `main` `4ee8905`): ✨ Review on the Timeline window (section 3a). The taskbar build now runs this branch (pid 9440, opened on this repo's timeline); **Daniel has not yet clicked Review in the app**, only seen the live run's report.
**Next task:** Daniel's verdict on the review (report at `C:\data\agenthub\timeline\diag-report.md`, digest beside it); then push and merge `feature/timeline-review` when he asks. Queued after that: republish the taskbar build (section 1), Timeline phase 2 (section 3), the follow-ups (section 4).

Read `README.md`, `docs/ARCHITECTURE.md` and `docs/ROADMAP.md` first; `AGENTS.md` has the engineering rules.

---

## 1. Where everything is

| What | State |
|---|---|
| `main` = `origin/main` `d53ebf3` | Pushed 2026-11-04 as a fast-forward of `feature/timeline` (which contained `feature/context-review`). CI (`build.yml`) green on all three branches. Holds: ✨ Review of context files, 🌳 Timeline phase 1, dark window chrome everywhere, plus the 2026-11-03 work (session picker PR #6, 📄 Context / 📁 Repo context editors, two-row pane header, three-step launcher, replaced-session event fix). |
| `feature/timeline-review` | Local only, eight commits on top of `main` (`e8cf72e` the feature … `d39d647` the reports column). Tests 178 pass, 4 live skipped. Not pushed, not merged: wait for Daniel. |
| `feature/context-review`, `feature/timeline` | Pushed, merged, finished. Safe to delete on GitHub when Daniel wants. |
| ✨ Review | Daniel tested the first version (timed out on the 91 KB De Beers `CLAUDE.md`); the fixed version passed live tests (91 KB in ~170 s) but **Daniel has not retried it in the app**. |
| 🌳 Timeline | **Approved by Daniel 2026-11-04** ("looks good, leave it as it is") after four rounds of corrections, all in `main` (section 3). |
| Taskbar build | `src\AgentHub\bin\Release\net8.0-windows\win-x64\publish` (inside the repo), updated with `C:\data\agenthub\publish-local.ps1 -Repo <repo>`. **Republished 2026-11-04 13:12 from `feature/timeline-review` `d39d647`** (Daniel asked), so it runs an unmerged branch until that merges: Review, Timeline, timeline review, dark chrome all in. The `*.old-*` leftovers were deleted (no instance was running). Daniel's instance from it: pid 9440, opened on this repo's timeline. Republish only when Daniel asks. |
| Test builds | `C:\data\agenthub\build-test1` … `build-test17`, `build-check`: throwaway. `build-test16` = `main`; `build-test17` = `feature/timeline-review` (running, pid 17648). Delete the folders once no instance runs from them. |

Pushing: the repo is `Daznl/AgentHub` (Daniel's personal account). `gh auth switch -u Daznl`, push, then `gh auth switch -u DanielWallisBarker` straight after. Push or merge only when Daniel asks. Fast-forward is fine when the branch was built on `main`; check `origin/main` first (`git fetch`, `git log main..origin/main`).

## 2. Working rules (learned the hard way; still apply)

- **Never automate Daniel's desktop** (no UI Automation clicks, no mouse). Screenshots with `C:\data\agenthub\screens\shot.ps1 -ProcId <pid> -Out <png> -TitleLike <part of window title>` (PrintWindow, read-only) are fine and were used all day. Anything needing clicks or hover: launch a test build and ask Daniel.
- **Never kill Daniel's running AgentHub instance** (the one under `publish`); it holds live Claude Code sessions. Build to a separate folder: `dotnet build src/AgentHub/AgentHub.csproj -c Release -o C:\data\agenthub\build-testN`. A folder is locked while an instance runs from it, so bump N. Test copies Claude started may be stopped by Claude.
- Launch a test copy with `Start-Process C:\data\agenthub\build-testN\AgentHub.exe -ArgumentList '--timeline', '"<folder>"'` and give Daniel the process id. He will pop it open on his screen, so what the screenshot shows may already have been scrolled or clicked by him.
- Tests: `dotnet test tests/AgentHub.Tests/AgentHub.Tests.csproj -c Release`. 174 pass, 4 live tests skipped. Real-repository checks run only with `AGENTHUB_TIMELINE_REPO=<repo>` (and `AGENTHUB_TIMELINE_ARCHIVE=C:\data\agenthub\history`); the live Review test only with `AGENTHUB_LIVE_REVIEW=1` (spends Claude usage). Temporary diagnostic tests (`TimelineDiag.cs`) were the quickest way to question real data; delete them before committing.
- Python is not installed; use PowerShell or C#. PowerShell 5.1 strips quotes from JSON passed to native programs: pass JSON arguments from Bash.
- Editing files from Bash with `perl -pi` bit three times today: `$"` interpolates, `\|` in an `s|…|…|` pattern breaks it, `\n` and `\u` in replacements get eaten. Use the Edit tool for anything with quotes, pipes or escapes.
- Data and scratch output go under `C:\data\agenthub\`, never AppData or Temp.

## 3. Timeline: what was built and why (phase 1, in `main`)

Daniel's ask: the story of a project from session 1 to now, matching the GitHub commits, as a tree from a starting point, sessions collapsible, parallel work side by side; later an agent summarises each reply as sessions run.

**Window** (`TimelineWindow.xaml(.cs)`): pane header **🌳** → `MainWindow.ShowTimeline(folder)`, one window per repository root (all worktrees), non-modal; `AgentHub.exe --timeline <folder>` opens one at start-up. Header: name, "N sessions · M commits · since … · worktrees · root", Refresh. **Three verticals per row**, top to bottom in time: the tree (`TimelineGutter`), the session card (or Start), and a commits card on the right level with the session that made the commits; outside commits are a right-hand card alone. Commits card: first 3 commits shown, chevron for the rest, "not merged" on open branches, "imported from another repository" on imported lanes. Session card expands to its prompts (lazy, cached by file length) and 🕘 Open in History. Re-reads git and transcripts every 20 s but only redraws when the model's signature changed. **Lane highlighting:** hover a line → that branch's lane bold everywhere, its cards framed in the lane colour, the rest faint, status line with counts and merged state; click pins, click on empty space / another lane / Esc clears.

**The tree is Git's branch graph** (`BranchGraph.Assign` in `TimelineBuilder.cs`). Daniel's first version used session-overlap lanes and he read the trunk as `main`; it now is:
- Trunk = `main`/`master` first-parent chain from local *and* remote tips (local `main` in De Beers is 86 commits behind `origin/main`).
- Each merge commit on the trunk recovers the branch it merged (commits reachable from its other parents that nothing claimed yet), named after a surviving tip in that set, else from the subject ("Merge pull request #N from owner/branch", "Merge branch 'x'"). A recovered branch named like the trunk is trunk. Remaining tips claim what they reach: open branches. Dangling commits → trunk. `git log --branches --remotes` (no stashes, no tags) + `git for-each-ref` (`origin/HEAD` dropped).
- **Imported history:** each commit's root set (`RootsOf`, keyed by hash: `TimelineCommit.Branch` is mutable and records hash by value, so a commit is no dictionary key). The project's root = the root with most exclusive descendants (trunk-chain root breaks ties; GitHub's one-commit "Initial commit" must not win). Descendants of another root that never meet the project's root are an `IsImported` lane: no fork, joined at the first commit descending from both. De Beers has three roots (Explorer 22 Sep, GitHub Initial commit 23 Sep, Search API 6 Oct brought in whole by `65a9d61`).
- **Lanes** (`Layout`): a branch spans its first to last row; merged → `MergesBack` always, ending at the lower of its last own row and the row above the merge commit (the merge row itself when on the lane). Columns by interval scheduling; spacing shrinks past 6 columns (`TimelineGutter.LaneWidth`). A session sits on the lane most of its commits are on, feature over trunk (Daniel's choice); no commits → trunk with a hollow node. `TimelineLine.Key` carries the branch key for highlighting.
- `TimelineGraphInvariantTests.AssertConnected` runs in every builder test: lines continuous row to row, every lane forks (unless imported), merges only at the lane's end. It found nothing on De Beers; the "lines ending in the air" Daniel saw were the merged-lane rule, fixed.

**Commit attribution** (`TimelineBuilder.Attribute`): candidates = sessions whose window covers the commit (2 min before first entry to 5 min after last) **and** whose transcript wrote an entry within 4 min of it (`TranscriptScan` collects entry minutes; Daniel keeps a session open for 12 days and it was tying every commit). Ties: hash mention, then the commit subject found in the transcript entries 15 min before to 2 min after (`TranscriptScan.MentionsBetween`, reads only that byte range via per-minute offsets). No active session → outside (by hand or GitHub web). De Beers: outside 205 → 128 of 391; 61 of those are GitHub web merges, the rest have no transcript activity within hours; only 3 commits are genuinely tied between two active sessions.

**Facts about Daniel's data that shaped this:** sessions run in the main repo folder and work in sibling worktrees (`wt-*`, `C:\data\wt\…`), so `gitBranch` in transcripts is useless; commits are made with `git commit -q`, so transcripts hold no hash; GitHub squash-merges leave `docs/handoff-*` branches as short open lanes; De Beers Explorer = 52 sessions, 391 commits, 18 worktrees, 9 lanes at its widest; a full rebuild takes ~1 s once transcripts are cached.

**Files:** `Services/Timeline/TimelineBuilder.cs` (model records, `TimelineBuilder`, `BranchGraph`), `Services/Timeline/TimelineSource.cs` (`ResolveAsync`, `ReadCommitsAsync`/`ParseLog`, `ReadBranchTipsAsync`/`ParseTips`, `ReadSessions`, `LastEntryTime`, `TranscriptScan`, `TranscriptHashes` facade), `Controls/TimelineGutter.cs`, `Controls/DarkTitleBar.cs` (DWM dark title bar, caption and frame for every window; replaced MainWindow's private copy), `TimelineWindow.xaml(.cs)`, `TerminalPaneControl` 🌳 button, `MainWindow.ShowTimeline`. Tests: `TimelineBuilderTests`, `TimelineSourceTests`, `TimelineGraphInvariantTests`.

**Phase 2 (not started; needs Daniel's answers):** after each finished reply, summarise the new turn with `claude -p` (the same lock-down as Review: `--tools ""`, `--setting-sources ""`, `--strict-mcp-config`, `--no-session-persistence`) and add "what was built" lines to the session card. Cache under `C:\data\agenthub\timeline\`. One-off backfill for past sessions from condensed transcripts (prompts, final replies, commits). Questions for Daniel:
1. Summaries model: Haiku or default?
2. Backfill: all sessions or from a date?
3. Also a global Timeline button with a repository picker?

## 3a. Timeline review: what was built and why (on `feature/timeline-review`)

Daniel's ask (2026-11-04): on the project story tree, a review that "looks at only the necessary information and tells the user what should be done differently based on good IGO practices", suggests additions to the main `CLAUDE.md` so Claude handles GitHub and building projects better, "advice for both Claude and the user", "maybe even score it", in two parts: the user and Claude interaction, and the GitHub cycle. Plan approved with the assumptions: default model, Part B scored in C#, additions target the user-wide `CLAUDE.md` only, `gh` for PR facts, 4 h long-session threshold, full prompts for the newest 25 sessions.

**Flow** (`Services/Timeline/TimelineReview.cs`): `GatherAsync` (branch tips, `git log --name-only` over `CLAUDE.md` / `.agenthub/handoff.md` / `docs` → `ReadTouchingAsync`; `gh pr list --state all --limit 100 --json …` → `ReadPullRequestsAsync`, null on any failure; `TranscriptTexts.Read` for the newest 25 sessions, a shared cache with the cards' prompt loading; the user-wide `CLAUDE.md`; repo file facts) → `Compute` (the checks) → `Build` (the digest) → `ClaudePrint.RunAsync` (the `claude -p` lock-down shared with the file Review, in `Services/Instructions/ClaudePrint.cs`) → `Parse` → `Save` to `C:\data\agenthub\timeline\<repo>\review-<stamp>.md` + `digest-<stamp>.txt`.

**Checks** (deterministic; Part A are facts for the model, Part B are scored as the mean compliance over applicable checks × 10): A1 sessions over 4 h; A2 committing sessions that never touched the context files; A3 sessions with no commits; A4 sessions overlapping in one folder. B1 branch names not `^(feat|fix|hotfix|chore|refactor|docs)/…` (IGO: `feature/` fails, which is every branch in this repo); B2 trunk non-merge commits with no `(#N)` subject and not a PR's `mergeCommit` (fast-forward merges count as direct, by the IGO rule); B3 merge commits vs squashes; B4 merged branch names still among the tips; B5–B8 from `gh` only (self-merge, zero reviews, base not trunk, title > 70). Squash-merged deleted branches are invisible to `git log --branches --remotes`, which is why B2–B4 lean on subjects and `gh`.

**Digest** (`<project>`, `<facts>`, `<branches>`, `<pull-requests>`, `<outside-sessions>`, `<sessions>` with ids S1 = newest, `<rules>`, `<user-claude-md>`): 60 KB budget; every session gets at least its one-line form (reserved first), the newest get prompts (≤ 40, 160 chars each) and the last reply (400 chars) while the full form plus all older one-liners still fit. The **IGO rules are data in the request, not the system prompt** (`RulesFor(hasPipeline)`): GitHub-cycle rules always, pipeline-stage rules only when `docs/pipeline-status.md` exists. They were transcribed from the dev kit cache on 2026-11-04; refresh by hand when the kit changes.

**Window** (`TimelineWindow`): two buttons beside Refresh, **✨ You & Claude** and **✨ GitHub cycle** (`ReviewKind.Interaction` / `ReviewKind.GitHub`; Daniel asked for "2 buttons and 2 different panels" at 13:05). One review runs at a time (the other button is disabled; the running one becomes ■ Cancel). A `ReviewSlot` per kind holds its button, panel, body, meta line, Add-rules button, cancellation and last result; the panel buttons carry the kind in `Tag` ("sessions" / "github"). The status line shows the stage ("Reading transcript 3 of 25…", "Asking Claude Code…"), the elapsed time and, once the digest exists, "N s of about 2 min for a 19 KB digest (estimate from 2 earlier reviews on this machine)" (`ReviewTimings`: 40 s overhead + median seconds-per-KB of the last 20 runs in `C:\data\agenthub\timeline\timings.json`, default 3.5 s/KB). The two reports stack in a 460 px column to the right of the tree (`ReportsColumn`, collapsed until a report is open, not an overlay), each with a card (score 22 px, colour by band, source line under it: "Claude's judgement" / "from N checks"), findings (what, evidence in mono with `S12` ids as links that scroll to and flash the session card, "Instead:", who badge), the additions, and **Add to CLAUDE.md…** → `SuggestionsRequested` → `MainWindow` opens the global Context window and calls `ContextWindow.ShowSuggestions(ReviewSuggestion)`, which reuses `ShowReview` with Apply boxes. For that, `InstructionReview.Plan` treats an empty `start` on a non-empty file as an append before the trailing empty line (stable `OrderBy` so several appends keep their order). The digests differ: the sessions one has prompts and last replies and no branch or PR data; the GitHub one has branches, pull requests and one-line sessions with their commits, and no prompts. `RulesFor(kind, hasPipeline)`, `PromptFor(kind)`, `SchemaFor(kind)` (score required only for the sessions review), `Parse(root, facts, kind)`; files are `review-sessions-*` / `review-github-*`.

**Verified:** 178 tests; the live test on a synthetic project and a real run of the (then single) review on this repo (`TimelineDiag.cs`, deleted again): gather 3 s, digest 22 KB, Claude 118 s, You and Claude 5/10, GitHub 3/10, seven findings and five additions that read as sensible (report at `C:\data\agenthub\timeline\diag-report.md`). Daniel ran **✨ You & Claude** in the app at 13:08 from the published build (19 KB digest, estimate "about 2 min" from 2 earlier reviews); its result and the Add-to-CLAUDE.md hand-off were not seen by Claude. The GitHub review has not been run in the app. The published build was live-swapped twice while instances ran (9440, 9400), so `*.old-*` files sit in the publish folder until those close.

**Known limits:** Codex transcripts have replies too but their last reply is whatever the reader returns; the rules digest is hard-coded; no model setting; additions go only to the user-wide file; the B2 rule counts fast-forwarded feature commits as direct-to-main (true to IGO, harsh on Daniel's habit).

## 4. Timeline follow-ups Daniel has heard about (none asked for yet)

- A session open for days has one card at its start time holding everything it ever committed; splitting such a session into one row per day of activity would put late merges where they happened.
- Colour lanes by branch rather than by column, and write the branch name at each fork; a trunk node where a merge lands.
- Pull in the sessions of an imported repository by matching a folder whose Git root commit is the imported root (the Search API's sessions are missing from De Beers).
- A dashed tail on open (unmerged) lane ends so they read as unmerged at a glance.
- Hover on a card could light its lane (the reverse of the current lane → cards).

## 5. Known loose ends

- A second AgentHub instance sometimes exits silently within ~10 s of starting (no `crash.log`). Not investigated.
- The Start Menu shortcut points at an old copy under `%LOCALAPPDATA%\AgentHub`; the taskbar pin uses the repo's `publish` folder.
- `*.old-*` files in the `publish` folder are left by live swaps; delete them once the main instance has been restarted.
- CI warns that `actions/checkout@v4` and `actions/setup-dotnet@v4` target Node 20; harmless for now.

## 6. Architecture and coding constraints (still true)

1. **WPF and WinForms namespaces:** `AgentHub.csproj` enables both (for `FolderBrowserDialog`). Files using UI types alias them: `using Button = System.Windows.Controls.Button;`, `using Color = System.Windows.Media.Color;`, `using MouseEventArgs = System.Windows.Input.MouseEventArgs;`, `using Brushes = System.Windows.Media.Brushes;` and so on; collection expressions need a typed target (`new[] { … }` for `string[]`).
2. **Terminal size:** a ConPTY resize after a CLI starts can wipe its first screen. Pane header modes keep the same height. The launcher hides the terminal (never collapses it). Same-size refits are ignored.
3. **Session events:** `EmbeddedTerminalControl` drops output and exit events from a replaced session (`ReferenceEquals(_session, session)`).
4. **Model calls go through installed CLIs on the user's login**, never an API key (`AGENTS.md`: no stored credentials). Lock them down as Review does.
5. **Git and CLI safety:** pull is fast-forward only. Destructive Git operations only on explicit user action. Usage figures name their source.
6. **Records as keys:** `TimelineCommit` and any record with a mutable property must not be a dictionary or set key; key by `Hash`.

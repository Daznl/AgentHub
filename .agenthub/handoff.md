# AgentHub Handoff Context

**Updated:** 2026-11-04 (end of the Timeline phase 1 session)
**Branch with the newest work:** `main` = `origin/main` `d53ebf3`. Nothing unmerged, nothing unpushed.
**Next task:** whatever Daniel asks. The queued items, in his order of interest: republish the taskbar build (section 1), Timeline phase 2 once he answers the three questions (section 3), the smaller Timeline follow-ups (section 4).

Read `README.md`, `docs/ARCHITECTURE.md` and `docs/ROADMAP.md` first; `AGENTS.md` has the engineering rules.

---

## 1. Where everything is

| What | State |
|---|---|
| `main` = `origin/main` `d53ebf3` | Pushed 2026-11-04 as a fast-forward of `feature/timeline` (which contained `feature/context-review`). CI (`build.yml`) green on all three branches. Holds: ✨ Review of context files, 🌳 Timeline phase 1, dark window chrome everywhere, plus the 2026-11-03 work (session picker PR #6, 📄 Context / 📁 Repo context editors, two-row pane header, three-step launcher, replaced-session event fix). |
| `feature/context-review`, `feature/timeline` | Pushed, merged, finished. Safe to delete on GitHub when Daniel wants. |
| ✨ Review | Daniel tested the first version (timed out on the 91 KB De Beers `CLAUDE.md`); the fixed version passed live tests (91 KB in ~170 s) but **Daniel has not retried it in the app**. |
| 🌳 Timeline | **Approved by Daniel 2026-11-04** ("looks good, leave it as it is") after four rounds of corrections, all in `main` (section 3). |
| Taskbar build | `src\AgentHub\bin\Release\net8.0-windows\win-x64\publish` (inside the repo), updated with `C:\data\agenthub\publish-local.ps1 -Repo <repo>`. **Still at `1159f31`: no Review, no Timeline, white title bars.** Republish only when Daniel asks; his main instance (pid 32744 at the time of writing) runs from it with live sessions. |
| Test builds | `C:\data\agenthub\build-test1` … `build-test15`, `build-check`: throwaway. `build-test15` = `main` minus the two handoff commits. Delete the folders once no instance runs from them. |

Pushing: the repo is `Daznl/AgentHub` (Daniel's personal account). `gh auth switch -u Daznl`, push, then `gh auth switch -u DanielWallisBarker` straight after. Push or merge only when Daniel asks. Fast-forward is fine when the branch was built on `main`; check `origin/main` first (`git fetch`, `git log main..origin/main`).

## 2. Working rules (learned the hard way; still apply)

- **Never automate Daniel's desktop** (no UI Automation clicks, no mouse). Screenshots with `C:\data\agenthub\screens\shot.ps1 -ProcId <pid> -Out <png> -TitleLike <part of window title>` (PrintWindow, read-only) are fine and were used all day. Anything needing clicks or hover: launch a test build and ask Daniel.
- **Never kill Daniel's running AgentHub instance** (the one under `publish`); it holds live Claude Code sessions. Build to a separate folder: `dotnet build src/AgentHub/AgentHub.csproj -c Release -o C:\data\agenthub\build-testN`. A folder is locked while an instance runs from it, so bump N. Test copies Claude started may be stopped by Claude.
- Launch a test copy with `Start-Process C:\data\agenthub\build-testN\AgentHub.exe -ArgumentList '--timeline', '"<folder>"'` and give Daniel the process id. He will pop it open on his screen, so what the screenshot shows may already have been scrolled or clicked by him.
- Tests: `dotnet test tests/AgentHub.Tests/AgentHub.Tests.csproj -c Release`. 160 pass, 3 live tests skipped. Real-repository checks run only with `AGENTHUB_TIMELINE_REPO=<repo>` (and `AGENTHUB_TIMELINE_ARCHIVE=C:\data\agenthub\history`); the live Review test only with `AGENTHUB_LIVE_REVIEW=1` (spends Claude usage). Temporary diagnostic tests (`TimelineDiag.cs`) were the quickest way to question real data; delete them before committing.
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

# AgentHub Handoff Context

**Updated:** 2026-11-04
**Branch with the newest work:** `feature/timeline` (local only; built on `feature/context-review`, which is built on `main`)
**Next task:** Daniel tests Timeline phase 1 in the app (section 3); then his answers decide phase 2

Read `README.md`, `docs/ARCHITECTURE.md` and `docs/ROADMAP.md` first; `AGENTS.md` has the engineering rules.

---

## 1. Where everything is

| What | State |
|---|---|
| `main` = `origin/main` `1159f31` | Pushed 2026-11-03. Session picker (PR #6, now merged), 📄 Context / 📁 Repo context editors, two-row pane header (setup vs working mode, Relaunch), three-step pane launcher (① Repository → ② Function type → ③ Session; SSH listed under Function type only when 🏠 Home is chosen), fix for events from replaced sessions (Relaunch/Restart/Stop status). Daniel tested all of it. |
| `feature/context-review` `462a03f` | Local, **not pushed**. ✨ Review in the context window (`InstructionReview`, `LineDiff`). Daniel tested the first version (it timed out on the 91 KB De Beers `CLAUDE.md`); the fixed version passed live tests (91 KB in ~170 s, all edits placed) but **Daniel has not retried it in the app yet**. |
| `feature/timeline` | Local, **not pushed**. 🌳 Timeline phase 1 complete and wired in (section 3): builder, source, gutter, window, pane button, 28 tests, README and roadmap. Daniel saw the first version ("looks good") and asked for three verticals (tree, sessions, commits at their time), then for the tree to be the Git branch graph (an unmerged branch was on the trunk); both done and screenshot-checked on De Beers Explorer. Test build: `C:\data\agenthub\build-test14` (`AgentHub.exe --timeline <folder>` opens the window at once). |
| Taskbar build | `src\AgentHub\bin\Release\net8.0-windows\win-x64\publish` (inside the repo), updated with `C:\data\agenthub\publish-local.ps1 -Repo <repo>`. At `1159f31`: **no Review, no Timeline**. Republish only when Daniel asks. |
| Test builds | `C:\data\agenthub\build-test*`, `build-check`: throwaway, safe to delete once no test instance runs from them. |

Pushing: the repo is `Daznl/AgentHub` (Daniel's personal account). `gh auth switch -u Daznl`, push, then `gh auth switch -u DanielWallisBarker` straight after. Push or merge only when Daniel asks; he decides when a branch goes to `main`.

## 2. Working rules learned this session

- **Never automate Daniel's desktop** (no UI Automation clicks, no mouse). It once clicked into his real terminal and a modal dialog blocked his test window. Screenshots with `C:\data\agenthub\screens\shot.ps1 -ProcId <pid> -Out <png>` (PrintWindow, read-only) are fine. For anything needing clicks, launch a test build and ask Daniel to try it.
- **Never kill running AgentHub instances** to free the build output; his main instance holds live Claude Code sessions. Build to a separate folder instead: `dotnet build src/AgentHub/AgentHub.csproj -c Release -o C:\data\agenthub\build-testN`, and run tests in Release.
- Launch a test copy with `Start-Process C:\data\agenthub\build-testN\AgentHub.exe` and give Daniel the process id.
- Live review test: set `AGENTHUB_LIVE_REVIEW=1` to run it (it spends Claude usage); without it the test is skipped.
- Python is not installed; use PowerShell or C#. PowerShell 5.1 strips quotes from JSON passed to native programs: pass JSON arguments from Bash.
- Data and scratch output go under `C:\data\agenthub\`, never AppData or Temp.

## 3. Timeline: phase 1 built, awaiting Daniel's test

Daniel's ask: a story of the project from session 1 to now, matching the GitHub commits, shown as a tree from a starting point, every session collapsible, parallel sessions visible side by side. Eventually an agent summarises each new reply as sessions run.

**Phase 1 (done on `feature/timeline`, no model calls):**
- Scope = one Git repository including all worktrees (`git worktree list`); outside Git, the one folder.
- Rows top to bottom in time: Start (first commit, or first session), each session, each same-day run of commits made outside sessions on one branch. A first commit made outside any session *is* the Start row; one made inside a session stays with that session.
- **The tree is Git's branch graph** (Daniel's second correction, 2026-11-04: he read the trunk as `main`, and an unmerged branch was sitting on it). `BranchGraph.Assign`: trunk = first-parent chain of `main` / `master` from local *and* remote tips (local `main` in De Beers is 86 commits behind `origin/main`); each merge commit on the trunk recovers the branch it merged (commits reachable from its other parents that nothing claimed yet; named after a tip in that set, else from the subject "Merge pull request #N from owner/branch" / "Merge branch 'x'"); remaining tips claim what they reach (open branches). Dangling commits go to the trunk. Stashes and tags are not read (`git log --branches --remotes`).
- Lanes: a branch spans from its first row to its last; merged → `MergesBack` always, and the lane ends at the lower of its last own row and the row above the merge commit (the merge row itself when that row is on the lane). Rows sit at session *start* time, so a long session that merges days later holds the merge in a card far above the branch; the lane still curves in at its end (Daniel reported lanes "ending in the air": that was this). Columns by interval scheduling, lowest free column. A session sits on the lane most of its commits are on, feature over trunk (Daniel's choice); no commits → trunk, hollow node (`TimelineRow.IsHollow`). Lane spacing shrinks past 6 columns (`TimelineGutter.LaneWidth(columns)`).
- Commit attribution (Daniel's third question, 2026-11-04: a branch "started outside sessions"): candidates are sessions whose window covers the commit (2 min before start to 5 min after the last entry) **and** whose transcript wrote an entry within 4 min of it (`TranscriptScan` collects entry minutes; a session Daniel keeps open for 12 days was claiming every commit as a tie). Ties: hash mention, then the commit subject found in the transcript entries 15 min before to 2 min after the commit (`TranscriptScan.MentionsBetween`, reads only that byte range via per-minute offsets). No active session → outside (by hand or GitHub web). De Beers: outside 205 → 128 of 391; of those 61 are GitHub web merges and the rest have no transcript activity within hours.

**Files:**
- `Services/Timeline/TimelineBuilder.cs`: `TimelineCommit` (Branch set by the graph), `TimelineBranchTip`, `TimelineBranch` (lane), `TimelineBuilder.Attribute` / `Build` / `Layout`, `BranchGraph.Assign` / `MergedBranchName`. `TimelineSessionInput` carries `Folder` (for Open in History).
- `Services/Timeline/TimelineSource.cs`: `ResolveAsync`, `ReadCommitsAsync` + `ParseLog` (5 fields), `ReadBranchTipsAsync` + `ParseTips` (local and remote heads, `origin/HEAD` dropped), `ReadSessions`, `LastEntryTime`, `TranscriptHashes`.
- `Controls/TimelineGutter.cs`: draws one row's trunk, lanes, fork and merge S-curves and node (hollow for sessions without commits, ring when live). Trunk `#2A3A52`, lanes by column. Lane highlighting: `LaneHovered` / `LaneClicked` events carry the branch key (`TimelineLine.Key`); `HighlightKey` draws that lane bold and the rest faint. `TimelineWindow.ApplyHighlight` frames the branch's cards (`_cards` by `TimelineRow.LaneKey`) and fades the rest; hover lights, click pins (`_pinnedKey`), a click on empty space or Esc clears.
- `Controls/DarkTitleBar.cs`: dark title bar, caption and frame colour (#0C121C) for every window via DWM; replaced MainWindow's private copy. Daniel asked for the popup's white chrome to go.
- `TimelineWindow.xaml(.cs)`: three verticals per row (gutter, session card or Start, commits card level with its session; outside commits on the right alone). `CommitsCard` shows the first 3 commits, chevron for the rest; "not merged" on open branches; branch after the subject only when a card spans several. Session expands to prompts (lazy, cached by file length) and 🕘 Open in History. Rebuild every 20 s only when the model's signature changed.
- `TerminalPaneControl` 🌳 button → `MainWindow.ShowTimeline(folder)`, one window per root; `AgentHub.exe --timeline <folder>`.
- Tests: `TimelineBuilderTests` (attribution, graph, rows, lanes, keys) and `TimelineSourceTests` (log, tips, last entry time, hash scan); suite 144 pass, 3 live skipped.

**Seen on real data:**
- De Beers Explorer: 52 sessions, 391 commits, 18 worktrees (stale `wt-*` entries included). Many `docs/handoff-*` branches were squash-merged on GitHub, so they show as short open lanes marked "not merged"; that is what git has.
- The De Beers window has about 9 columns at its widest point; the adaptive spacing keeps the gutter under ~150 px.

**Things Daniel should try:** open 🌳 from a pane on this repo and on De Beers Explorer; expand a live session (prompts load, LIVE badge, ring); Open in History lands on the right session; leave it open while a session commits and see the commit move onto the session card within ~20 s; a pane whose folder is a worktree opens the same window as the main folder.

**Phase 2 (not started, needs Daniel's answers):** after each finished reply, summarise the new turn with `claude -p` (the same locked-down setup as Review: `--tools ""`, `--setting-sources ""`, `--strict-mcp-config`, `--no-session-persistence`) and add "what was built" lines to the session card. Cache under `C:\data\agenthub\timeline\`. One-off backfill for past sessions from condensed transcripts (prompts, final replies, commits). Open questions:
1. Summaries model: Haiku or default?
2. Backfill: all sessions or from a date?
3. Also a global Timeline button with a repository picker?
## 4. Known loose ends

- A second AgentHub instance sometimes exits silently within ~10 s of starting (no `crash.log`). Not investigated.
- The Start Menu shortcut points at an old copy under `%LOCALAPPDATA%\AgentHub`; the taskbar pin uses the repo's `publish` folder.
- `*.old-*` files in the `publish` folder are left by live swaps; delete them once the main instance has been restarted.

## 5. Architecture and coding constraints (still true)

1. **WPF and WinForms namespaces:** `AgentHub.csproj` enables both (for `FolderBrowserDialog`). Files using UI types alias them: `using Button = System.Windows.Controls.Button;`, `using Color = System.Windows.Media.Color;`, `using MessageBox = System.Windows.MessageBox;`, `using UserControl = System.Windows.Controls.UserControl;` and so on.
2. **Terminal size:** a ConPTY resize after a CLI starts can wipe its first screen. Pane header modes keep the same height. The launcher hides the terminal (never collapses it). Same-size refits are ignored.
3. **Session events:** `EmbeddedTerminalControl` drops output and exit events from a replaced session (`ReferenceEquals(_session, session)`).
4. **Model calls go through installed CLIs on the user's login**, never an API key (`AGENTS.md`: no stored credentials). Lock them down as Review does.
5. **Git and CLI safety:** pull is fast-forward only. Destructive Git operations only on explicit user action. Usage figures name their source.

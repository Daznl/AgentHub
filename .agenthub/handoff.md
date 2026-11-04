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
| `feature/timeline` | Local, **not pushed**. 🌳 Timeline phase 1 complete and wired in (section 3): builder, source, gutter, window, pane button, 28 tests, README and roadmap. Daniel saw the first version ("looks good") and asked for three verticals (tree, sessions, commits at their time); done and screenshot-checked on De Beers Explorer. Test build: `C:\data\agenthub\build-test10` (`AgentHub.exe --timeline <folder>` opens the window at once). |
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
- Rows top to bottom in time: Start (first commit, or first session), each session, each same-day run of commits made outside sessions. A first commit made outside any session *is* the Start row (not a commits row as well); one made inside a session stays with that session.
- Column 0 is the trunk. A session running alone sits on the trunk; one that overlaps another gets a lane that forks off when it starts and merges back when it ends; a live session's lane (written to in the last 3 min) stays open.
- Commit attribution: a commit belongs to the session running at its commit time (2 min before start to 5 min after the last entry); with several running, to the one whose transcript mentions its 7-character hash (latest started wins a tie); otherwise it stays on the trunk.

**Files:**
- `Services/Timeline/TimelineBuilder.cs`: `Attribute`, `Build`, lane layout into `TimelineRow`s with `TimelineLine`s. `TimelineSessionInput` now carries `Folder` (needed for Open in History).
- `Services/Timeline/TimelineSource.cs`: `ResolveAsync` (root and worktrees), `ReadCommitsAsync` (`git log --all --source`), `ReadSessions`, `LastEntryTime`, `TranscriptHashes` (incremental hex-word scan).
- `Controls/TimelineGutter.cs`: `FrameworkElement` drawing one row's trunk, lanes, fork and merge S-curves and node in `OnRender`; stretches with the row. Trunk `#2A3A52`, lane and node colours by column (`#2FD4F5 #C084FC #34D399 #FBBF24 #60A5FA #F472B6 #FB923C #A3E635`), live node gets a ring.
- `TimelineWindow.xaml(.cs)`: header (name, "N sessions · M commits · since … · worktrees · root", Refresh) and column headings. **Three verticals per row** (Daniel's ask after seeing v1): gutter, then the session card (or Start), then a commits card on the right, level with the session that made the commits (`CommitsCard`: first 3 commits shown, chevron for the rest; branch inline after the subject only when the card spans several branches; `origin/HEAD` is not a branch). Cards built in code (`NewCard` / `MakeExpandable`); a session expands to its prompts (read lazily with `SessionHistoryReader.ReadEntries`, cached by file length) and 🕘 Open in History → `MainWindow.ShowHistory`. Scrolls to the bottom on open; re-reads every 20 s but only rebuilds when a signature of the model changed (no flicker), keeping expanded keys and scroll position.
- `TerminalPaneControl`: 🌳 button (both header modes) raises `TimelineRequested`; `MainWindow.ShowTimeline(folder)` resolves the scope and keeps one window per root in `_timelineWindows`. `AgentHub.exe --timeline <folder>` opens one at start-up.
- Tests: `TimelineBuilderTests` (attribution, rows, lanes, keys) and `TimelineSourceTests` (`ParseLog`, `LastEntryTime`, `TranscriptHashes`), 28 in all; whole suite 133 pass, 3 live skipped.

**Seen in the screenshots (worth knowing):**
- This repo shows only 3 sessions because most of its sessions ran from other folders; De Beers Explorer shows 52 sessions, 393 commits, 18 worktrees (stale `wt-*` entries included: that is what git reports).
- `git log --source` names whichever ref reached the commit first, so an old `main` commit can be labelled with a feature branch. Acceptable for phase 1; a "prefer main/master" rule would be a small later change.
- Consecutive sessions in the same lane draw as merge-then-fork bumps between rows. Truthful, slightly busy.

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

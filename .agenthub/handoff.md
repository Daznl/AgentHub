# AgentHub Handoff Context

**Updated:** 2026-11-03
**Branch with the newest work:** `feature/timeline` (local only; built on `feature/context-review`, which is built on `main`)
**Next task:** finish the project Timeline, phase 1 (section 3)

Read `README.md`, `docs/ARCHITECTURE.md` and `docs/ROADMAP.md` first; `AGENTS.md` has the engineering rules.

---

## 1. Where everything is

| What | State |
|---|---|
| `main` = `origin/main` `1159f31` | Pushed 2026-11-03. Session picker (PR #6, now merged), 📄 Context / 📁 Repo context editors, two-row pane header (setup vs working mode, Relaunch), three-step pane launcher (① Repository → ② Function type → ③ Session; SSH listed under Function type only when 🏠 Home is chosen), fix for events from replaced sessions (Relaunch/Restart/Stop status). Daniel tested all of it. |
| `feature/context-review` `462a03f` | Local, **not pushed**. ✨ Review in the context window (`InstructionReview`, `LineDiff`). Daniel tested the first version (it timed out on the 91 KB De Beers `CLAUDE.md`); the fixed version passed live tests (91 KB in ~170 s, all edits placed) but **Daniel has not retried it in the app yet**. |
| `feature/timeline` | Local, **not pushed**. Timeline data layer only, not wired into the UI (section 3). |
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

## 3. Timeline: design agreed, phase 1 in progress

Daniel's ask: a story of the project from session 1 to now, matching the GitHub commits, shown as a tree from a starting point, every session collapsible, parallel sessions visible side by side. A **🌳 Timeline** button in the pane header opens it for that pane's repository. Eventually an agent summarises each new reply as sessions run.

**Phase 1 (agreed, in progress): the tree from data only, no model calls.**
- Scope = one Git repository including all worktrees (`git worktree list`); outside Git, the one folder. `igo-debeers-explorer` and `debeers-prospector` are separate timelines.
- Rows top to bottom in time: Start (first commit / first session), each session, each same-day run of commits made outside sessions.
- Column 0 is the trunk. A session running alone sits on the trunk; one that overlaps another gets a lane that forks off when it starts and merges back when it ends; a live session's lane stays open.
- Commit attribution: a commit belongs to the session running at its commit time (2 min before start to 5 min after the last entry); with several running, to the one whose transcript mentions its 7-character hash; otherwise it stays on the trunk.

**Done (compiles, no tests yet):**
- `src/AgentHub/Services/Timeline/TimelineBuilder.cs`: `Attribute`, `Build`, and lane layout into `TimelineRow`s with `TimelineLine`s (column, from top, to bottom, fork, merge).
- `src/AgentHub/Services/Timeline/TimelineSource.cs`:
  - `ResolveAsync` (repository root and worktrees).
  - `ReadCommitsAsync` (`git log --all --source`).
  - `ReadSessions`: sessions across all worktrees, live and archived, de-duplicated.
  - `LastEntryTime`.
  - `TranscriptHashes`: an incremental scan for hex words.

**Next, in order:**
1. Tests for `TimelineBuilder` (attribution with and without hash mentions; overlap gives a lane; lane merges back; live lane stays open; same-day outside commits group; Start row) and for `TranscriptHashes` / `ParseLog`.
2. `TimelineGutter : FrameworkElement` that draws one row's lanes in `OnRender` from `TimelineRow` (stretches with the row as cards expand). Lane colours: column 0 trunk neutral `#2A3A52`, nodes by column (`#2FD4F5 #C084FC #34D399 #FBBF24 #60A5FA #F472B6 #FB923C #A3E635`).
3. `TimelineWindow` (non-modal, one per repository, like `ContextWindow`):
   - **Header:** repository name; "N sessions · M commits · since <date>"; Refresh.
   - **Rows:** gutter beside a card.
   - **Session card:** title, times and duration, agent, live badge, branch chips from its commits, commit count.
   - **Expanded session:** prompts, loaded lazily with `SessionHistoryReader.ReadEntries`; the commits; and "Open in History", which calls `MainWindow.ShowHistory(folder, transcript)`.
   - **Outside-commits card:** "N commits outside sessions", expanding to the list.
   - **Behaviour:** scroll to the bottom (now) on open; rebuild every ~20 s while open, keeping expanded rows by `TimelineRow.Key`; build off the UI thread.
4. Pane header button **🌳 Timeline** (both header modes) and MainWindow wiring.
5. README and roadmap; build; launch a test copy for Daniel.

**Facts found while designing (they shaped the rules above):**
- Daniel's sessions run in the main repository folder and work in sibling worktrees (`wt-*`, `C:\data\wt\...`); no Claude Code session has a worktree as its folder. So the `gitBranch` Claude Code records shows the main folder's branch, not what the session built. Commits are the evidence.
- Commits are made with `git commit -q`, so transcripts have no `[branch hash]` lines; hashes appear through `git log` output and PR text. A mention alone does not prove authorship (sessions quote old hashes), hence time window first.
- An archived transcript's file time is when it was copied; use `TimelineSource.LastEntryTime` for when a session really ended.
- Scale: De Beers Explorer has 52 sessions (277 MB of transcripts) and 393 commits; the archive holds 585 transcripts.

**Phase 2 (not started, needs Daniel's answers):** after each finished reply, summarise the new turn with `claude -p` (the same locked-down setup as Review: `--tools ""`, `--setting-sources ""`, `--strict-mcp-config`, `--no-session-persistence`) and add "what was built" lines to the session node. Cache under `C:\data\agenthub\timeline\`. One-off backfill for past sessions from condensed transcripts (prompts, final replies, commits). Open questions to ask him:
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

# AgentHub Handoff Context

**Updated:** 2026-11-04 15:00 (end of the session host build session; taskbar build republished)
**Branch with the newest work:** `deploy/local-timeline-review-session-host` `6c25d59`, local only: `feature/session-host` (✨ sessions that outlive AgentHub, section 3b, local only, built on `main` `d53ebf3`) merged with `feature/timeline-review` (section 3a, pushed). The merge had doc conflicts only (README, ROADMAP, this file; resolved, this handoff supersedes the timeline-review one). 203 tests pass on it. **The taskbar build runs it** (Daniel asked at 14:55).
**Next task:** Daniel restarts his AgentHub (pid 33952, old code: that restart still ends its sessions; every restart after it does not) and tries the session host (section 3b, "what to try"). Then merge order when he says so: `feature/timeline-review` first (check CI with `gh run list --branch feature/timeline-review`, fast-forward, push), then `feature/session-host` (push it with `gh auth switch -u Daznl`, merge; resolve the doc conflicts exactly as the deploy branch did, or merge the deploy branch itself), CI, push; then the deploy branch can go. Queued after that: Timeline phase 2 (section 3), the follow-ups (section 4).

Read `README.md`, `docs/ARCHITECTURE.md` and `docs/ROADMAP.md` first; `AGENTS.md` has the engineering rules.

---

## 1. Where everything is

| What | State |
|---|---|
| `main` = `origin/main` `d53ebf3` | Pushed 2026-11-04 as a fast-forward of `feature/timeline`. CI (`build.yml`) green. Holds: ✨ Review of context files, 🌳 Timeline phase 1, dark window chrome everywhere, plus the 2026-11-03 work (session picker PR #6, 📄 Context / 📁 Repo context editors, two-row pane header, three-step launcher, replaced-session event fix). |
| `feature/session-host` | **Local only**, two commits on top of `main` (`86c757a`, `d6042b1`). 182 tests pass (3 live usage/review tests skipped; the 4 live host tests run). Not pushed: Daniel has not seen it running yet. |
| `deploy/local-timeline-review-session-host` | **Local only**, `6c25d59` = `feature/session-host` + `feature/timeline-review` (merge commit, doc conflicts resolved). The published taskbar build. Throwaway once both feature branches are in `main`. |
| `feature/timeline-review` | Pushed (`origin/feature/timeline-review`), eleven commits on top of `main` (`e8cf72e` … `4036f7f`). Two timeline reviews, lane colours by branch order, unmerged/unpushed counts, imported-history root fix. Not merged: wait for Daniel. |
| `feature/context-review`, `feature/timeline` | Pushed, merged, finished. Safe to delete on GitHub when Daniel wants. |
| ✨ Review | Daniel tested the first version (timed out on the 91 KB De Beers `CLAUDE.md`); the fixed version passed live tests (91 KB in ~170 s) but **Daniel has not retried it in the app**. |
| 🌳 Timeline | Approved by Daniel 2026-11-04 ("looks good, leave it as it is"). |
| Taskbar build | `src\AgentHub\bin\Release\net8.0-windows\win-x64\publish` (inside the repo), updated with `C:\data\agenthub\publish-local.ps1 -Repo <repo>`. **Republished 2026-11-04 14:58 from `deploy/local-timeline-review-session-host` `6c25d59`** (version `1.0.0+6c25d59…`, 11 files updated, nothing locked, host files present). Daniel's running instance (pid 33952, started 13:34) is still the old code until he restarts it; that one restart ends its sessions, later ones do not. The script needed no change for the host (`dotnet publish` carries `AgentHub.SessionHost.*` and `AgentHub.Hosting.dll` along). Two `*.old-*` leftovers from earlier swaps remain. Republish only when Daniel asks. |
| Test builds | `C:\data\agenthub\build-test1` … `build-test20`, `build-check`, `publish-check`: throwaway. `build-test20` = the session host branch; no instance runs from it (Claude's test instances were closed). Delete the folders once no instance runs from them. |
| Session host data | `C:\data\agenthub\sessions\<id>\` (`session.json`, `replay.bin`, `host.log`) and `C:\data\agenthub\host\<hash>\` (the versioned host copy; `d80e70084c18` is the current build). Empty of sessions now. `C:\data\agenthub\test-settings\` holds the settings folder Claude's test instances used (`AGENTHUB_SETTINGS_DIR`), with the `run*.ps1` launch scripts. |

Pushing: the repo is `Daznl/AgentHub` (Daniel's personal account). `gh auth switch -u Daznl`, push, then `gh auth switch -u DanielWallisBarker` straight after. Push or merge only when Daniel asks. Fast-forward is fine when the branch was built on `main`; check `origin/main` first (`git fetch`, `git log main..origin/main`).

## 2. Working rules (learned the hard way; still apply)

- **Never automate Daniel's desktop** (no UI Automation clicks, no mouse). Screenshots with `C:\data\agenthub\screens\shot.ps1 -ProcId <pid> -Out <png> -TitleLike <part of window title>` (PrintWindow, read-only) are fine. A WebView2 terminal paints at the window's top-left corner in such a screenshot (PrintWindow artefact), not in its pane. Anything needing clicks or hover: launch a test build and ask Daniel.
- **Never kill Daniel's running AgentHub instance** (the one under `publish`); it holds live Claude Code sessions. Build to a separate folder: `dotnet build src/AgentHub/AgentHub.csproj -c Release -o C:\data\agenthub\build-testN`. A folder is locked while an instance runs from it, so bump N. Test copies Claude started may be stopped by Claude.
- **Test instances get their own settings:** start them with `AGENTHUB_SETTINGS_DIR=C:\data\agenthub\test-settings` (see `run2.ps1` there), or they share and overwrite `%APPDATA%\AgentHub\settings.json`, pane layout included, with Daniel's instance. Launch with `Start-Process` from a `.ps1` file and give Daniel the process id. Close them with `CloseMainWindow()` so hosted sessions detach; `Stop-Process` leaves hosts running (harmless: they show up as orphans next start, but tidy up).
- Tests: `dotnet test tests/AgentHub.Tests/AgentHub.Tests.csproj -c Release`. Real-repository checks run only with `AGENTHUB_TIMELINE_REPO=<repo>` (and `AGENTHUB_TIMELINE_ARCHIVE=C:\data\agenthub\history`); the live Review test only with `AGENTHUB_LIVE_REVIEW=1` (spends Claude usage). The live host tests need only `powershell` on PATH and take ~10 s. Temporary diagnostic tests (`TimelineDiag.cs`) were the quickest way to question real data; delete them before committing.
- Python is not installed; use PowerShell or C#. PowerShell 5.1 strips quotes from JSON passed to native programs: pass JSON arguments from Bash.
- **Writing files from Bash loses backslashes:** heredocs, `printf` and `perl -pi` all turned `C:\\Users` into `C:\Users` today (three attempts), which breaks JSON with Windows paths. Use the Write/Edit tools for any file holding paths, quotes, pipes or escapes. `sed -i` on `.csproj` files silently did nothing (CRLF); Edit tool again.
- Data and scratch output go under `C:\data\agenthub\`, never AppData or Temp.

## 3. Timeline: what was built and why (phase 1, in `main`)

Daniel's ask: the story of a project from session 1 to now, matching the GitHub commits, as a tree from a starting point, sessions collapsible, parallel work side by side; later an agent summarises each reply as sessions run.

**Window** (`TimelineWindow.xaml(.cs)`): pane header **🌳** → `MainWindow.ShowTimeline(folder)`, one window per repository root (all worktrees), non-modal; `AgentHub.exe --timeline <folder>` opens one at start-up. Header: name, "N sessions · M commits · since … · worktrees · root", Refresh. **Three verticals per row**, top to bottom in time: the tree (`TimelineGutter`), the session card (or Start), and a commits card on the right level with the session that made the commits; outside commits are a right-hand card alone. Commits card: first 3 commits shown, chevron for the rest, "not merged" on open branches, "imported from another repository" on imported lanes. Session card expands to its prompts (lazy, cached by file length) and 🕘 Open in History. Re-reads git and transcripts every 20 s but only redraws when the model's signature changed. **Lane highlighting:** hover a line → that branch's lane bold everywhere, its cards framed in the lane colour, the rest faint, status line with counts and merged state; click pins, click on empty space / another lane / Esc clears.

**The tree is Git's branch graph** (`BranchGraph.Assign` in `TimelineBuilder.cs`): trunk = `main`/`master` first-parent chain from local and remote tips; each merge commit on the trunk recovers the branch it merged; remaining tips claim what they reach (open branches); dangling commits → trunk. **Imported history:** each commit's root set (`RootsOf`, keyed by hash); the project's root is the end of the trunk's first-parent chain unless that root has nothing of its own but itself (GitHub's one-commit "Initial commit"), in which case the root with most commits of its own among those the first trunk merge brought in. Descendants of another root that never meet the project's root are an `IsImported` lane. **Lanes** (`Layout`): a branch spans its first to last row; merged → `MergesBack`; columns by interval scheduling; **colours by order of appearance** (`TimelineBranch.ColourIndex`, 12-colour palette; on `feature/timeline-review`). `TimelineGraphInvariantTests.AssertConnected` runs in every builder test.

**Commit attribution** (`TimelineBuilder.Attribute`): candidates = sessions whose window covers the commit (2 min before first entry to 5 min after last) and whose transcript wrote an entry within 4 min of it; ties by hash mention, then by the commit subject in the transcript (`TranscriptScan.MentionsBetween`). No active session → outside.

**Facts about Daniel's data:** sessions run in the main repo folder and work in sibling worktrees (`wt-*`, `C:\data\wt\…`), so `gitBranch` in transcripts is useless; commits are made with `git commit -q`, so transcripts hold no hash; GitHub squash-merges leave `docs/handoff-*` branches as short open lanes; De Beers Explorer = 52 sessions, 391 commits, 18 worktrees, 9 lanes at its widest.

**Files:** `Services/Timeline/TimelineBuilder.cs`, `Services/Timeline/TimelineSource.cs`, `Controls/TimelineGutter.cs`, `Controls/DarkTitleBar.cs`, `TimelineWindow.xaml(.cs)`. Tests: `TimelineBuilderTests`, `TimelineSourceTests`, `TimelineGraphInvariantTests`.

**Phase 2 (not started; needs Daniel's answers):** after each finished reply, summarise the new turn with `claude -p` (the same lock-down as Review) and add "what was built" lines to the session card. Cache under `C:\data\agenthub\timeline\`. One-off backfill for past sessions. Questions for Daniel: 1. Summaries model: Haiku or default? 2. Backfill: all sessions or from a date? 3. Also a global Timeline button with a repository picker?

## 3a. Timeline review: what was built and why (on `feature/timeline-review`)

Two buttons beside Refresh in the Timeline window, **✨ You & Claude** and **✨ GitHub cycle** (`ReviewKind.Interaction` / `ReviewKind.GitHub`): deterministic checks over sessions, branches and `gh pr list` (`Services/Timeline/TimelineReview.cs`: `GatherAsync` → `Compute` → `Build` digest → `ClaudePrint.RunAsync` → `Parse` → `Save` to `C:\data\agenthub\timeline\<repo>\review-<kind>-<stamp>.md`), scored reports in a 460 px column right of the tree, and **Add to CLAUDE.md…** which hands the suggested additions to the global Context window as Apply boxes (`ContextWindow.ShowSuggestions`). The IGO rules are data in the request (`RulesFor(kind, hasPipeline)`), transcribed from the dev kit cache on 2026-11-04. Daniel ran **✨ You & Claude** once in the app; Claude did not see the result; the GitHub review has not been run in the app. Known limits: the rules digest is hard-coded; no model setting; additions go only to the user-wide file; the B2 rule counts fast-forwarded feature commits as direct-to-main (true to IGO, harsh on Daniel's habit).

## 3b. Session host: sessions that outlive AgentHub (on `feature/session-host`, built 2026-11-04)

**Daniel's ask (2026-11-04 14:05):** "updating the actual AgentHub app is becoming tedious: I have to wait for my current sessions to hand off, then reload, then open them again." He chose level 2, sessions that outlive AgentHub (level 1, resume each transcript on start, falls out of it).

**Assumptions made without asking (Daniel was away):** one host process per session (not one for all); plain PowerShell and Cmd panes survive too (cheap, same path); the replay buffer is 2 MB (`ReplayBuffer.DefaultCapacity`); records live under `C:\data\agenthub\sessions` (setting `sessionsDirectory`); the feature is on by default (`sessionsOutliveAgentHub`). Say so when he first looks.

**What was built** (full design in `docs/ARCHITECTURE.md`, "Sessions that outlive AgentHub"):
- `src/AgentHub.Hosting/` (class library, no UI): `ConPtySession` + `ConPtyNative` moved here from `Terminal/` (namespace `AgentHub.Hosting`; now also raise raw bytes and accept byte input), `ReplayBuffer` (ring, UTF-8-safe snapshot), `SessionFrame` (the pipe protocol, `ProtocolVersion = 1`), `SessionRecord` / `SessionStore` (`session.json`, `replay.bin`, `host.log`, `IsHostAlive` = pid alive and named `AgentHub.SessionHost`).
- `src/AgentHub.SessionHost/Program.cs`: `AgentHub.SessionHost.exe <id> --root <dir>`; reads the record, starts the ConPTY, serves `\\.\pipe\agenthub-session-<id>` one client at a time (hello/welcome with version check, replay under the output lock, then output; input/resize/kill/detach), exits when the CLI exits (records the exit code, writes `replay.bin`, gives an attached client 5 s to hear `Exited`). Logs to `host.log`.
- `src/AgentHub/Terminal/`: `ITerminalSession` (`Write`, `Resize`, `Kill`, `Detach`, events), `LocalSession` (in-process fallback), `HostedSession` (pipe client; `ReplayText`; `HostLost` when the pipe breaks without `Exited`), `SessionHostService` (installs the host into `C:\data\agenthub\host\<sha of its dll>\`, `StartAsync` writes the record, starts the host and polls the pipe up to 15 s, `AttachAsync` 3 s, `ListLive`, `PruneEnded(keep 20)`).
- `EmbeddedTerminalControl`: `_session` is an `ITerminalSession`; `SpawnSession` (hosted when `Host.IsAvailable`, else local with a subtitle saying so), `AttachSession(record)` → `AttachAsync` (replay, then `Resize` so the CLI redraws), `ShowEnded` (dead host: `replay.bin` + "Exited (code N) while AgentHub was closed" + `ReattachFailed` event), `Detach()` (window close) vs `Dispose()` (pane close: kill), `EndCurrent()` (kill + release, used by Stop, Relaunch, Restart), `NoteTranscript` (load-modify-save of the record's `TranscriptPath`, since the host owns the rest), `StartedAt` (now, or the record's for a reattach; the context tracker is started with it).
- `TerminalPaneControl`: `ConfigureHost`, `AttachSession`, `RestoreLayout`, `OfferResume` (launcher on the folder and CLI, step 3 for Claude Code/Codex with "This pane's last session is first in the list"), `LayoutEntry()`, `Detach()`.
- `MainWindow`: `_sessionHosts` (null when the setting is off), `RestorePanes()` replaces `InitDefaultTerminalPanes` (layout entries → attach / ended / launcher; live hosts not in the layout get panes too; status line "Reattached N session(s)…"), `SaveLayout()` on session start/exit, add, close, move; `OnClosed` saves the layout synchronously (`SettingsService.Save`) and detaches; pane ✕ confirms when a session runs; **■ Stop all** toolbar button (confirms). `SettingsService` now serialises writes (several `_ = SaveAsync` calls used to race) and honours `AGENTHUB_SETTINGS_DIR`.
- `AppSettings`: `SessionsOutliveAgentHub`, `SessionsDirectory`, `PaneLayout` (`Models/PaneLayoutEntry.cs`). README has the table rows.
- Build: `AgentHub.csproj` references both new projects, so the host's exe, dll, runtimeconfig and deps land beside `AgentHub.exe` in build and publish output. Solution updated. No CI change needed.

**Verified:** 182 tests (framing, ring buffer, store; 4 live host tests: attach/type/detach/reattach/replay/exit in a folder with spaces, kill, a command that cannot start, a dead host fails fast). In the app (test instance, own settings folder, screenshots `C:\data\agenthub\screens\host-1.png`, `host-2.png`): a hand-started host with PowerShell was reattached at start with its prompt replayed; a seeded ended record showed "Exited (code 0) while AgentHub was closed" with the launcher reopened on the folder and CLI; closing the window detached (`host.log`: "client detached", host still alive), the layout was saved with the session id, and the next start reattached again (`host.log`: new client pid attached). Daniel has **not** seen any of it, and nothing was tried with Claude Code itself yet.

**What to try (Daniel):** build-test20 or a republish; start Claude Code in a pane; close AgentHub; reopen; the pane should come back mid-conversation with the screen replayed and the context badge within a few seconds; type to confirm the CLI is live. Then close a pane with ✕ (asks), Stop all (asks), Relaunch (asks), and check `C:\data\agenthub\sessions\<id>\host.log` if anything looks off.

**Known limits / follow-ups:** a second AgentHub instance attaching to a session another instance holds just times out after 3 s and shows the launcher ("Session lost: …"); a "busy" message would be better. Old `C:\data\agenthub\host\<hash>\` folders are never pruned. The replay is raw terminal output, so a TUI's screen is rebuilt from its last 2 MB of escape sequences (works for Claude Code's full redraws; a shell shows its scrollback). The host's outgoing channel is unbounded (a client that stops reading could grow it). Antigravity and SSH sessions go through the host like any other command; untested. Settings-off path (`sessionsOutliveAgentHub: false`) compiles and is simple but was not run.

## 4. Timeline follow-ups Daniel has heard about (none asked for yet)

- A session open for days has one card at its start time holding everything it ever committed; splitting such a session into one row per day of activity would put late merges where they happened.
- Write the branch name at each fork; a trunk node where a merge lands.
- Pull in the sessions of an imported repository by matching a folder whose Git root commit is the imported root (the Search API's sessions are missing from De Beers).
- A dashed tail on open (unmerged) lane ends so they read as unmerged at a glance.
- Hover on a card could light its lane (the reverse of the current lane → cards).

## 5. Known loose ends

- A second AgentHub instance sometimes exits silently within ~10 s of starting (no `crash.log`). Not investigated. (Claude's test instances today ran fine for minutes.)
- The Start Menu shortcut points at an old copy under `%LOCALAPPDATA%\AgentHub`; the taskbar pin uses the repo's `publish` folder.
- `*.old-*` files in the `publish` folder are left by live swaps; delete them once the main instance has been restarted.
- CI warns that `actions/checkout@v4` and `actions/setup-dotnet@v4` target Node 20; harmless for now.
- The test instance's window opened at a small default size with cramped panes; Daniel's instance may be maximised by habit. Not looked into.

## 6. Architecture and coding constraints (still true)

1. **WPF and WinForms namespaces:** `AgentHub.csproj` enables both (for `FolderBrowserDialog`). Files using UI types alias them: `using Button = System.Windows.Controls.Button;`, `using Color = System.Windows.Media.Color;` and so on; collection expressions need a typed target.
2. **Terminal size:** a ConPTY resize after a CLI starts can wipe its first screen. Pane header modes keep the same height. The launcher hides the terminal (never collapses it). Same-size refits are ignored. A reattach resizes on purpose, so the CLI redraws for the pane's current size.
3. **Session events:** `EmbeddedTerminalControl` drops output and exit events from a replaced session (`ReferenceEquals(_session, session)`), and a launch or attach that finishes after the pane was replaced or closed lets go of what it started (`_launchGeneration`, `_disposed`).
4. **Model calls go through installed CLIs on the user's login**, never an API key (`AGENTS.md`: no stored credentials). Lock them down as Review does.
5. **Git and CLI safety:** pull is fast-forward only. Destructive Git operations only on explicit user action. Usage figures name their source.
6. **Records as keys:** `TimelineCommit` and any record with a mutable property must not be a dictionary or set key; key by `Hash`.
7. **Session records have two writers:** the host owns pids and exit code, AgentHub owns `TranscriptPath`; each reloads before saving its field. Never save a stale in-memory record over the file.

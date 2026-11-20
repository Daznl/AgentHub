# Handoff: advanced templates

Written 2026-11-14 at the end of the session that built stage 6 (the Template helper), for the next session, on this computer or another (a fresh clone). Stages 1 to 6 are on `main` (stage 6 from `feature/template-helper`); no work is left on a branch.

Read first: `AGENTS.md`, then `docs/ADVANCED_TEMPLATES.md` (what the feature is, how it is used, the file format, how a run works, editing, the build order). This file only adds what that one does not say: where things stand, what was decided, what to do next and what to watch for.

## Side piece, 2026-11-16: past sessions on a fresh install

Separate from advanced templates, on branch `feature/discover-session-folders`. The owner noticed that a new user's earlier Claude sessions did not show. Cause: History and the pane launcher only looked in Home, registered repositories and browsed folders. Claude Code keeps sessions on the computer, not in the Claude account, so nothing can be fetched from an account. Now `SessionFolderDiscovery` lists every folder with sessions (from each transcript's recorded `cwd`). Those folders appear in launcher step ① and in History under "Past sessions on this computer", and History offers ➕ Add to repositories for a Git folder. Tests: `SessionFolderDiscoveryTests`, plus `tests/ui/found-folders.ps1` (passes on this computer against the real transcripts; screenshots in `C:\data\agenthub\uitest`).

Defaults taken without an answer: found folders show by themselves (no scan button); Git folders are offered for adding, never added by themselves; scratch folders under AppData/temp and everything under `C:\data\agenthub` except `worktrees` are left out.

## Side piece, 2026-11-17: pull request cleanup and install guide

- **Pull requests 7 and 9 merged.** Both conflicted with the advanced-template work and were brought up to date with `main`. 7 (turns the worker starts by itself) now sends turn ends, missed replies and idle nudges through `AskAndFollowAsync`. 9 (`new_session`) also carries the local follow-up `8909670`, which starts a fresh session by itself for new work over 150k tokens. 9 is now refused during an advanced-template run, which has its own New session steps. No pull requests are open.
- **Install guide.** The new `CLAUDE.md` imports `AGENTS.md` and tells Claude Code how to install from a clone: prerequisite checks with winget installs, build and test, `scripts\Deploy-Taskbar.ps1 -InstallDir C:\Apps\AgentHub`, and a desktop shortcut to pin. README has an "Install with Claude Code" section. Tested with a fresh install into a folder whose path has a space. The winget package names could not be checked live because the winget source is blocked on the IGO network.
- **Open:** 37 remote branches that are fully merged into `main` are waiting for the owner's go to delete them. 8 unmerged branches (`agenthub-workflows`, `compact-header`, two `deploy/taskbar-*`, `feature/animated-background`, `prompt-only-strip`, `turn-capture`, `turn-suggestions`) and the old worktrees under `C:\data\agenthub\worktrees\AgentHub` are untouched. The taskbar app likely still runs the older combined build of 7, 8 and 9; `Deploy-Taskbar.ps1 -Check` will say so.

## Side piece, 2026-11-18: runs stalled after a new session

Branch `fix/run-new-session-wait`. Tuesday run `20261118-104904-stage-1-additions-slices-a-i` (in `C:\data\agenthub\runs`) closed slice A's session, started slice B's (`b-new` done), then paused at `b-sync`: "No worker session is running in this pane. Start one, then press Go." Cause: `RestartWorkerAsync` waited a fixed 4 s, but a hosted session reports running only once its host process has started and connected, which took longer. Now it waits until the session runs (up to `SessionStartTimeout`, 90 s), then the 4 s for the prompt; a session that never starts pauses at the new-session step (Go retries). The handoff cycle uses the same wait. Tests: `A_new_session_that_takes_a_while_to_start_does_not_stop_the_run` (fails on the old fixed wait: Problem instead of Question), `A_new_session_that_never_starts_pauses_the_run_saying_so`. That Tuesday run is still paused at `b-sync`, with the session open: press Go (or ▶ Resume from ⏯ Runs) to carry on.

## Side piece, 2026-11-18: setup for people cloning the repo

Branch `docs/setup-quickstart`. `bootstrap.ps1` now checks the real prerequisites (Windows 1809+, .NET SDK 8+, the .NET 8 Desktop Runtime, Git, WebView2), prints the `winget` command for each missing one, asks before installing, refreshes `PATH` and checks again. New switches: `-CheckOnly`, `-Yes`, `-NoLaunch`. README has a Quick start at the top and an "Install as a taskbar app" section; `CLAUDE.md` step 1 uses `bootstrap.cmd -CheckOnly`. Tested here: all-present path, a faked missing WebView2, and a full build. The winget install path itself is untested (winget source blocked on the IGO network, and this computer has everything).

## Side piece, 2026-11-18: run tree beside the pane

Branch `feature/run-tree-panel`. Owner asked for the run tree next to the terminal a run drives, small, toggled by a button above the session, blue for the current step, green for done, steps clickable for detail, vertical scroll. Built: **◨ Tree** in the orchestrator box (only when it follows an advanced template) toggles `Controls/RunTreePanel` in a column right of the pane; rows from `RunTreeView` (same rows as the window, no Start/Done caps). `OrchestratorBoxView` carries `Run` and `RunStamp`, so the pane redraws when a step moves. Read-only: Go/Pause stay in the window. "Blue" is the app's accent cyan `#2FD4F5`, the colour the window already uses for **● NOW**, so the two views match; when paused the tree stays blue (the window turns amber).

Tests: `RunTreeViewTests`, `RunTreePanelTests` (renders off screen, checks green and blue pixels; `AGENTHUB_RENDER_DIR` saves the PNG), `tests/ui/run-tree-panel.ps1` (24 checks pass). On-screen screenshots were not taken: the screen was locked; `Grab` and `advanced-template-run.ps1`'s menu shot now skip on a locked screen instead of failing. **To do:** look at it on screen with a real run (emoji icons do not show in the off-screen render).

## Side piece, 2026-11-18: headless AgentHub on Ubuntu (plan)

Branch `docs/linux-headless-plan`. The owner wants AgentHub to run on Ubuntu over SSH for a colleague, with no windows, driven by Claude Code as a tool. Slice 0, the plan, is `docs/linux/PLAN.md`: decisions, the shape (`agenthub daemon` / `agenthub <command>` / `agenthub mcp`), what is Windows-bound, every Windows action with its command and MCP tool, the owner-only actions and how they are kept, how Windows stays unchanged, and slices 1 to 6. Kept in `docs/linux/` so the Windows helper (which packs `docs/*.md`) does not learn of it.

Owner's decisions: same functionality without a user interface; the owner answers the owner's questions; one user per computer; the install steps ask where the data folder goes; the simplest install (Ubuntu's `dotnet-sdk-8.0`, build from the clone); the Windows app must not change. Open: version scheme, the `posix_spawn` pseudo-terminal spike, confirming `dotnet-sdk-8.0` in the Ubuntu archive.

**Slice 1, Core split** (branch `feature/linux-core-split`): `src/AgentHub.Core` (net8.0) holds models, services, orchestrator, `Sessions/` and the pure terminal helpers (attention, bracketed paste, folder trust); the app keeps windows, controls, `AgentLauncher`, `RepoContextService` and `PaneBuildWorker` (now `Terminal/PaneBuildWorker.cs`). `AgentHub.Hosting` is net8.0 with the ConPTY types marked Windows-only. Untangled on the way: repository models give a `BadgeTone` and booleans instead of brushes and `Visibility` (`Controls/BadgeToneBrushConverter`, `BoolToVisibility` in `App.xaml`; `BadgeToneBrushConverterTests` pins every old colour); `ContextWindowTracker` and `HistoryArchiveService` take an `ITickTimer` factory (the app passes `DispatcherTickTimer.Create`, the same `DispatcherTimer`). Tests: `tests/AgentHub.Core.Tests` (net8.0, 373) and `tests/AgentHub.Tests` (Windows, 39: controls, rendering, live host); shared `LiveFactAttribute` and `SampleTemplates` are linked into the Windows tests. Two idle-nudge tests in `PaneOrchestratorTests` raced under a full run (a worker silent before its turn end could be nudged first); their worker now goes silent only when its turn ends. CI: the Windows job also runs the tests; a new `linux` job builds `AgentHub.Linux.slnf` and runs the Core tests on ubuntu-latest. Publish output: the same files plus `AgentHub.Core.dll`/`.pdb`. The first Ubuntu run failed 16 Core tests, fixed on the same branch (pull request 26): program names are read with both kinds of folder separator (`CommandLineText.ProgramName`, used by `DetectSource` and `DetectCli`); `EncodeClaudeProjectName` trims either separator; percentages are written "55%" whatever the culture (`Percent.Of`; Linux CI runs in the invariant culture, which writes "55 %"); tests write Windows paths through `TestPaths.Native` (`C:\Work\x` → `/c/Work/x` on Linux), the Template helper's stand-in CLI is a shell script on Linux, and the creation-time test is `[WindowsFact]` (Linux cannot set a file's creation time). `SynchronizationContextTickTimer.Stop` now waits for a tick under way, so none lands after it (its test was flaky under load).

**Branch rule (owner, 2026-11-20):** all Linux work lives on branch `linux-headless` (worktree `C:\data\agenthub\worktrees\AgentHub\linux-session-host`), pushed to origin so CI runs, and is **never merged or pushed to `main`**; no pull requests into `main` (pull request 26 was closed for this). Test with backend tests only (`dotnet test` on Windows, the `linux` CI job); never run `tests/ui/*.ps1` for this work, they open AgentHub windows on the owner's screen. Plan decision 8 and "Keeping Windows unchanged" say the same.

**Slice 2, session host on Linux** (on `linux-headless`): `IPty` with `ConPtySession` (Windows, unchanged) and `UnixPty` (Linux: `posix_openpt`, `posix_spawn` with `POSIX_SPAWN_SETSID`, then file actions open the terminal's other side as standard input, output and error, so it is the controlling terminal; signals reset to default; `/bin/sh -c <command line>`; a missing program or folder is reported before starting; `ioctl(TIOCSWINSZ)` resize; kill ends the session's process group; the exit is raised after the last output). `Pty.Start` takes the output and exit callbacks before anything is read. `SessionTransport`: the same named pipe on Windows; on Linux a Unix socket `$XDG_RUNTIME_DIR/agenthub/<id>.sock` (or `/tmp/agenthub-<uid>`), folder mode 0700, listening only while waiting for a client, as one pipe instance. `AgentHub.SessionHost` is net8.0 and calls `setsid` on Linux. `HostedSession`, `ITerminalSession` and `SessionHostService` moved from the app into `AgentHub.Hosting` (namespace `AgentHub.Hosting`). `AgentHubData`: the data folder, `C:\data\agenthub` on Windows (every path unchanged), `AGENTHUB_DATA` or `~/agenthub-data` on Linux; every `C:\data\agenthub\…` constant in Core now comes from it. `SessionStore.IsHostAlive` reads `/proc/<pid>/cmdline` on Linux (process names are cut to 15 characters). `SessionHostLiveTests` moved to `AgentHub.Core.Tests` and run PowerShell on Windows, bash on Linux (plus a test that the shell runs in the working folder on a real terminal); `AgentHub.Linux.slnf` includes the session host. Verified: Windows build 0 warnings, 387 + 35 tests pass; Ubuntu CI 385 pass (the 5 host tests included), 8 skipped (live Claude tests, the creation-time test). **Next:** slice 3, the session controller (pane session logic out of the WPF controls into Core).

**Where to pick up (end of session 2026-11-20):**
- Work in `C:\data\agenthub\worktrees\AgentHub\linux-session-host` on branch `linux-headless` (head `86e1c2d` plus this handoff), clean, pushed. Push with the Daznl token per command (see `AGENTS.md`/memory); the token now has the `workflow` scope, needed because the branch changes `.github/workflows/build.yml`. Check CI with `gh run list --repo Daznl/AgentHub --branch linux-headless`.
- No Linux machine on this computer (no WSL, no Docker): the `linux` CI job on ubuntu-latest is the only Linux test bed. Each try is a push and about a minute of CI.
- Slice 3 is the first slice that edits code the Windows app is built from (`TerminalPaneControl`, `EmbeddedTerminalControl`, `PaneBuildWorker`); it still stays on `linux-headless`. Prove it with `PaneOrchestratorTests` against both workers; backend tests only.
- Owner asked how advanced templates look over SSH; answered from the plan (slices 4 and 5): `agenthub orch attach <session> --advanced <template> --project <folder>`, `runs tree`, `orch log --follow`, `attach`, `status`, `questions` / `answer` (terminal only), `runs go|pause|from|list|resume`, `adv …`. **Open, waiting for the owner:** add `agenthub runs watch <run>` to the plan, a live view that redraws the run tree in place with the latest orchestrator message and any waiting question under it (the Windows tree updates itself; `runs tree` prints once). Ask before adding it.
- Left over, untouched: local and remote branch `feature/linux-core-split` (its commits are all on `linux-headless`; pull request 26 closed, not merged) and its worktree `C:\data\agenthub\worktrees\AgentHub\linux-headless-plan`; the scratch test build `C:\data\agenthub\build-linux-core-split`; logs in `C:\data\agenthub\testlogs`. Delete them only with the owner's go.
- Still open from the plan: version scheme; confirm `dotnet-sdk-8.0` in the Ubuntu 22.04 and 24.04 archives before slice 6.

## What the owner asked for

An orchestrator that follows a planned sequence instead of improvising one: a tree of stages and steps (instruct the worker, wait for it, ask the owner, new session, close session, handoff, create branch, commit, merge, move work to a branch, open a pull request), shown like the Timeline tree but forward-looking; ticked off live; pausable at any time and resumable later; editable, also for later steps while a run is under way; and a helper crab that builds the sequence from a written plan.

Called **advanced templates**. The earlier, looser orchestration templates (📚 Templates, `workflows/*.md`, `OrchestratorWorkflow`) stay as they are, for the less formal way of working.

## Where it stands

| Stage | What | State |
| --- | --- | --- |
| 1 | File format, checks, project store (`AdvancedTemplate`, `AdvancedTemplateStore`) | merged, pull request 10 |
| 2 | Window that draws a project's templates as trees (`AdvancedTemplatesWindow`, `AdvancedTemplateTree`) | merged, 11 |
| 3 | Run engine (`TemplateRun`, `PaneOrchestrator.Run.cs`) and its controls: Follow, Go, Pause, from here, live tree | merged, 12 and 13 |
| 4 | Runs kept on disk and resumed (`RunStore`, `RunRecorder`, `RunsWindow`) | merged, 14 |
| 5 | Editing through drafts (`TemplateEditing`, `AdvancedTemplatesWindow.Editing.cs`); a saved change applied to a run that stands still (`TemplateRun.ProblemsWith` / `Rebase`, `PaneOrchestrator.ApplyTemplate`) | merged, 16 |
| 6 | Template helper: the crab of the window builds a template from a plan and changes the one on screen, into a draft (`TemplateHelper`, `AdvancedTemplatesWindow.Helper.cs`) | merged |
| 7 | Parallel stages across panes | **next**, not started; needs the owner's decisions (below) |

Tests on `main`: 363 unit tests pass, 5 live ones skipped unless `AGENTHUB_LIVE_REVIEW=1`. With it, `TemplateHelperTests.Live_it_builds_a_sound_template_from_a_plan_and_changes_it` passes against the real Claude Code (2026-11-14, about a minute). The interface tests pass: `tests/ui/advanced-templates.ps1`, `advanced-template-run.ps1`, `saved-runs.ps1`, `template-editing.ps1`, the new `template-helper.ps1`, and `crab-helper.ps1` (the helper pane changed). `template-helper.ps1 -Live` drives the real helper through the window (build from a plan, then a change) and passed.

**Still not done: a run with a real Claude session, end to end.** Every run so far used a scripted orchestrator. The prompt the real orchestrator gets (`RunPrompt` in `PaneOrchestrator.Run.cs`, the `[RUN]` section from `TemplateRun.Describe`, and the changed-template notice from `DescribeRun`) has never been read by a model. Expect to tune it. This is the biggest unknown; do it before or alongside stage 7. The Template helper's prompt, by contrast, has been tried live and gave sound templates (a branch per stage, a context read, a build with "don't start stage N", an escalating watch, handoff, commit, pull requests as the plan said).

**The taskbar install** is on `f847e17` (stage 4): it has neither stage 5 nor 6. Update it only with `scripts\Deploy-Taskbar.ps1 -CloseRunning` (see `AGENTS.md`), after asking whether AgentHub may be closed, and confirm with `-Check`.

## Decisions the owner made

1. The worker runs every git step; AgentHub runs no git for a run.
2. The orchestrator judges a step done from the worker's reply. AgentHub checks nothing itself.
3. Parallel stages must stay possible: the format has `after` on stages for it. The engine runs one worker in `ExecutionOrder()`.
4. The helper is a crab like the others (`AppHelperPane`, as the Timeline helper uses it), good at building templates.
5. Templates travel with the project and are committed: `orchestration/<id>.json` in the project's repository. Not `.agenthub/`, which this repository and others git-ignore.
6. Editing is both by hand and by the helper. Either writes a draft (`<id>.draft.json`), the view shows it, and saving replaces the original.
7. No loops.
8. This repository does not follow IGO's GitHub policy (it is used at home and at IGO): once a pull request's checks pass, merge it and go on to the next stage without asking. Still no force push and no rewriting pushed history.

## Defaults taken without an answer (confirm or change)

The owner said "start building the next steps" without answering the stage 5 questions, so the proposed answers were taken:

- **During a run only steps not yet reached can be edited or removed.** Done, skipped and current steps are locked (🔒), stay in place, and nothing new can go before the current step.
- **A paused run takes a saved change only on ⤵ Apply to this run**, never by itself, and only while it stands still (ready, paused, finished).
- **Up and down buttons**, no drag. A step moves over the edge of its stage into the next.

From earlier sessions, still unconfirmed:

- A run is attached to a pane that already has a running session. A `new-session` step restarts the session in that same pane.
- A paused run is never put on a session by itself. **▶ Resume…** offers the running sessions of the run's project that have no orchestrator. Resuming into a session AgentHub starts itself is not built.
- Runs live outside any repository: `C:\data\agenthub\runs` (`AGENTHUB_RUNS_DIR`).

Taken in stage 6 without an answer (the owner said "build the rest"):

- **The helper may change a template a run follows**, as a draft. It is told the steps the run has reached and to keep them; ⤵ Apply still refuses a change to them. This was the open stage 6 question.
- **What the helper makes opens in the editor at once**, unless the owner is editing another template; then it waits in the list.
- **The helper reads the project itself** (Read, Grep, Glob on the project, read-only) rather than AgentHub pasting the plan in. It writes nothing; AgentHub writes the draft.

## Stage 6: how it was built

- `TemplateHelper` (service): the system prompt (the step kinds generated from `StepKind`, the id rules, a default stage shape), the reply schema (`answer`, `suggestions`, `action` none / create / change, the whole `template`), the request (the project's templates, the one on screen as JSON, a run's reached steps, the step ids taken, the plan files found, the conversation), `RunAsync` (print mode, prompt in a file, `--add-dir` the project), `Read`, `Prepare` (ids made valid; an id used twice or that a removed step had gets a new one; fields a kind does not use dropped), `NewTemplateId`, `FindPlans`.
- `AppHelperPane` gained `Answerer` (answers in place of `AppHelp`), `WorkingText`, `AskToolTip`, `Ask`, `Stop`, `BusyChanged`. The main window's and the Timeline's helpers are unchanged.
- `AdvancedTemplateStore.List` lists a draft with no template beside it as a new template (`AdvancedTemplateFile.NewDraft`, `IsNew`); the window shows it, opens it in the editor and saves it like any draft.
- `AdvancedTemplatesWindow.Helper.cs`: the crab, the pane, the plan buttons, and `AnswerAsync`: flushes the draft being edited, asks, refuses to write over a template changed meanwhile, prepares and writes the draft, opens it, and adds to the crab's answer where the draft is, its problems, the ids AgentHub corrected and whether the run could take it.
- Fixed on the way: `AdvancedTemplate.HasParallelStages` was written into every template file (`"hasParallelStages": false`), and threw while writing a template whose stages loop. It is `[JsonIgnore]` now. Files saved before carry the property; it is ignored when read and dropped when written again.

## Stage 7: what to decide and build

Parallel stages across panes: two stages that do not wait for each other (`after`) run at once on separate workers. The format, `Predecessors`, `ExecutionOrder` and `HasParallelStages` exist; the engine runs one worker in `ExecutionOrder()`. Questions for the owner before building:

1. **Where the second worker works.** `AGENTS.md` says two autonomous agents must not edit the same working tree by default: parallel work belongs in git worktrees. Should AgentHub create a worktree per parallel stage (where, named how, removed when?), or should the template's steps do it (a new step kind)?
2. **Which panes.** A run attached to one pane needs more: take idle panes, start new ones, or ask each time?
3. **One orchestrator or one per pane.** One orchestrator watching several workers keeps one conversation with the owner; one per pane is simpler for the engine but splits the conversation.
4. **Merging back.** When parallel stages finish, who merges their branches: a `merge-branch` step in a later stage that waits for both (`after`), as the format already allows?

Likely shape: `TemplateRun` keeps a place per lane instead of one index; the run bar and the tree show a lane per pane; `RunStore` records each lane's place.

## Things to watch for

- **Three open pull requests from other sessions touch the same code**: 7 (`fix(orchestrator): report turns the worker starts by itself`), 8 (`fix(terminal): copy reliably…`) and 9 (`feat(orchestrator): let orchestrator start a fresh session on request`). 7 and 9 change `PaneOrchestrator`, which stages 3 to 5 changed heavily. They will need `main` merged in and their conflicts resolved by hand. Do not overwrite them.
- **`PaneOrchestrator.Run` is replaced, not changed, when a template is applied.** Read it once per use; never keep a `TemplateRun` across an await expecting it to be the live one. `ApplyTemplate` takes `_runGate` with no wait and refuses while the run is moving.
- **The changed-template notice is not saved with a run.** If the owner applies a change and detaches before the orchestrator is asked again, a resumed orchestrator with the same Claude session is not told of the change in `[RUN]` (its system prompt is rebuilt from the saved run's template, so it still sees the new outline there).
- **Editing tools are named once per redraw.** Typing a step's text updates its title on the tree at once, but its buttons' names and tooltips ("Move up: …") keep the old title until the next redraw (closing the fields, any structural change). UI tests must close the editor before finding a step's buttons by its new title.
- **The same text can appear twice in the window**: a template's size is both in the list and above the tree. Count with `-ge 1` in UI tests.
- **A fresh session is not "waiting for input".** `IsAwaitingInput` is only true after a session finished a turn. The run treats a session that has been quiet for `TurnSettle` (15 s) as idle, and `FollowWhenQuietAsync` in the loop picks a waiting run up once it is.
- **The interface tests copy `%APPDATA%\AgentHub\settings.json`**, so AgentHub must have been run once on the computer. They take the app as `-Exe`; build one with `dotnet build src\AgentHub -c Release -o C:\data\agenthub\build-test`. They use UI Automation only and write screenshots to `C:\data\agenthub\uitest`. Look at the screenshots: the checks do not see layout.
- **Windows PowerShell 5.1 reads a script without a byte order mark as ANSI.** A test script with non-ASCII text in it must be saved as UTF-8 with a byte order mark, or stay ASCII. Also: `Type` is an alias of `Get-Content`; do not name a helper function after it.
- **A WPF message box reached through its owner window lists its buttons as bare panes** in UI Automation. The tests find them by name and press them with `BM_CLICK` (`Named`, `Press`).
- **An owned window's elements are also descendants of the main window** in UI Automation: a text shown in the tree window and in the pane's box counts twice from the main window.
- **Scripted tests can race the run.** New tests should wait on a state, not on an exact count of messages.
- **The scripted Template helper** is a `.cmd` that hands stdin to `tests/ui/fake-template-helper.ps1` (`AGENTHUB_TEMPLATE_HELPER_COMMAND`); it answers by words in the `<request>` (build … PLAN.md, review step, broken, escalation, fail, slow) and logs every request, so tests check what the helper was told. Wait for the pane's **Ask** button to come back, not for a time.
- **The helper's answer has AgentHub's own lines added** (where the draft is, its problems, ids corrected). The model is told not to say where the draft is; if both say it, look at the prompt's `answer` rule.
- On the IGO computer the active `gh` account is the IGO one and the repository belongs to `Daznl`; commands there pass that account's token for the one call. At home, with `Daznl` as the active account, plain `git` and `gh` work.

## Working rules that held

- One branch and one pull request per stage, from the updated trunk; never based on another branch.
- Build, unit tests and the interface tests before each pull request; screenshots looked at; README, roadmap and `docs/ADVANCED_TEMPLATES.md` updated in the same pull request.
- Pull request titles: `type(scope): verb object`, under 70 characters.

## First steps for the next session

1. `git pull` (or clone), then `dotnet build AgentHub.sln` and `dotnet test tests\AgentHub.Tests`.
2. Ask whether to put the latest `main` on the taskbar (`scripts\Deploy-Taskbar.ps1`); it closes the running app.
3. Confirm the stage 5 and 6 defaults above, ask whether to do the live run with a real Claude session first, and put the stage 7 questions to the owner.
4. Rewrite this file at the end of the session.

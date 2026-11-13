# Handoff: advanced templates

Written 2026-11-13 at the end of the session that built stages 1 to 4, for a new session on another computer (a fresh clone). Everything described here is on `main` at `68ccd33`; no work is left on a branch.

Read first: `AGENTS.md`, then `docs/ADVANCED_TEMPLATES.md` (what the feature is, how it is used, the file format, how a run works, the build order). This file only adds what that one does not say: where things stand, what was decided, what to do next and what to watch for.

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
| 5 | Editing templates in the app, through drafts, also while a run is paused | **next** |
| 6 | Helper crab: builds a template from a plan, changes one from a request | not started |
| 7 | Parallel stages across panes | not started |

Tests on `main`: 327 unit tests pass, 6 live ones skipped (run eight times in a row). Three interface tests pass: `tests/ui/advanced-templates.ps1`, `advanced-template-run.ps1`, `saved-runs.ps1`.

**Not done: a run with a real Claude session, end to end.** Every run so far used a scripted orchestrator (and, in the interface tests, a `cmd.exe` session). The prompt the real orchestrator gets (`RunPrompt` in `PaneOrchestrator.Run.cs`, the `[RUN]` section from `TemplateRun.Describe`) has never been read by a model. Expect to tune it. This is the biggest unknown and is worth doing before stage 5 or right after it.

**Not done: the taskbar install.** The AgentHub the owner runs from the taskbar has none of this. Update it only with `scripts\Deploy-Taskbar.ps1` (see `AGENTS.md`), after asking, since it closes the running app.

## Decisions the owner made

1. The worker runs every git step; AgentHub runs no git for a run.
2. The orchestrator judges a step done from the worker's reply. AgentHub checks nothing itself.
3. Parallel stages must stay possible: the format has `after` on stages for it. The engine runs one worker in `ExecutionOrder()`.
4. The helper is a crab like the others (`AppHelperPane`, as the Timeline helper uses it), good at building templates.
5. Templates travel with the project and are committed: `orchestration/<id>.json` in the project's repository. Not `.agenthub/`, which this repository and others git-ignore.
6. Editing is both by hand and by the helper. Either writes a draft (`<id>.draft.json`), the view shows it, and saving replaces the original. `AdvancedTemplateStore` already does the draft handling.
7. No loops.
8. This repository does not follow IGO's GitHub policy (it is used at home and at IGO): once a pull request's checks pass, merge it and go on to the next stage without asking. Still no force push and no rewriting pushed history.

## Defaults taken without an answer (confirm or change)

- A run is attached to a pane that already has a running session. A `new-session` step restarts the session in that same pane.
- A paused run is never put on a session by itself. **▶ Resume…** offers the running sessions of the run's project that have no orchestrator; with none, it says to start one. Resuming into a session AgentHub starts itself is not built.
- Runs live outside any repository: `C:\data\agenthub\runs` (`AGENTHUB_RUNS_DIR`).

## Stage 5: what to build

Editing in `AdvancedTemplatesWindow`: add, remove, reorder and change stages and steps; every change goes to the draft; **Save** calls `AdvancedTemplateStore.AcceptDraftAsync`; a draft with problems cannot be saved (the store refuses, the window should say why); **Discard** drops the draft. The window already notes when a draft exists but does not show it.

Editing while a run is paused: `TemplateRun.Restore(template, snapshot, …)` already carries progress over to a changed template by step id (steps it no longer has are forgotten, new ones are to do). What is missing is the way to give a live run a changed template: the run holds its template for life (`TemplateRun.Template`), and `PaneOrchestrator.Run` has no setter. A likely shape: when paused, rebuild the run from `Restore(newTemplate, run.Snapshot(), …)` and tell the orchestrator in its next `[RUN]` section. The orchestrator's system prompt holds the outline as it stood at attach and already says `[RUN]` wins.

Open questions for the owner, asked and not yet answered:

1. Can a step that is already done be edited or removed during a run, or only steps not yet started?
2. Does a paused run take a saved change by itself, or only on something like "Apply to this run"?
3. Drag to reorder, or are up and down buttons enough at first?

## Things to watch for

- **Three open pull requests from other sessions touch the same code**: 7 (`fix(orchestrator): report turns the worker starts by itself`), 8 (`fix(terminal): copy reliably…`) and 9 (`feat(orchestrator): let orchestrator start a fresh session on request`). 7 and 9 change `PaneOrchestrator`, which stages 3 and 4 changed heavily (it is now a partial class with `PaneOrchestrator.Run.cs`; `AskAsync` returns whether the run advanced; the restart is in `RestartWorkerAsync`). They will need `main` merged in and their conflicts resolved by hand. Do not overwrite them.
- **A fresh session is not "waiting for input".** `IsAwaitingInput` is only true after a session finished a turn. The run treats a session that has been quiet for `TurnSettle` (15 s) as idle, and `FollowWhenQuietAsync` in the loop picks a waiting run up once it is. Keep this in mind for anything that starts a session and then expects a turn end.
- **The interface tests copy `%APPDATA%\AgentHub\settings.json`**, so AgentHub must have been run once on the computer. They take the app as `-Exe`; build one with `dotnet build src\AgentHub -c Release -o C:\data\agenthub\build-test`. They use UI Automation only and write screenshots to `C:\data\agenthub\uitest`. Look at the screenshots: the checks do not see layout.
- **Windows PowerShell 5.1 reads a script without a byte order mark as ANSI.** A test script with non-ASCII text in it must be saved as UTF-8 with a byte order mark, or stay ASCII.
- **A WPF message box reached through its owner window lists its buttons as bare panes** in UI Automation. `saved-runs.ps1` finds them by name and presses them with `BM_CLICK` (`Named`, `Press`).
- **An owned window's elements are also descendants of the main window** in UI Automation: a text shown in the tree window and in the pane's box counts twice from the main window.
- **Scripted tests can race the run.** A scripted worker that answers at once moves the run on while the test is still asserting. Two tests were flaky for that reason and for reading a record while it was being replaced; both are fixed, but new tests should wait on a state, not on an exact count of messages.
- On the IGO computer the active `gh` account is the IGO one and the repository belongs to `Daznl`; commands there pass that account's token for the one call. At home, with `Daznl` as the active account, plain `git` and `gh` work.

## Working rules that held this session

- One branch and one pull request per stage, from the updated trunk, in its own git worktree; never based on another branch. When a stage needed an unmerged one, the part that did not was built first.
- Build, unit tests and the interface tests before each pull request; screenshots looked at; README, roadmap and `docs/ADVANCED_TEMPLATES.md` updated in the same pull request.
- Pull request titles: `type(scope): verb object`, under 70 characters.

## First steps for the new session

1. `git clone https://github.com/Daznl/AgentHub.git`, then `dotnet build AgentHub.sln` and `dotnet test tests\AgentHub.Tests`.
2. Read `docs/ADVANCED_TEMPLATES.md`.
3. Ask the owner the three stage 5 questions above and whether to do the live run with a real Claude session first.
4. Rewrite this file at the end of the session.

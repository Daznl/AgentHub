# Handoff: advanced templates

Written 2026-11-13 at the end of the session that built stage 5 (editing), for the next session, on this computer or another (a fresh clone). Stages 1 to 4 are on `main`; stage 5 is on `feature/advanced-template-editing` (pull request below) until it is merged.

Read first: `AGENTS.md`, then `docs/ADVANCED_TEMPLATES.md` (what the feature is, how it is used, the file format, how a run works, editing, the build order). This file only adds what that one does not say: where things stand, what was decided, what to do next and what to watch for.

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
| 5 | Editing through drafts (`TemplateEditing`, `AdvancedTemplatesWindow.Editing.cs`); a saved change applied to a run that stands still (`TemplateRun.ProblemsWith` / `Rebase`, `PaneOrchestrator.ApplyTemplate`) | **built, branch `feature/advanced-template-editing`** |
| 6 | Helper crab: builds a template from a plan, changes one from a request | **next** |
| 7 | Parallel stages across panes | not started |

Tests on the branch: 341 unit tests pass, 4 live ones skipped. One live test fails on this computer, `LiveUsageServiceTests.Live_AntigravityUsage_ReturnsAvailableSnapshots` (the Antigravity CLI timed out); it has nothing to do with this work. Four interface tests pass: `tests/ui/advanced-templates.ps1`, `advanced-template-run.ps1`, `saved-runs.ps1` and the new `template-editing.ps1`.

**Still not done: a run with a real Claude session, end to end.** Every run so far used a scripted orchestrator. The prompt the real orchestrator gets (`RunPrompt` in `PaneOrchestrator.Run.cs`, the `[RUN]` section from `TemplateRun.Describe`, and now the changed-template notice from `DescribeRun`) has never been read by a model. Expect to tune it. This is the biggest unknown; do it before or alongside stage 6.

**The taskbar install** was updated this session to `f847e17` (stage 4). It does not have stage 5. Once the branch is merged, update it with `scripts\Deploy-Taskbar.ps1` (see `AGENTS.md`), after asking, since it closes the running app.

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

## Stage 6: what to build

The helper crab in the Advanced templates window: given a written plan (a file such as `docs/PLAN.md`, or text), it writes a template; given a request ("add a review step after each build"), it changes one. It writes the **draft** only (`AdvancedTemplateStore.SaveDraftAsync`); the owner sees it with ✏ Open the draft and saves or discards it, so stage 5's editor is how its work is checked. Look at how the Timeline helper uses `AppHelperPane` first. The helper must know the format (the "Format" section of `docs/ADVANCED_TEMPLATES.md` is the reference), the step kinds and the rule that step ids are never reused. Validate what it writes; a draft with problems is fine to show, not to save.

Open question for the owner: should the helper also be able to change a template a run follows, as a draft the owner then applies? Stage 5 already makes that safe (Apply refuses changes to reached steps).

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
- On the IGO computer the active `gh` account is the IGO one and the repository belongs to `Daznl`; commands there pass that account's token for the one call. At home, with `Daznl` as the active account, plain `git` and `gh` work.

## Working rules that held

- One branch and one pull request per stage, from the updated trunk; never based on another branch.
- Build, unit tests and the interface tests before each pull request; screenshots looked at; README, roadmap and `docs/ADVANCED_TEMPLATES.md` updated in the same pull request.
- Pull request titles: `type(scope): verb object`, under 70 characters.

## First steps for the next session

1. `git pull` (or clone), then `dotnet build AgentHub.sln` and `dotnet test tests\AgentHub.Tests`.
2. If stage 5's pull request is not merged yet, check its state first.
3. Confirm the stage 5 defaults above with the owner, ask whether to do the live run with a real Claude session first, and ask the stage 6 question.
4. Rewrite this file at the end of the session.

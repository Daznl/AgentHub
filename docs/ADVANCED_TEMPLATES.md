# Advanced templates

An **orchestration template** (📚 Templates, `workflows/*.md`) gives an orchestrator instructions and leaves the sequence to it. An **advanced template** is the sequence itself: stages and steps the orchestrator follows one by one, ticking each off, pausing when something needs the owner.

Status (2026-11-13): the file format, its checks and the project store are built (`AdvancedTemplate`, `AdvancedTemplateStore`), a window shows a project's templates as trees (`AdvancedTemplatesWindow`, `AdvancedTemplateTree`), and an orchestrator on a pane can follow one step by step, started, paused and sent on from that window (`TemplateRun`, `PaneOrchestrator.Run.cs`). A run is lost when its orchestrator is detached or AgentHub closes: keeping runs, editing and the helper crab are not built; see "Build order" below. Tested with a scripted orchestrator only, not yet with a real Claude session end to end.

## Following one

1. Start a session in a pane, in the project.
2. **🧭 Orchestrate → 🗺 Follow: <template>**. Only templates without problems are offered. An orchestrator is put on the session and the template's window opens with a purple run bar: **RUN READY**. Nothing is sent yet; tell the orchestrator anything it should know in its box on the pane.
3. **▶ Go** in the run bar. The current step is outlined and marked **● NOW**; steps turn green and **✓ DONE** as the orchestrator calls them done; the window follows the run down the tree.
4. **⏸ Pause** stops the run at its step (**⏸ PAUSED HERE**); **▶ Go** carries it on.
5. **▶ from here** on a step (shown under the pointer, or with the keyboard on it) sends the run on from that step after a confirmation: steps before it that are not done become **SKIPPED**.
6. When the run needs you (an ask-owner step, or an escalation) the bar says **RUN WAITING FOR YOU** with the question, the step says **🙋 WAITING FOR YOU**, and the pane pulses as for any orchestrator message. Answer in the orchestrator's box on the pane.
7. **🗺 Run** in the orchestrator's box brings the window back. **✕ Detach** ends the run.

The run follows the template as it was when the orchestrator was attached; the window says so when the file has changed since.

## Seeing them

**🧭 Orchestrate → 🗺 Advanced templates of this project…** on a running pane opens the window for the checkout the pane's folder is in (one window per project). `AgentHub.exe --advanced-templates <folder>` opens it at start.

The list on the left holds every file in `orchestration/`; a file that cannot be read, or has problems, is listed in amber and says what is wrong. The tree on the right runs from Start to Done: each stage a purple node with its steps beneath it, in the order a single worker follows. A step shows its kind on the right and **⚑ pauses for you** when it is an escalation point. A project without templates offers **➕ Add the staged build example**, which writes `orchestration/staged-build.json` (`AdvancedTemplate.StagedBuildExample`) to look at and change. The files are re-read on **↻ Refresh** and whenever the window is come back to.

## Where they live

`orchestration/<id>.json` in the project's own repository, committed with it, so the template travels with the project. Not under `.agenthub/`, which holds the owner's own notes and is often git-ignored.

A change, made by hand or by the helper, is first written beside the template as `orchestration/<id>.draft.json`. The view shows the draft; saving puts it in the template's place in one step. A draft that is not sound cannot be saved over the template. Drafts are kept out of commits by `orchestration/.gitignore`, which AgentHub writes with the first draft.

The file holds the plan only. How far a run has got is recorded elsewhere against the step ids, so steps can be added, removed and moved while a run is paused without losing its progress. Never reuse a step id.

## Format

```json
{
  "version": 1,
  "name": "Staged build",
  "description": "Builds the plan one stage at a time, a fresh session per stage.",
  "stages": [
    {
      "id": "stage-1",
      "title": "Stage 1: data model",
      "steps": [
        { "id": "s1-branch", "kind": "create-branch", "branch": "feature/data-model" },
        { "id": "s1-read", "kind": "instruct", "text": "Read docs/PLAN.md and .agenthub/handoff.md." },
        { "id": "s1-build", "kind": "instruct", "title": "Build stage 1", "text": "Build stage 1 of docs/PLAN.md. Don't start stage 2." },
        { "id": "s1-watch", "kind": "wait-for-worker", "escalate": true },
        { "id": "s1-handoff", "kind": "handoff" },
        { "id": "s1-commit", "kind": "commit" },
        { "id": "s1-close", "kind": "close-session" }
      ]
    },
    {
      "id": "stage-2",
      "title": "Stage 2: screens",
      "steps": [
        { "id": "s2-new", "kind": "new-session" },
        { "id": "s2-build", "kind": "instruct", "text": "Read .agenthub/handoff.md, then build stage 2 of docs/PLAN.md." },
        { "id": "s2-watch", "kind": "wait-for-worker", "escalate": true },
        { "id": "s2-check", "kind": "ask-owner", "text": "Stage 2 is built. Open the pull request?" },
        { "id": "s2-pr", "kind": "open-pull-request", "target": "main" }
      ]
    }
  ]
}
```

Template: `name` (needed), `description`, `model` (the orchestrator's; the setting's when left out), `stages`.

Stage: `id` (needed), `title`, `steps`, `after`.

Step: `id` and `kind` (needed), `title` (the label on the tree; made up from the kind when left out), `text`, `branch`, `target`, `escalate`.

Ids are lower-case letters, digits and dashes. Stage ids are unique among stages; step ids are unique in the whole template. Comments and trailing commas are read; unknown properties are ignored; AgentHub writes the file back without them.

### Step kinds

The worker carries out every step, the git ones included. The orchestrator judges from the worker's reply whether the step is done.

| `kind` | What happens | Needs |
| --- | --- | --- |
| `instruct` | `text` goes to the worker as its next instruction | `text` |
| `wait-for-worker` | the orchestrator watches until the worker's work is finished | |
| `ask-owner` | the run stops and `text` is put to the owner | `text` |
| `new-session` | a fresh worker session starts in the pane | |
| `close-session` | the worker session ends | |
| `handoff` | the worker writes its handoff notes and updates the project's documents | |
| `create-branch` | the worker creates `branch` and switches to it | `branch` |
| `commit` | the worker commits to the current branch | |
| `merge-branch` | the worker merges `branch` into `target` | `branch`, `target` |
| `move-work-to-branch` | the worker moves its uncommitted work onto `branch` | `branch` |
| `open-pull-request` | the worker opens a pull request into `target` (the trunk when left out) | |

`escalate: true` marks an escalation point: anything there the orchestrator cannot answer from the owner's instructions pauses the run and messages the owner.

### Order and parallel stages

`after` lists the stages that must be finished before a stage starts.

- Left out: the stage before it in the file (nothing for the first). A file without any `after` runs top to bottom.
- `[]`: nothing, so the stage can start at once.
- `["api", "ui"]`: once both are finished.

Two stages that do not depend on each other may run side by side on separate workers (`HasParallelStages`). A single worker follows `ExecutionOrder()`: every `after` respected, the stage earlier in the file first among those free to start. Stages cannot loop; a file whose stages wait for each other is refused.

## How a run works

Built 2026-11-13 (`TemplateRun`, `PaneOrchestrator.Run.cs`; tests `TemplateRunTests`, `tests/ui/advanced-template-run.ps1`).

A run is a pane orchestrator (🧭) given an advanced template. It is attached to a running session, like any orchestrator, and waits for **Go**; until then it is an orchestrator like any other, so the owner can tell it the job first.

**AgentHub keeps the place, never the orchestrator.** Every message to the orchestrator has a `[RUN]` section: the current step, what it means, and the step after it. Its reply has a fourth field, `step_outcome`:

| `step_outcome` | Meaning | What AgentHub does |
| --- | --- | --- |
| `done` | the step is complete, judged from the worker's reply | ticks it off, moves to the next step and asks again; a `to_worker` sent with it is dropped |
| `continuing` | under way | types `to_worker` into the worker, if any, and waits for its turn to end |
| `escalate` | the owner must settle something | pauses the run and shows `to_owner`; nothing goes to the worker |
| `null` | no step is concerned | nothing |

Step by step:

- **Steps for the worker** (`instruct`, the git kinds, `handoff`): the orchestrator words the instruction; when the worker's turn ends it judges the reply. If it words nothing, AgentHub sends the step's own instruction (`TemplateRun.WorkerInstruction`).
- **`wait-for-worker`**: nothing is sent. The orchestrator judges the worker's latest reply at once; when the worker is still busy, at its turn end.
- **`ask-owner`**: AgentHub puts the question in the orchestrator box and pauses. The owner's answer goes to the orchestrator, and `done` carries the run on.
- **`new-session`**, **`close-session`**: AgentHub starts a fresh session in the pane, the way the last one was started, or ends the session. The orchestrator stays on the pane while the run is unfinished, session or no session.

Pausing and going on:

- **Pause** stops the run at its current step. The worker's own turn runs on, but it is sent nothing more and its turn ends are not acted on; the orchestrator still answers the owner.
- **Go** carries a paused run on: the orchestrator is shown the worker's latest reply and decides whether the step is done already. Go with a step named goes on from that step, whatever the run was doing: steps before it that were not done count as skipped, it and everything after it are to do.
- An **escalation** is answered in the orchestrator box; the run carries on by itself once the orchestrator has what it needs (`continuing` or `done`), or on Go.
- The run also pauses, saying why, when there is no worker session for a step that needs one, when the session ends by itself, and when the orchestrator fails or reaches a usage limit.

The orchestrator's fixed rules still hold in a run. A step of the template is the owner asking for exactly that action (a `merge-branch` step needs no further approval), but never a force push, a rewrite of pushed history or anything destructive: such a step is escalated.

## Build order

1. [x] File format, checks, project store (`AdvancedTemplate.cs`, `AdvancedTemplateTests.cs`)
2. [x] Window: the tree, read-only, opened from 🧭 Orchestrate (`AdvancedTemplatesWindow`, `AdvancedTemplateTree`, `tests/ui/advanced-templates.ps1`)
3. [x] Run engine on `PaneOrchestrator`: step by step, live ticks, pause on escalation, Go / Pause / go on from a step. Remaining: a live run with a real Claude session
4. [ ] Runs kept on disk, a Paused runs panel, resume after AgentHub restarts
5. [ ] Editing by hand through drafts, also while a run is paused
6. [ ] The helper crab: builds a template from a plan, changes one from a request
7. [ ] Parallel stages across panes

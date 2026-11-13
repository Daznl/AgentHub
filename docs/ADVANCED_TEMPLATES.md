# Advanced templates

An **orchestration template** (📚 Templates, `workflows/*.md`) gives an orchestrator instructions and leaves the sequence to it. An **advanced template** is the sequence itself: stages and steps the orchestrator follows one by one, ticking each off, pausing when something needs the owner.

Status (2026-11-13): the file format, its checks and the project store are built (`AdvancedTemplate`, `AdvancedTemplateStore`). The panel, the run engine, paused runs, editing and the helper crab are not; see "Build order" below.

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

## Build order

1. [x] File format, checks, project store (`AdvancedTemplate.cs`, `AdvancedTemplateTests.cs`)
2. [ ] Panel: the tree, read-only, opened from 🧭 Orchestrate
3. [ ] Run engine on `PaneOrchestrator`: step by step, live ticks, pause on escalation, Go / Pause / start from a step
4. [ ] Runs kept on disk, a Paused runs panel, resume after AgentHub restarts
5. [ ] Editing by hand through drafts, also while a run is paused
6. [ ] The helper crab: builds a template from a plan, changes one from a request
7. [ ] Parallel stages across panes

# AgentHub workflows and orchestrator runs

This branch holds only AgentHub's own data:

- `workflows/`: Daniel's orchestrator workflows (copies of the built-in templates, edited for a project or pipeline). Pick one per idea in AgentHub's Ideas window.
- `runs/`: the record of every orchestrator run, one folder each. Start with `runs/INDEX.md`, then a run's `REVIEW.md` (the run turn by turn), `run.json` (facts), `workflow.md` (the workflow exactly as it ran), `events.jsonl` (what AgentHub sent and did) and `orchestrator.jsonl` (the orchestrator's Claude Code transcript). Worker transcripts are not kept here.

AgentHub commits here through its own worktree; it never pushes. Get it in a clone with `git fetch origin agenthub-workflows` and `git worktree add ../agenthub-workflows agenthub-workflows`.

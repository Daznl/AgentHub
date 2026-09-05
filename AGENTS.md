# AgentHub Agent Instructions

Read `README.md`, `docs/ARCHITECTURE.md`, and `docs/ROADMAP.md` before making architectural changes.

## Product intent

AgentHub is a Windows-first personal control centre for GitHub repositories and interchangeable coding CLIs. The repository/task is the durable workspace; Codex, Claude Code, Antigravity, and future agents are executors that can be swapped.

## Engineering rules

- Target Windows with .NET 8 WPF unless a migration is explicitly chosen.
- Keep agent commands configurable in settings.
- Do not store API keys or provider credentials.
- Prefer the user's installed `git`/credential manager rather than reimplementing Git authentication.
- Pull remains fast-forward-only unless the user explicitly chooses another strategy.
- Treat destructive Git operations as explicit user actions.
- Do not let two autonomous agents edit the same working tree by default. Parallel tasks belong in Git worktrees.
- `.agenthub/handoff.md` is human-readable shared context and must remain usable without AgentHub.
- Usage/quota estimates must identify their source and must not imply exact provider balances when they are inferred.

## Before finishing a change

- Build the solution.
- Exercise affected Git command paths with spaces in repository paths.
- Keep README/roadmap updated when behavior changes.

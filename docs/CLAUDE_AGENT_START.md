# Suggested prompt for the next coding agent

You are continuing development of AgentHub, a Windows-first WPF application that manages local GitHub repositories and launches interchangeable coding CLIs (Codex, Claude Code, Google Antigravity).

Start by reading README.md, docs/ARCHITECTURE.md, and docs/ROADMAP.md. Build the solution and fix any compile/runtime issues before adding features.

The next target is v0.2: task worktrees. Implement it incrementally and preserve these rules:

1. GitHub/origin remains the canonical remote; Git remains the source of code history.
2. Different tasks get different worktrees. Do not encourage multiple agents to edit the same working tree concurrently.
3. Provider CLI commands must remain configurable, not hard-coded beyond defaults.
4. Never store provider credentials/API keys.
5. Destructive Git operations require explicit user action and clear UI.
6. Preserve the v0.1 fast-forward-only pull behavior.

For v0.2, add a task model, a workspace/worktree service, create/list/open/remove worktrees, and launch any configured agent inside a task worktree. Add tests for command construction/path handling where practical.

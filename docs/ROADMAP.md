# Roadmap

## v0.1 — Repo + agent launcher (included)

- Repo registry
- Clone/add existing
- Git status
- Fetch / fast-forward pull / push
- Open GitHub/folder
- Configurable Codex / Claude / Antigravity launch
- Shared handoff file

## v0.2 — Task worktrees

- Create task from selected repo
- Automatically create branch + Git worktree
- List active tasks/worktrees beneath each repo
- Launch any configured agent in a task worktree
- Archive/delete completed worktree after merge
- Detect dirty worktrees and protect against accidental removal

## v0.3 — Session manager

- Track agent process/session start and end
- Session history per repo/task
- Quick switch: Codex → Claude → Antigravity in same worktree
- Generate a handoff template from `git diff`, recent commits and task notes
- Optional provider-specific context files (`AGENTS.md`, `CLAUDE.md`, etc.) without duplicating project truth

## v0.4 — Usage + budget dashboard

- Pluggable usage adapters
- Record observed CLI/session consumption where available
- Manual remaining-quota override when providers expose no machine-readable balance
- Session burn-rate estimates
- Recommend an agent based on user-defined priorities: capability, remaining quota, reset time, cost
- Clear confidence/source label beside every usage figure

## v0.5 — GitHub workflow

- GitHub CLI (`gh`) integration
- PR create/view/open
- CI status
- Issue/task association
- Branch protection awareness

## Multi-Session Cockpit Stages (Active)

- **Stage 1 (Completed):** Embedded dual-terminal multiplexer via Windows ConPTY + WebView2/xterm.js. Side-by-side terminal panes for concurrent interactive sessions.
- **Stage 2 (Next):** Interactive CLI compatibility testing & ANSI escape sequence verification across Antigravity, Claude Code, and Codex.
- **Stage 3:** Attention detector & alerting engine (flashing badges, bell chime, and input-idle detection when an agent awaits user confirmation).
- **Stage 4:** Automated Git worktree isolation per task with cross-agent handoff orchestration.

## Later

- Notifications when an agent exits or needs attention via Windows Toast
- Repository templates and bootstrap rules
- Multiple agent profiles per provider (e.g. conservative vs autonomous)
- Model selection metadata where a CLI exposes a stable interface
- Optional local SQLite database once settings/session data outgrow JSON

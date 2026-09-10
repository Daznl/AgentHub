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

- [x] Configurable Codex, Claude and Gemini CLI usage commands
- [x] Background pseudoconsole (ConPTY) terminal probe for interactive slash commands (`/status`)
- [x] Native headless Antigravity expansion (`agy -p /usage`) with multi-model limit tracking
- [x] Normalized remaining-limit dashboard (percentage **left**) with reset times, progress bars, refresh state and isolated provider failures
- [x] Clean per-provider usage sources instead of TUI scraping: Codex on-disk `rate_limits` (newest reading across concurrent sessions), Claude usage endpoint, Antigravity `agy -p /usage`
- [x] User-configurable, persisted usage polling interval (15s - 120m) replacing the earlier adaptive engine
- [x] Collapsible raw source feed panel for live output verification
- Pluggable non-CLI usage adapters
- Record observed CLI/session consumption where available
- Manual remaining-quota override when providers expose no machine-readable balance
- Session burn-rate estimates
- Recommend an agent based on user-defined priorities: capability, remaining quota, reset time, cost
- Clear confidence/source label beside every usage figure

## v0.5 — GitHub workflow (Partially completed in v0.1.5 / v0.1.6)
 
- [x] GitHub CLI (`gh`) integration & repository browser (v0.1.5: remote repo search, filter, 1-click clone, live sync state badges, in-card fast-forward pull, and 1-click push)
- [x] In-app GitHub sign-in with real browser verification (v0.1.6): `gh auth login --web` driven non-interactively, one-time code shown in a dialog, browser opened, `gh` keeps the token. Terminal route kept as fallback.
- [x] Multiple GitHub accounts (v0.1.6): account dropdown from `gh auth status --json`, switch via `gh auth switch`, sign out via `gh auth logout`, GitHub Enterprise hosts accepted.
- [x] Load every repository the account can see (v0.1.6): single paginated GraphQL query (own + shared + organisation), streamed page by page into a virtualised list. Replaces `gh repo list`, which only returned personally owned repos.
- [x] Access-aware categorisation (v0.1.6): GitHub `viewerPermission` + owner + archived state group repos into mine / org you can push / org clone-only / shared / archived, with per-card role badges, access-based filters, and Push shown only where GitHub will accept it. Everyone can clone anything visible. See `docs/GITHUB_INTEGRATION.md`.
- Opt-in `gh auth setup-git` button so `git push` credentials follow the active gh account
- PR create/view/open
- CI status
- Issue/task association
- Branch protection awareness

## Multi-Session Cockpit Stages (Active)

- **Stage 1 (Completed in v0.1.5):** Embedded dynamic multi-terminal multiplexer via Windows ConPTY + WebView2/xterm.js. Side-by-side terminal panes for concurrent interactive sessions with 3+ default terminals, dynamic "➕ Add Terminal" pane creation, individual "✕ Close" controls, draggable splitters, and robust lifecycle idempotency preventing input duplication.
- **Stage 1b (Completed in v0.1.6):** Open a shell in any folder: Cockpit toolbar "📂 PowerShell in Folder…" (folder picker → plain PowerShell in a new pane, idle pane reused when all 6 exist) and a per-pane 📂 button launching that pane's selected shell/agent in a chosen folder (confirms before replacing a running session). Last folder persisted as `LastTerminalFolder` in settings.
- **Stage 2 (Next):** Interactive CLI compatibility testing & ANSI escape sequence verification across Antigravity (`agy`), Claude Code (`claude`), and Codex (`codex`).
- **Stage 3 (In progress):** Attention detector & alerting engine. Shipped: `AttentionDetector` (input-idle after a work burst, terminal bell) armed only for agent CLI sessions, amber pane pulse plus a persistent **⏳ WAITING FOR YOU** badge, and taskbar flashing when AgentHub is backgrounded; thresholds in `AttentionIdleSeconds` / `AttentionFlashEnabled`. Remaining: bell chime, `[y/N]`-style prompt heuristics, Windows Toast.
- **Stage 4:** Automated Git worktree isolation per task with cross-agent handoff orchestration.

## Later

- Notifications when an agent exits or needs attention via Windows Toast
- Repository templates and bootstrap rules
- Multiple agent profiles per provider (e.g. conservative vs autonomous)
- Model selection metadata where a CLI exposes a stable interface
- Optional local SQLite database once settings/session data outgrow JSON

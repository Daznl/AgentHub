# Architecture

## Core rule

**Repo first, agent second.**

AgentHub owns workflow metadata and orchestration. Git owns code history. GitHub owns the canonical remote. Provider CLIs own model authentication and execution.

## Current components

```text
MainWindow (WPF)
    |
    +-- SettingsService ------- %APPDATA%/AgentHub/settings.json
    +-- GitService ------------ git.exe (status, fetch, pull --ff-only, push)
    +-- GitHubService --------- gh.exe (auth status/accounts, browser device-flow login, account switch, GraphQL repo query, cloning)
    |     +-- GitHubLoginWindow  modal that drives `gh auth login --web` and shows the one-time code
    +-- RepoDiscoveryService -- local folder scanner & auto-detection
    +-- UsageService --------- clean per-provider usage readers + normalized snapshots
    +-- AgentLauncher --------- wt.exe / powershell.exe external launcher
    +-- RepoContextService ---- .agenthub/handoff.md + Explorer/browser
    |
    +-- Multi-Session Cockpit (WPF Grid + Splitters)
         |
         +-- "PowerShell in Folder…" / per-pane 📂  -- FolderBrowserDialog -> session cwd (any folder, not only repos)
         +-- TerminalPaneControl (x N panes)
              |
              +-- EmbeddedTerminalControl (WebView2 + xterm.js)
                   |
                   +-- ConPtySession (Win32 PseudoConsole HPCON)
                        |
                        +-- ConPtyNative (kernel32 CreatePseudoConsole, CreateProcess)
                        +-- Anonymous Pipes (in/out streaming)
                             |
                             +-- Process (powershell, cmd, codex, claude, agy)

External tools
    +-- git
    +-- gh (GitHub CLI)
    +-- codex (OpenAI Codex CLI)
    +-- claude (Anthropic Claude Code CLI)
    +-- agy (Google Antigravity CLI)
```

## Why shell out to Git

Using the user's installed Git keeps behavior consistent with PowerShell/GitHub Desktop and respects their credential manager, SSH setup and global Git config. It also avoids a native Git library dependency in the first version.

## GitHub integration (accounts, sign-in, access-aware browser)

Full detail lives in [GITHUB_INTEGRATION.md](GITHUB_INTEGRATION.md). The essentials:

```text
GitHub Repos view
    |
    +-- Account combo / Sign in / Sign out
    |     +-- gh auth status --json hosts      (who is signed in, which is active)
    |     +-- gh auth switch / gh auth logout  (per host + login)
    |     +-- GitHubLoginWindow -> gh auth login --hostname H --git-protocol https --web --skip-ssh-key
    |          (stdin closed => gh prints the one-time code + URL, never prompts; dialog shows code, opens browser)
    |
    +-- GitHubService.StreamAllRepositoriesAsync
          +-- gh api graphql --paginate  viewer.repositories(affiliations: OWNER, COLLABORATOR, ORGANIZATION_MEMBER)
          +-- one JSON object per line -> ParseRepositoryLine -> Progress<T> -> batched UI render
          +-- viewerPermission + owner + isArchived => CanPush / Category
```

1. **gh owns credentials.** AgentHub never sees a token; it drives `gh` non-interactively and parses
   what `gh` prints. The one-time device code is the only secret-adjacent value shown, and it is
   meant to be shown.
2. **GitHub decides push rights.** `viewerPermission` (ADMIN/MAINTAIN/WRITE => push; TRIAGE/READ =>
   clone/pull only; archived => never push) is read straight from GitHub and drives the Push button,
   the access badge and the group the repo appears in. Everyone can clone anything they can see.
3. **`gh repo list` is not "everything I can see".** Without an owner it lists only personally owned
   repos, which is why organisation-only accounts saw nothing. The GraphQL query with all
   affiliations replaces it.
4. **Stream, don't wait.** Pages arrive as NDJSON lines; the view re-renders every 25 repos / 400 ms
   into a virtualised, grouped `ListBox`. Loads are cancellable so account switches never mix lists.

## Terminal subsystem & ConPTY integration

AgentHub hosts interactive character-mode processes directly inside WPF using:
1. **Windows PseudoConsole (ConPTY):** Win32 API (`CreatePseudoConsole`, `CreateProcess` with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`) handles low-level VT/ANSI translation, process lifecycle, and buffer virtualization.
2. **Synchronous anonymous pipes:** Bidirectional streaming with dedicated background reading threads to avoid deadlocks. Pseudoconsole ends of pipes are closed immediately after child process creation to allow proper EOF detection.
3. **Microsoft WebView2 + xterm.js:** Renders the terminal frontend with hardware-accelerated canvas rendering, full ANSI color palette, and dynamic resizing via `fitAddon`.
4. **Lifecycle & event idempotency:** WPF `Loaded` events fire repeatedly across tab switches and visual tree rearrangements. `EmbeddedTerminalControl` utilizes strict initialization state flags (`_isInitialized`, `_isInitializing`) and idempotent event subscriptions to guarantee zero duplicate input handlers or ghost processes.
5. **Working directory is a first-class input, not derived from repos.** A session's cwd normally comes from the pane's repository dropdown, but the Cockpit toolbar's "📂 PowerShell in Folder…" and each pane's 📂 button let the user pick any folder through the Windows folder picker. The toolbar button always opens plain PowerShell in a new (or idle) pane; the pane button launches that pane's selected shell/agent and confirms before replacing a running session. The last folder is persisted as `AppSettings.LastTerminalFolder`. Details in [MULTI_SESSION_COCKPIT.md §2.3](MULTI_SESSION_COCKPIT.md).

## Planned v0.2 session model

A session will be a durable record:

```text
Session
- id
- repo id
- worktree path
- branch
- agent id
- task title
- started/ended timestamps
- process id (while live)
- status
- handoff path
- git start/end commit
```

The session model enables real agent switching without losing the relationship between a task, branch and terminal.

## Planned worktree flow

```text
main repo
  |
  +-- task: auth-fix
       |
       +-- branch: agent/auth-fix
       +-- worktree: <AgentHub workspace>/repo/auth-fix
       +-- launch Codex
       +-- optional handoff -> Claude in SAME worktree
```

Different tasks should get different worktrees. Multiple agents should not concurrently edit the same working tree unless the user explicitly chooses to do so.

## Usage monitoring

AgentHub monitors provider rate limits without burning prompt quota or storing API keys. The
guiding principle (learned the hard way): **prefer clean structured sources over scraping redrawn
terminal frames.** Each provider is read from the cleanest signal it exposes:

```text
Cockpit usage dashboard
    |
    +-- UsagePollingBadge (user-set interval, persisted to settings.json)
    +-- Usage interval box (0.25m - 120m; Enter / focus-out applies + refreshes)
    +-- ToggleRawOutputBtn / RawOutputPanel (inspect the raw source text)
    |
    +-- UsageService (parallel collection, isolated per-provider failures)
         |
         +-- CodexUsageReader  ---- ~/.codex/sessions/**/rollout-*.jsonl (on-disk rate_limits)
         +-- ClaudeUsageReader ---- Anthropic OAuth usage endpoint (JSON)
         +-- ProcessRunner + UsageOutputParser -- agy -p /usage (non-interactive stdout)
              |
              +-- UsageSnapshot[] -> WPF display models (cards, % left bars, reset timestamps)
```

1. **Codex — on-disk rate limits (no CLI call):** Codex records the latest server-reported
   `rate_limits` block (primary = 5-hour, secondary = weekly) into its session rollout JSONL files
   as an API side effect. `CodexUsageReader` reads these directly. Because any write bumps a file's
   mtime and several sessions can be open at once, it selects the reading with the newest **recorded
   timestamp** across recent files (not the newest file mtime), and tolerates files locked by a live
   Codex session (shared read). Because Codex only writes a reading on an API call, a window whose
   `resets_at` has already passed is treated as rolled over and fully available again (shown as 100%
   left) rather than surfacing the stale pre-reset number.
2. **Claude — OAuth usage endpoint:** `ClaudeUsageReader` reads Claude's structured usage JSON
   (session/5-hour and weekly utilization + reset times) rather than driving the interactive
   `/status` TUI.
3. **Antigravity — native headless expansion:** `agy -p /usage` prints tab-separated live limits for
   Gemini and Claude/GPT models in ~1s without prompting the LLM; `UsageOutputParser` strips ANSI and
   extracts the limits.
4. **User-controlled polling:** A single refresh interval (minutes) is user-set, persisted to
   `settings.json`, and clamped to a 15-second floor / 120-minute ceiling. The badge shows the active
   terminal count and the current cadence; editing the interval box applies and refreshes immediately.
5. **Presentation:** Bars show percentage **left** (full = plenty remaining, amber ≤ 35%, red ≤ 15%),
   with human-friendly reset text (near-term windows count down; further-out ones show day + clock).
6. **Safety:** AgentHub stores no API keys or provider credentials. Every displayed value identifies
   the local source that supplied it, and parser/read failures are surfaced per-provider instead of
   being presented as a balance. Default providers are preconfigured in `settings.json` and migrated
   on launch.

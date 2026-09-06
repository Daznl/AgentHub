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
    +-- GitHubService --------- gh.exe (auth status, remote repo query, cloning)
    +-- RepoDiscoveryService -- local folder scanner & auto-detection
    +-- UsageService --------- clean per-provider usage readers + normalized snapshots
    +-- AgentLauncher --------- wt.exe / powershell.exe external launcher
    +-- RepoContextService ---- .agenthub/handoff.md + Explorer/browser
    |
    +-- Multi-Session Cockpit (WPF Grid + Splitters)
         |
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

## Terminal subsystem & ConPTY integration

AgentHub hosts interactive character-mode processes directly inside WPF using:
1. **Windows PseudoConsole (ConPTY):** Win32 API (`CreatePseudoConsole`, `CreateProcess` with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`) handles low-level VT/ANSI translation, process lifecycle, and buffer virtualization.
2. **Synchronous anonymous pipes:** Bidirectional streaming with dedicated background reading threads to avoid deadlocks. Pseudoconsole ends of pipes are closed immediately after child process creation to allow proper EOF detection.
3. **Microsoft WebView2 + xterm.js:** Renders the terminal frontend with hardware-accelerated canvas rendering, full ANSI color palette, and dynamic resizing via `fitAddon`.
4. **Lifecycle & event idempotency:** WPF `Loaded` events fire repeatedly across tab switches and visual tree rearrangements. `EmbeddedTerminalControl` utilizes strict initialization state flags (`_isInitialized`, `_isInitializing`) and idempotent event subscriptions to guarantee zero duplicate input handlers or ghost processes.

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
   Codex session (shared read).
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

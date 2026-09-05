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
    +-- UsageService --------- configurable CLI usage commands + normalized parsers
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

AgentHub monitors provider rate limits without burning prompt quota or storing API keys:

```text
Cockpit usage dashboard
    |
    +-- UsagePollingBadge (adaptive interval: 1m - 15m based on active terminals)
    +-- ToggleRawOutputBtn / RawOutputPanel (inspect raw captured text)
    |
    +-- UsageService (parallel collection, isolated provider failures)
         |
         +-- ProcessRunner (native headless CLI execution: agy -p /usage)
         |
         +-- BackgroundTerminalCollector (background ConPTY probe: codex /status, claude /status)
         |
         +-- UsageOutputParser (ANSI strip, regex parsing, tab delimiter extraction)
              |
              +-- UsageSnapshot[] -> WPF display models (cards, progress bars, reset timestamps)
```

1. **Native Headless Execution:** Google Antigravity (`agy`) supports native non-interactive slash command expansion via `agy -p /usage`, returning tab-separated live limits for Gemini and Claude/GPT models (weekly and 5-hour limits) in ~1s without prompting the LLM.
2. **Background Pseudoconsole Probes:** Codex and Claude Code require genuine TTYs for slash commands (`/status`). `BackgroundTerminalCollector` starts an isolated background ConPTY session, sends sequential keystrokes (`/status`, Left-Arrow tab navigation for Claude session usage), captures raw terminal streams, and parses progress bars and reset times safely.
3. **Adaptive Polling Engine:** Polling frequency scales dynamically with the number of active interactive sessions running in Cockpit:
   - 0 active terminals: 15 minutes (`IDLE · EVERY 15M`)
   - 1 active terminal: 5 minutes (`⚡ 1 ACTIVE · EVERY 5M`)
   - 2 active terminals: 2 minutes (`⚡ 2 ACTIVE · EVERY 2M`)
   - 3+ active terminals: 1 minute (`⚡ {N} ACTIVE · EVERY 1M`)
4. **Terminal Feed Panel:** A collapsible "Terminal Feed ▾" panel displays the raw terminal output captured from each CLI for verification.
5. **Safety:** AgentHub stores no API keys or provider credentials. Every displayed value identifies the local CLI command that supplied it, and parser failures are surfaced instead of being presented as a balance. Default providers are preconfigured in `settings.json` and automatically migrated on launch.

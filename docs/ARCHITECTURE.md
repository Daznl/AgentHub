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

Provider quota APIs are inconsistent or absent. AgentHub should use adapters with confidence labels:

```text
IUsageProvider
- GetUsageAsync()
- source: official API | CLI output | local log | manual
- timestamp
- confidence
```

The app must never present inferred usage as an exact provider balance.

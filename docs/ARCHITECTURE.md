# Architecture

## Core rule

**Repo first, agent second.**

AgentHub owns workflow metadata and orchestration. Git owns code history. GitHub owns the canonical remote. Provider CLIs own model authentication and execution.

## Current components

```text
MainWindow (WPF)
    |
    +-- SettingsService ---- %APPDATA%/AgentHub/settings.json
    +-- GitService --------- git.exe
    +-- AgentLauncher ------ wt.exe / powershell.exe
    +-- RepoContextService - .agenthub/handoff.md + Explorer/browser

External tools
    +-- codex
    +-- claude
    +-- agy
```

## Why shell out to Git

Using the user's installed Git keeps behavior consistent with PowerShell/GitHub Desktop and respects their credential manager, SSH setup and global Git config. It also avoids a native Git library dependency in the first version.

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

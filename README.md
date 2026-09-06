# AgentHub

A Windows-first local control centre for working with multiple AI coding CLIs across GitHub repositories.

AgentHub treats the **repository as the workspace** and the AI CLI as an interchangeable tool. Add or clone a repo once, inspect Git status, pull/push, then launch Codex, Claude Code, or Google Antigravity in that exact working directory.

## What v0.1 does

- Register existing local Git repositories.
- **Local Repository Auto-Discovery:** 1-click folder scanner that auto-detects existing local Git clones across your system and links them directly to their remote GitHub counterparts.
- **Real-Time Sync Status, In-App Pull & Push:** Checks local clone existence, branch, uncommitted changes, and ahead/behind counts with color-coded badges (`✓ Up to date`, `⇣ Behind`, `⇡ Ahead`, `✎ Modified`). Supports 1-click fast-forward pull (`⇣ Pull`) and 1-click push (`⬆ Push`) directly from the remote repository cards.
- **GitHub Repository Browser:** View, search, and filter your remote GitHub repositories in-app via your local GitHub CLI (`gh`). Filter by visibility (private/public) or sync status (up-to-date, needs pull, dirty), view push timestamps, and 1-click clone & register directly into AgentHub.
- Clone GitHub repositories by HTTPS or SSH URL.
- Show current branch, changed-file count, ahead/behind state and origin remote.
- Run `git fetch --prune`, `git pull --ff-only`, and `git push` from the UI.
- Open the repository folder or GitHub remote.
- **Embedded Dynamic Multi-Terminal Cockpit:** Run multiple interactive CLI sessions (Antigravity, Claude Code, Codex, PowerShell, Cmd) side-by-side inside the app using Windows ConPTY + xterm.js. Supports 3+ concurrent terminal panes with draggable splitters, a 1-click **➕ Add Terminal** button, individual **✕ Close** buttons, synchronized working directory selection, and clean ConPTY lifecycle management without duplicated keystrokes or external window sprawl.
- Launch **Codex**, **Claude Code**, or **Antigravity** in Windows Terminal or directly into specific Cockpit terminal panes (T1, T2, T3).
- Display current Codex, Claude and Antigravity usage limits in the Cockpit. AgentHub reads each provider from its cleanest local source — Codex from its on-disk session `rate_limits`, Claude from its usage endpoint, Antigravity from `agy -p /usage` — rather than scraping interactive TUIs. Cards show **percentage left**, reset time, source and refresh state on a user-set polling interval, without storing provider credentials.
- Keep agent commands configurable in `%APPDATA%\AgentHub\settings.json`.
- Create `.agenthub/handoff.md` in a repo to carry context between agents.
- Never stores API keys or provider credentials. Authentication remains owned by each CLI, `gh`, and Git Credential Manager.

## Why this architecture

The Git repository is the durable state. Claude, Codex and Gemini/Antigravity are replaceable executors. AgentHub deliberately shells out to the installed `git`, `gh`, `codex`, `claude`, and `agy` commands instead of depending on provider SDKs. That keeps provider authentication and rapidly-changing CLI behavior outside the app.

## Requirements

- Windows 10/11
- .NET 8 SDK (to build)
- Git on `PATH`
- Optional but recommended:
  - GitHub CLI (`gh.exe`) — for remote repository browsing & authentication
  - Windows Terminal (`wt.exe`) — for external tabbed launches
- Any coding CLIs you want to use on `PATH`, initially:
  - `codex`
  - `claude`
  - `agy`

## Quick start

Run the bootstrap script from the repository root (handles prerequisites verification, building, and launching):

```cmd
.\bootstrap.cmd
```

Or build manually:

```powershell
dotnet restore
dotnet run --project .\src\AgentHub\AgentHub.csproj
```

Release build:

```powershell
dotnet publish .\src\AgentHub\AgentHub.csproj -c Release -r win-x64 --self-contained false
```

## Configuration

On first launch AgentHub creates:

```text
%APPDATA%\AgentHub\settings.json
```

Default agent definitions:

```json
{
  "agents": [
    { "id": "codex", "name": "Codex", "command": "codex", "arguments": "", "enabled": true },
    { "id": "claude", "name": "Claude Code", "command": "claude", "arguments": "", "enabled": true },
    { "id": "antigravity", "name": "Antigravity", "command": "agy", "arguments": "", "enabled": true }
  ],
  "usageProviders": [
    { "id": "gemini", "name": "Antigravity", "command": "agy", "arguments": ["-p", "/usage"], "enabled": true },
    { "id": "codex", "name": "Codex", "command": "codex", "arguments": ["/status"], "enabled": true, "unqualifiedPercentagesAreRemaining": true },
    { "id": "claude", "name": "Claude Code", "command": "claude", "arguments": ["/status"], "enabled": true }
  ],
  "usageRefreshMinutes": 2,
  "preferWindowsTerminal": true
}
```

You can add flags in `arguments`. For example, if you deliberately want an Antigravity profile that skips permission prompts, create a second agent definition rather than hard-coding it into AgentHub.

Usage providers are disabled by default because coding CLIs such as Codex and Claude Code currently expose quota and usage via interactive slash commands (`/status` and `/usage`) rather than non-interactive headless commands. Executing bare CLI calls would consume user token quota or fail with terminal errors. To enable usage tracking, supply a custom script or wrapper command in `settings.json` that outputs quota text and percentages. A successful command prints quota labels and percentages; explicit `left`/`remaining` or `used`/`consumed` wording overrides `unqualifiedPercentagesAreRemaining`.

## Handoff convention

Each repo can contain:

```text
.agenthub/
  handoff.md
```

Before switching agents, record:

- current task
- completed work
- next steps
- relevant files

This is intentionally plain Markdown so every CLI can read it without AgentHub being present.

## Safety choices

- `Pull` uses `--ff-only`. AgentHub will not silently create a merge commit.
- The app does not auto-commit or auto-push in v0.1.
- Removing a repo from AgentHub does **not** delete local files or the GitHub repo.
- Agent permission/autonomy settings remain controlled by the provider CLI.

## Roadmap

See [`docs/ROADMAP.md`](docs/ROADMAP.md). The next major step is **task worktrees + agent sessions**, then provider usage/budget monitoring.

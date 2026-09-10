<p align="center">
  <img src="docs/AgentHub-icon.png" alt="AgentHub" width="128" height="128" />
</p>

<h1 align="center">AgentHub</h1>

A Windows-first local control centre for working with multiple AI coding CLIs across GitHub repositories.

AgentHub treats the **repository as the workspace** and the AI CLI as an interchangeable tool. Add or clone a repo once, inspect Git status, pull/push, then launch Codex, Claude Code, or Google Antigravity in that exact working directory.

## What v0.1 does

- Register existing local Git repositories.
- **Local Repository Auto-Discovery:** 1-click folder scanner that auto-detects existing local Git clones across your system and links them directly to their remote GitHub counterparts.
- **Real-Time Sync Status, In-App Pull & Push:** Checks local clone existence, branch, uncommitted changes, and ahead/behind counts with color-coded badges (`✓ Up to date`, `⇣ Behind`, `⇡ Ahead`, `✎ Modified`). Supports 1-click fast-forward pull (`⇣ Pull`) and 1-click push (`⬆ Push`) directly from the remote repository cards.
- **GitHub Repository Browser:** View, search, and filter every remote repository the signed-in account can see (own, shared, and organisation) via your local GitHub CLI (`gh`). Repos stream in page by page and are grouped by what you are allowed to do: your own, organisation repos you can push to, organisation repos you can only clone or pull, and archived. Each card shows the owner and your GitHub role (Admin, Maintain, Push, or Read-only), the Push button only appears where GitHub will accept it, and anyone can clone anything they can see. Filter by access, visibility (private/public) or sync status (up-to-date, needs pull, dirty), and 1-click clone & register directly into AgentHub.
- Clone GitHub repositories by HTTPS or SSH URL.
- Show current branch, changed-file count, ahead/behind state and origin remote.
- Run `git fetch --prune`, `git pull --ff-only`, and `git push` from the UI.
- Open the repository folder or GitHub remote.
- **Embedded Dynamic Multi-Terminal Cockpit:** Run multiple interactive CLI sessions (Antigravity, Claude Code, Codex, PowerShell, Cmd) side-by-side inside the app using Windows ConPTY + xterm.js. Supports 3+ concurrent terminal panes with draggable splitters, a 1-click **➕ Add Terminal** button, individual **✕ Close** buttons, synchronized working directory selection, and clean ConPTY lifecycle management without duplicated keystrokes or external window sprawl.
- **PowerShell in any folder:** The Cockpit's **📂 PowerShell in Folder…** button opens the Windows folder picker and starts a plain PowerShell session in the chosen directory in a new pane, so you can work outside registered repositories. Each pane also has a 📂 button that launches that pane's selected shell or agent CLI in a folder you pick. The last folder is remembered in `settings.json`.
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
  - GitHub CLI (`gh.exe`) — for remote repository browsing & authentication. Sign in from the GitHub Repos view: AgentHub drives `gh auth login --web`, shows the one-time code and opens the browser; `gh` keeps the token. Multiple accounts can be signed in and switched from the account dropdown.
  - Windows Terminal (`wt.exe`) — for external tabbed launches
- Any coding CLIs you want to use on `PATH`, initially:
  - `codex`
  - `claude`
  - `agy`

## GitHub accounts and permissions

Open **Manage GitHub Repos → GitHub Repos**. The header shows the active GitHub CLI account.

- **Sign in** opens a dialog that starts `gh auth login --web` for you, shows the one-time code, copies it and opens github.com. Approve in the browser; `gh` stores the token. Signing in with another account adds it alongside the existing one. GitHub Enterprise Server hosts can be entered in the dialog.
- **Account dropdown** lists every account `gh` knows about. Picking one runs `gh auth switch` and reloads the repository list. **Sign out** runs `gh auth logout` for the selected account after confirmation.
- **What you see** is every repository the active account can access: your own, ones shared with you, and every organisation you belong to. They stream in as `gh` pages through GitHub.
- **What you can do** is decided by GitHub, not AgentHub. Each card shows your role. Admin, Maintain and Write mean you can push. Read and Triage mean you can clone and pull only. Archived repositories are never pushable. Groups and the access filter reflect the same rule.
- **Git credentials:** switching the active `gh` account does not change what `git push` uses. Run `gh auth setup-git` once if you want git to follow the active `gh` account.

Design notes and gotchas: [docs/GITHUB_INTEGRATION.md](docs/GITHUB_INTEGRATION.md).

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

Release build (produces the branded `AgentHub.exe` with its icon on the shortcut and taskbar):

```powershell
dotnet publish .\src\AgentHub\AgentHub.csproj -c Release -r win-x64 --self-contained false
```

The executable is written to:

```text
src\AgentHub\bin\Release\net8.0-windows\win-x64\publish\AgentHub.exe
```

Right-click it → **Pin to taskbar**, or **Send to → Desktop** to create a shortcut. Both pick up the AgentHub icon automatically.

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

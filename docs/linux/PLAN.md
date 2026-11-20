# AgentHub headless on Ubuntu: plan

Status: **slices 1 (Core split) and 2 (session host on Linux) built, nothing runs headless yet.** Written 2026-11-18. This file sits in `docs/linux/` on purpose: the Windows app packs `docs/*.md` (top level only) into its helper's knowledge, and a plan must not make the helper think Linux support exists.

## Goal

A colleague runs AgentHub on an Ubuntu computer they reach over Secure Shell (SSH). There are no windows. Claude Code, running in their SSH session, drives AgentHub as a tool. **Everything AgentHub does on Windows, it does here; only the screens are gone.** The owner (the person) still answers everything AgentHub or an orchestrator asks the owner.

## Decisions

| # | Decision | From |
|---|---|---|
| 1 | Same functionality as the Windows app, without a user interface (UI). | owner |
| 2 | Questions to the owner are answered by the owner, never by the Claude session driving AgentHub. | owner |
| 3 | One user per Ubuntu computer. | owner |
| 4 | The data folder is chosen by the user at setup: the Claude Code install steps ask where it goes (suggesting `~/agenthub-data`). | owner |
| 5 | Install the simplest way for the user and the build: Ubuntu's own `dotnet-sdk-8.0` package from `apt` (it brings the runtime), build from the clone, publish into an install folder. Same flow as Windows. No self-contained build, no Microsoft package feed. | owner, choice made here |
| 6 | **The Windows app must not change.** See [Keeping Windows unchanged](#keeping-windows-unchanged). | owner |
| 7 | One executable, `agenthub`: `agenthub daemon` (the background service), `agenthub <command>` (the command-line tool, CLI), `agenthub mcp` (the Model Context Protocol (MCP) server Claude Code talks to). | proposed |
| 8 | Work lands on `main` in slices, one branch and pull request each, not on a long-lived Linux branch. | proposed |

## Shape

```text
Ubuntu computer, one user
│
├── agenthub daemon        systemd user service; lingering on, so it outlives the SSH login
│     │                    owns: sessions, orchestrators, runs, usage polling, history archive
│     │                    socket: $XDG_RUNTIME_DIR/agenthub/daemon.sock (folder mode 0700)
│     │
│     └── AgentHub.SessionHost (one per session, as on Windows)
│            pseudo-terminal (Linux) in place of ConPTY; Unix socket in place of the named pipe
│            same frames (SessionFrame), same replay buffer, same records
│
├── agenthub <command>     the person over SSH: every action, text or --json; `attach` to type into a session
│
└── agenthub mcp           stdio MCP server, registered with `claude mcp add`; Claude Code's tools
```

On Windows nothing of this runs: the WPF (Windows Presentation Foundation) app keeps hosting sessions and orchestrators itself, as now.

### What already runs on Linux

`TemplateRun`, `RunStore`, `AdvancedTemplate`, `TemplateEditing`, `TemplateHelper`, `ClaudeJsonSession`, `OrchestratorWorkflow`, the Timeline builder and reviews, the history readers and archiver logic, the usage readers (`~/.claude/.credentials.json`, `~/.codex/sessions`, `agy -p /usage`), `GitService`, `GitHubService`, `SessionFrame`, `ReplayBuffer`, `SessionStore` (apart from its default path). They shell out to `git`, `gh` and `claude` or read files; nothing Windows-specific.

### What is Windows-bound

| Where | What | Linux answer |
|---|---|---|
| `AgentHub.Hosting/ConPtyNative.cs`, `ConPtySession.cs` | Win32 pseudo-console | `UnixPty`: `openpty`, then `posix_spawn` with `POSIX_SPAWN_SETSID` and file actions opening the pty's slave side as standard input, output and error (it becomes the controlling terminal). No `fork` from managed code. Resize by `ioctl(TIOCSWINSZ)`. A spike proves it first. |
| `AgentHub.SessionHost/Program.cs` | `NamedPipeServerStream` | `UnixDomainSocketEndPoint` under `$XDG_RUNTIME_DIR/agenthub/`. The protocol is stream-based, so only the transport changes. |
| `SessionStore.DefaultRoot`, `HistoryArchiver.DefaultRoot`, runs, timeline, host copies | `C:\data\agenthub\…` | Under the data folder chosen at setup. Windows defaults stay as they are. |
| `SettingsService` | `%APPDATA%\AgentHub` | `~/.config/agenthub` (`$XDG_CONFIG_HOME`). |
| `SessionHostService` | live check by process name `AgentHub.SessionHost` | Same check, Linux process name. |
| `ContextWindowTracker`, `HistoryArchiver` | `DispatcherTimer` | A timer that posts to the caller's synchronisation context: the UI thread on Windows (same behaviour), the daemon's loop on Linux. |
| `GitHubRepository`, `RepositoryDefinition`, `UsageDisplayModels` | WPF `Color` / `Brush` in models | Colours move to the WPF side; models keep plain data. |
| `PaneBuildWorker`, `TerminalPaneControl`, `EmbeddedTerminalControl` | Session logic inside WPF controls: launch, turn ends, attention, folder trust, transcript finding, context badge | Moved into a UI-free `SessionController` in Core. On Windows the controls call it (same behaviour); on Linux the daemon does. |
| `AgentLauncher`, `SshConnection.BuildCommandLine`, `RepoContextService` | `wt.exe`, `powershell.exe`, `explorer.exe`, `notepad.exe` | Linux: a shell session (`$SHELL`), `ssh` run directly, paths printed, `$EDITOR`. |
| `SessionFolderDiscovery`, `SessionContextReader` | Claude Code project folder names from Windows paths; AppData left out | Decode Linux paths (`/home/x/repo` → `-home-x-repo`); leave out `~/.cache` and temporary folders. |

## Owner-only actions

Rule: **anything an orchestrator asks the owner, and every confirmation the Windows app asks the owner, is owner-only.** Everything else Claude may do through MCP.

Owner-only:

- answering an orchestrator's question, and messaging an orchestrator (the orchestrator takes those words as the owner's);
- confirming what Windows confirms: replacing a busy session, closing or relaunching a running session, stop all, and the rest found from the `MessageBox` confirmations in slice 5;
- GitHub sign-in (the device code is entered in the owner's browser anyway);
- applying reviewed changes to an instruction file (`CLAUDE.md` and the like).

How it is kept:

- **The MCP server has no tool for these.** Claude can read pending questions (`questions_list`) and ask for a confirmation; the reply tells Claude to tell the owner the exact command to run.
- **Owner commands need a person at a terminal:** `agenthub answer`, `agenthub tell`, `agenthub confirm` prompt for the text or "yes" on the terminal and refuse when standard input is not a terminal. Claude Code's Bash tool has none.
- **Setup adds Claude Code deny rules** for those commands.
- Honest limit: Claude Code runs as the same Linux user, so this is a strong guard rail, not a security boundary. The same is true on Windows today.

A run waiting on the owner shows as paused with its question in `agenthub status`, `agenthub questions` and the MCP `runs_list`, the way the pane box shows it on Windows.

## Every Windows action and its headless form

"Visual" rows are layout or decoration with no function; they are dropped. Say if one of them matters.

### Repositories (📁 Local Repos)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| 🔍 Scan folder for repos… | `repos scan <folder>` | `repos_scan` |
| Add existing repo | `repos add <folder>` | `repos_add` |
| Clone from GitHub | `repos clone <owner/name> [folder]` | `repos_clone` |
| 🔍 Auto-Detect Local | `repos detect` | `repos_detect` |
| 🔄 Check Sync | `repos sync-check` | `repos_sync_check` |
| Open folder / GitHub | `repos show <repo>` (prints path and web address) | `repos_show` |
| Remove | `repos remove <repo>` | `repos_remove` |
| ↻ Refresh Git status | `git status <repo>` | `git_status` |
| Fetch / Pull (fast-forward) / Push | `git fetch\|pull\|push <repo>` (pull stays fast-forward only) | `git_fetch`, `git_pull`, `git_push` |
| ⚡ Launch in T1/T2/T3 | `sessions start --repo <repo> --cli <cli> [--slot N]` | `sessions_start` |
| External Window | not applicable (Windows Terminal); `attach` replaces it | none |
| Open handoff | `repos handoff <repo>` (prints `.agenthub/handoff.md`) | `repos_handoff` |

### GitHub (🐙 GitHub Repos)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| Sign in (device code) / Use terminal instead | `github login [--host]` (prints code and web address) | owner only |
| Account list / switch | `github accounts`, `github switch <login>` | `github_accounts`, `github_switch` |
| Sign out | `github logout <login>` | owner only |
| Every repository visible, grouped by access, filtered | `github repos [--filter] [--group]` (streamed) | `github_repos` |
| Card: Pull / Push / ⚡ Cockpit / Clone or open / 🌐 GitHub | `git pull\|push`, `sessions start`, `repos clone`, `repos show` | as above |

### Cockpit sessions

| Windows | `agenthub` | MCP tool |
|---|---|---|
| ➕ Add Terminal / pane launcher: ① folder (filter, found folders, browse), ② CLI, ③ new or resume a past session | `sessions start --folder <path> --cli <cli> [--resume <id>]`; `sessions folders` lists found folders; `sessions past <folder>` lists resumable sessions | `sessions_start`, `sessions_folders`, `sessions_past` |
| 📂 PowerShell in Folder… | `sessions start --folder <path> --shell` (`$SHELL`) | `sessions_start` |
| 🔐 SSH connections and each pane's SSH button | `ssh list\|add\|remove`, `sessions start --ssh <name>` | `ssh_list`, `sessions_start` |
| Typing into a session | `attach <session>` (detach key leaves it running; replay on attach, as on Windows) | `sessions_send` (guarded: refused at a security check and while an orchestrator owns the session, as on Windows) |
| Reading a session | `sessions output <session> [--since <offset>]` | `sessions_read` |
| Relaunch / Restart / Stop / ✕ Close | `sessions relaunch\|restart\|stop\|close <session>` | same; confirmation by the owner when the session runs |
| ■ Stop all | `sessions stop-all` | asks the owner to confirm |
| Last prompt, Copy | `sessions show <session>` | `sessions_show` |
| Context badge (context window use) | in `sessions show` and `status` | in `sessions_show` |
| Attention flash (a CLI waits for input) | `status`, `events --follow` | `sessions_wait` (returns when a session needs attention or a turn ends) |
| Folder trust / permission prompt on screen | flagged in `status` | flagged in `sessions_show` |
| Sessions outlive AgentHub, reattach on start | the daemon reattaches to live hosts on start | none needed |
| ← Back to the last session's output | `sessions output <ended session>` | `sessions_read` |
| ◀ ▶ move pane, pane sizes, window placement | visual | none |

### Orchestrator (🧭 Orchestrate on a pane)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| Orchestrator with no preset context / with a template's context | `orch attach <session> [--template <workflow>]` | `orch_attach` |
| Follow an advanced template | `orch attach <session> --advanced <template> --project <folder>` | `orch_attach` |
| Resume a saved run on this pane | `runs resume <run> <session>` | `runs_resume` |
| Message box (owner talks to the orchestrator) | `tell <session>` (prompts for the text) | owner only |
| Orchestrator's messages and questions | `orch log <session> [--follow]`, `questions` | `orch_log`, `questions_list` |
| Answer a question | `answer <question>` (prompts for the text) | owner only |
| ✕ Detach | `orch detach <session>` | `orch_detach` |
| Hand-off threshold field | `orch threshold <session> <0.40–0.95>` | `orch_threshold` |
| ◨ Tree / 🗺 Run | `runs tree <run>` | `runs_tree` |

### Templates (📚 Templates)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| List and read | `templates list`, `templates show <id>` | `templates_list`, `templates_show` |
| ⧉ Duplicate / ➕ New / 💾 Save / 🗑 Delete | `templates copy\|new\|save\|delete` (`save` takes a file; local templates stay in `workflows/local`) | same; delete asks the owner |

### Advanced templates and runs (🗺, ⏯ Runs)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| A project's templates as trees | `adv list <project>`, `adv tree <project> <template>` | `adv_list`, `adv_tree` |
| ➕ Add the staged build example | `adv example <project>` | `adv_example` |
| Edit (draft): add stage or step, step below, move up or down, remove, edit fields, escalation point | `adv edit <project> <template> <operation> …` (each operation as in `TemplateEditing`; writes the draft) | `adv_edit` |
| 💾 Save / 🗑 Discard / ✓ Done | `adv save\|discard <project> <template>` | `adv_save`; discard asks the owner |
| Template helper: ask, new chat, build from a plan | `adv helper <project> [--new] "<message>"` | `adv_helper` |
| ▶ Go / ⏸ Pause / ▶ from here | `runs go\|pause <run>`, `runs from <run> <step>` | `runs_go`, `runs_pause`, `runs_from` |
| ⤵ Apply to this run | `runs apply <run>` | `runs_apply` |
| Runs list (live and saved, progress, reason) | `runs list` | `runs_list` |
| ▶ Resume… (pick a session) | `runs resume <run> <session>` | `runs_resume` |
| 🗑 Delete | `runs delete <run>` | asks the owner |

### History (🕘)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| Sessions per repository and found folders, re-scan | `history sessions [<folder>]` | `history_sessions` |
| ➕ Add to repositories | `repos add <folder>` | `repos_add` |
| Entries with filters (you, replies, thinking, tool calls, results, events), search, expand | `history show <session> [--only prompts,replies,…] [--search <text>]` | `history_show` |
| Follow live / ↓ Latest | `history show <session> --follow` | `history_show` with `since` |
| ⤓ Export Markdown | `history export <session> <file>` | `history_export` |
| 📄 Transcript file | `history show <session> --path` | in `history_show` |

### Timeline (🌳)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| Timeline of a repository (lanes, sessions, commits) | `timeline <repo> [--json]` (text tree: lanes and cards in time order) | `timeline_show` |
| ✨ You & Claude / ✨ GitHub cycle reviews | `timeline review sessions\|github <repo>` | `timeline_review` |
| Open report / What was sent | `timeline report <repo> <kind> [--digest]` | `timeline_report` |
| Add to CLAUDE.md… | `context review` with the report's rules, then the owner applies | owner applies |
| 🕘 Open in History | `history show <session>` | `history_show` |

### Context (📄 Context, 📁 Repo context)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| View a CLI's user-wide or a folder's instruction file | `context show <cli> [--folder]` | `context_show` |
| Edit, Save, ↻ Reload, Open folder | `context edit <cli> [--folder]` (`$EDITOR`; same safe save: atomic, refuses a file changed meanwhile) | none: Claude edits files itself |
| ✨ Review: changes with Apply boxes, Apply review, Discard review | `context review <cli> [--folder]` lists changes; `context apply <review> <ids>` (prompts) | `context_review`; applying is owner only |
| Context helper: Suggest, example chips | `context suggest <cli> "<rule>"` | `context_suggest` |

### Usage (📊)

| Windows | `agenthub` | MCP tool |
|---|---|---|
| Provider cards (% left, resets, source) | `usage [--refresh]` | `usage_get` |
| Interval, hide or show cards | `config set UsageRefreshMinutes …`, `config set HiddenUsageCards …` | `config_set` |
| Terminal Feed (raw source, sanitised or raw, copy) | `usage --raw` | in `usage_get` |

### Settings, helper, decoration

| Windows | `agenthub` | MCP tool |
|---|---|---|
| ⚙ Settings (`settings.json` in Notepad) | `config show\|get\|set\|edit` | `config_get`, `config_set` |
| The AgentHub helper (crab), answers from the docs | `help "<question>"` | `help_ask` |
| Animated background, crab animations, dark title bar, colours | visual | none |
| `PreferWindowsTerminal` | Windows only | none |

## Keeping Windows unchanged

Every slice, before its pull request:

- The Windows build: `dotnet build AgentHub.sln -c Release`, 0 errors.
- All unit tests pass, live tests with `AGENTHUB_LIVE_REVIEW=1` where a slice touches them, and the interface tests in `tests/ui/*.ps1`, screenshots looked at.
- Windows paths, settings location, pipe names, the session protocol and its version stay as they are, so hosts started by the installed app keep working with a newer one and back.
- Moves are moves: code moved into Core keeps its behaviour, pinned by tests written before the move where none exist.
- Linux-only code lives in Linux-only types behind `OperatingSystem.IsLinux()`, or in projects the WPF app does not reference.
- The taskbar install is never updated without asking (`AGENTS.md`).

Expected Windows-visible differences: none. The publish folder gains `AgentHub.Core.dll`; a new host build is copied to `C:\data\agenthub\host\<hash>` on first use, as on any update.

## Slices

Each is one branch from the updated `main` and one pull request, with README, roadmap and handoff updated in it.

| # | Slice | Done when |
|---|---|---|
| 0 | This plan | merged |
| 1 | **Core split.** `AgentHub.Core` (`net8.0`): UI-free services and models moved; timers and colours untangled; `AgentHub.Core.Tests` (`net8.0`). `AgentHub.Linux.slnf` solution filter with only the projects that build on Linux. Ubuntu job in `.github/workflows/build.yml` builds the filter and runs the Core tests. | Windows unchanged (checks above); Ubuntu job green |
| 2 | **Portable session host.** `IPty` (ConPTY, Unix pseudo-terminal), `ISessionTransport` (named pipe, Unix socket); `AgentHub.Hosting` and `AgentHub.SessionHost` on `net8.0`; configurable data root. Linux twin of `SessionHostLiveTests` (bash in a folder whose path has spaces: attach, type, detach, reattach from an offset, resize, exit). | Live tests pass on Windows and on the Ubuntu job |
| 3 | **Session controller.** Pane session logic out of `TerminalPaneControl` / `EmbeddedTerminalControl` into Core `SessionController`; `PaneBuildWorker` becomes a thin adapter; `HostedBuildWorker` for the daemon. Highest Windows risk: every interface test. | Orchestrator tests pass against both workers |
| 4 | **Daemon and CLI.** `agenthub daemon` (systemd user unit, `KillMode=process` so a daemon restart leaves sessions running, as closing AgentHub does), the socket API, every CLI row above, `attach`, owner commands. | A scripted run end to end on Ubuntu: start a session, attach an orchestrator, follow a template, answer a question by hand |
| 5 | **MCP server.** `agenthub mcp`, the tools above, owner confirmations; MCP C# software development kit (SDK) version checked first. | Claude Code drives a run through MCP; owner-only tools absent |
| 6 | **Install and release.** `CLAUDE.md` section "Installing on Ubuntu" (asks for the data folder; `apt install dotnet-sdk-8.0 git gh`; build, test, publish; systemd unit; `loginctl enable-linger`; `claude mcp add`; deny rules), `scripts/install-linux.sh` doing the same non-interactively, README section, version. | A fresh Ubuntu 24.04 install by Claude Code from a clone, by someone who has not seen it before |

## What the colleague needs

- Ubuntu 22.04 or 24.04, reached over SSH; permission to run `loginctl enable-linger` for their user (or an administrator who does it once).
- `git`, `gh` and `claude` installed and signed in. Both sign-ins work over SSH: they print a code and a web address to open on their own computer.
- `codex` and `agy` if they use them.
- Nothing graphical.

## Open

- Version number and scheme for the release (no assembly version is set today; the taskbar check reads the commit stamp, which must keep working).
- Confirm `dotnet-sdk-8.0` is in the Ubuntu archive for 22.04 and 24.04 before slice 6 relies on it.
- The `posix_spawn` pseudo-terminal spike (slice 2) is the main technical unknown.

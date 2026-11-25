# AgentHub Agent Instructions

Read `README.md`, `docs/ARCHITECTURE.md`, and `docs/ROADMAP.md` before making architectural changes. Read `docs/GITHUB_INTEGRATION.md` before touching sign-in, accounts or the repository browser.

Picking up work in progress: read `docs/HANDOFF.md` first (where the current piece of work stands, what was decided, what is next), and rewrite it at the end of the session. It is committed so that a session on another computer can carry on from a clone; `.agenthub/handoff.md` is git-ignored here and stays on one computer.

## Product intent

AgentHub is a Windows-first personal control centre for GitHub repositories and interchangeable coding CLIs. The repository/task is the durable workspace; Codex, Claude Code, Antigravity, and future agents are executors that can be swapped.

## Engineering rules

- Target Windows with .NET 8 WPF unless a migration is explicitly chosen.
- Keep agent commands configurable in settings.
- Do not store API keys or provider credentials.
- Prefer the user's installed `git`/credential manager rather than reimplementing Git authentication.
- GitHub access goes through `gh` only, driven non-interactively (stdin closed, explicit flags) and parsed from its output. Never implement an OAuth flow or token store in AgentHub. See `docs/GITHUB_INTEGRATION.md`.
- Push/pull rights come from GitHub's `viewerPermission` for the signed-in account, never inferred from ownership, naming or local state. Cloning is always allowed for anything visible.
- Use the GraphQL `viewer.repositories` query (all affiliations) to list repositories; `gh repo list` without an owner returns only personally owned repos.
- Pull remains fast-forward-only unless the user explicitly chooses another strategy.
- Treat destructive Git operations as explicit user actions.
- Do not let two autonomous agents edit the same working tree by default. Parallel tasks belong in Git worktrees.
- Never replace a running Cockpit session without an explicit confirmation. New work goes to a new pane or an idle one; when a user targets a busy pane, ask first (see the folder-launch handlers in `MainWindow.xaml.cs`).
- Cockpit sessions may run in any folder, not just registered repositories. Pass the folder as the ConPTY working directory; do not embed paths in command lines.
- `.agenthub/handoff.md` is human-readable shared context and must remain usable without AgentHub.
- Usage/quota estimates must identify their source and must not imply exact provider balances when they are inferred.

## Before finishing a change

- Build the solution.
- Exercise affected Git command paths with spaces in repository paths.
- Keep README/roadmap updated when behavior changes.

## Updating the app the user runs

The pinned taskbar icon launches a separate install folder. `git pull`, `dotnet publish` and launching the publish output do not change it, so none of them count as updating the app.

- Update it only with `scripts\Deploy-Taskbar.cmd -CloseRunning` (the `.cmd` gets past a blocked PowerShell execution policy). It finds the install folder from the taskbar pin, whatever the pin is called; on this owner's computer that is `C:\Apps\AgentHub`. Never make a desktop shortcut or ask the user to pin it again when a pin exists.
- When the user asks to rebuild, install, deploy or "put on the taskbar", that request is the go to close the running app: run it, don't ask again. Ask first only when the deploy was not requested (say, offered at the end of other work).
- Never tell the user they have the latest AgentHub until `scripts\Deploy-Taskbar.ps1 -Check` prints `UP TO DATE`, and say which commit is installed.
- When "latest" looks wrong, compare the commit stamped in the installed `AgentHub.dll` with `origin/main` before searching branches.

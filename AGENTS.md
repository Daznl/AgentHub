# AgentHub Agent Instructions

Read `README.md`, `docs/ARCHITECTURE.md`, and `docs/ROADMAP.md` before making architectural changes. Read `docs/GITHUB_INTEGRATION.md` before touching sign-in, accounts or the repository browser.

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

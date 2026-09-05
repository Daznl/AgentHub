# AgentHub Handoff Context

**Generated:** 2026-09-05  
**Current Branch:** `main`  
**Current Milestone:** Multi-Session Cockpit & GitHub Hub (v0.1.5)

---

## 1. Current State of the System

AgentHub is a Windows-first personal control centre built with **.NET 8 WPF** that manages local and remote GitHub repositories and hosts interchangeable AI coding CLIs (Codex, Claude Code, Google Antigravity, PowerShell, and Cmd).

### Key Subsystems

1. **Local Repositories View (`RepoViewGrid`)**:
   - Lists registered local repositories with real-time Git sync state badges (`✓ Up to date`, `⇣ Behind`, `⇡ Ahead`, `✎ Modified`).
   - Detailed repository inspector with branch name, dirty file count, ahead/behind counts, origin URL, and raw git status output.
   - 1-click Git actions: `Fetch`, `Pull (fast-forward-only)`, and `Push`.
   - Quick launch agent into Cockpit terminals (`⚡ Launch in T1`, `⚡ Launch in T2`, `⚡ Launch in T3`) or external Windows Terminal (`wt.exe`).
   - Automated folder scanning (`🔍 Scan for repos`) using [`RepoDiscoveryService.cs`](file:///C:/Users/danie/Desktop/AgentHub/src/AgentHub/Services/RepoDiscoveryService.cs) that detects repositories up to 2 levels deep and imports them.

2. **Remote GitHub Browser (`GitHubViewGrid`)**:
   - Queries repositories directly via the local `gh` CLI ([`GitHubService.cs`](file:///C:/Users/danie/Desktop/AgentHub/src/AgentHub/Services/GitHubService.cs)).
   - Displays authenticated account status and repository counts.
   - Search/filter by name/description, visibility (All, Public, Private), and sync state (Up to date, Needs Pull, Ahead, Uncommitted Changes, In AgentHub, Not in AgentHub).
   - 1-click Fast-Forward Pull (`⇣ Pull`) directly from remote cards when local is behind.
   - 1-click Push (`⬆ Push`) directly from remote cards when local branch is ahead.
   - 1-click Clone & Register for remote repositories not yet added locally.
   - Auto-detection linking local folders to GitHub remotes.

3. **Multi-Session Cockpit (`CockpitViewGrid`)**:
   - Dynamic N-terminal multiplexer running side-by-side sessions (defaults to 3 panes).
   - Dynamic pane management: `➕ Add Terminal` button (supports up to 6 concurrent sessions) and individual `✕` close buttons per pane.
   - Draggable horizontal splitters (`GridSplitter`) between all panes.
   - Per-pane repository selection (`RepoCombo`) and shell selection (`ShellCombo`).
   - Session hosting powered by Windows ConPTY ([`ConPtySession.cs`](file:///C:/Users/danie/Desktop/AgentHub/src/AgentHub/Terminal/ConPtySession.cs), [`ConPtyNative.cs`](file:///C:/Users/danie/Desktop/AgentHub/src/AgentHub/Terminal/ConPtyNative.cs)) and Microsoft WebView2 + xterm.js ([`EmbeddedTerminalControl.xaml.cs`](file:///C:/Users/danie/Desktop/AgentHub/src/AgentHub/Terminal/EmbeddedTerminalControl.xaml.cs), [`terminal.html`](file:///C:/Users/danie/Desktop/AgentHub/src/AgentHub/Terminal/Assets/terminal.html)).
   - Top navigation button labeled `⚡ Cockpit`.

---

## 2. Recent Bugfixes & Improvements

- **Navigation Button Label:** Updated top navigation button from `⚡ Cockpit (Dual Terminal)` to `⚡ Cockpit` in [`MainWindow.xaml`](file:///C:/Users/danie/Desktop/AgentHub/src/AgentHub/MainWindow.xaml).
- **Terminal Duplicate Typing Fix:** Resolved an issue where typed characters were duplicated when interacting with Cockpit terminals.
  - **Root Cause:** WPF `Loaded` events fired multiple times during tab switching and pane layout re-evaluations while `EnsureCoreWebView2Async` was completing. This resulted in duplicate subscriptions to `WebView.CoreWebView2.WebMessageReceived`, causing each character sent via `term.onData` to be dispatched multiple times to `_session.Write(data)`.
  - **Resolution:** Added `_isInitialized` and `_isInitializing` guards, enforced single-subscription idempotency (`-= CoreWebView2_WebMessageReceived; += CoreWebView2_WebMessageReceived;`), and cleaned up pseudoconsole pipe handles safely in `ConPtySession.cs`.
- **Repo Combo Synchronization:** When a session is launched into a terminal pane from the main repo detail view, the pane's repository combobox now automatically syncs with the launched repository.

---

## 3. Key Architecture & Coding Constraints

1. **WPF & WinForms Namespace Disambiguation:**
   - Both `<UseWPF>true</UseWPF>` and `<UseWindowsForms>true</UseWindowsForms>` are enabled in `AgentHub.csproj` (needed for `FolderBrowserDialog`).
   - Files referencing UI types must explicitly alias them:
     - `using UserControl = System.Windows.Controls.UserControl;`
     - `using Color = System.Windows.Media.Color;`
     - `using MessageBox = System.Windows.MessageBox;`
     - `using Forms = System.Windows.Forms;`
2. **Build File Locking:**
   - Always terminate running instances of `AgentHub.exe` (`Get-Process -Name "AgentHub" -ErrorAction SilentlyContinue | Stop-Process -Force`) before running `dotnet build`.
3. **Git & CLI Safety:**
   - Pull operations must always use fast-forward only (`--ff-only`).
   - Destructive Git operations must never execute without explicit user action.
   - Quota or usage metrics must display their confidence source and never imply exact provider balances.

---

## 4. Next Priorities (Roadmap Alignment)

- **Cockpit Stage 2 (CLI Compatibility Verification):**
  - Verify interactive behavior and full ANSI/VT escape sequences with Codex, Claude Code, and Antigravity (`agy`) across multiple repos.
- **Cockpit Stage 3 (Attention & Bell Engine):**
  - Visual badges / audio chimes when a CLI requires interactive user confirmation or encounters an error.
- **Milestone v0.2 (Task Worktrees):**
  - Automatic creation of Git worktrees per task to isolate agent file edits and avoid parallel conflict in the same working tree.

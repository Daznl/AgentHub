# Multi-Session Embedded Cockpit Architecture & Staging Plan

## 1. Key Architectural Requirements

### 1.1 Multi-Session Orchestration
- **Concurrent Session Management:** AgentHub concurrently manages, monitors, and interacts with multiple running agent CLI sessions (e.g., Antigravity `agy`, Claude Code `claude`, OpenAI Codex `codex`, or PowerShell scripts) across separate repositories or worktrees.
- **Durable Session Lifecycle:** Each session transitions through explicit states:
  - `Starting` → `Running` → `NeedsAttention` (awaiting user confirmation) → `Completed` | `Failed` | `Terminated`.
- **Session Metadata:** Track for every session:
  - Unique session ID, repository ID, target branch, and isolated working directory path.
  - Active agent definition (binary, arguments, working tree).
  - Timestamps (start, last active, completion), process ID, and exit codes.
  - Live buffer summary and unread attention flags.

### 1.2 Workspace & Worktree Isolation (Safety Invariant)
- **Zero In-Place Collisions:** In accordance with AgentHub engineering principles, two autonomous agents must **never** edit the same working directory concurrently.
- **Automated Git Worktrees:** When multiple sessions target the same repository, AgentHub automatically provisions isolated Git worktrees (`git worktree add -b agent/<task-name> <worktree-path> <base-branch>`).
- **Safe Teardown:** Prevent deletion or archival of dirty worktrees (uncommitted diffs or unstaged changes).

### 1.3 Embedded Terminal Multiplexer (Eliminating Window Sprawl)
- **Zero External Window Sprawl:** Replace detached `wt.exe` / `powershell.exe` pop-up windows with fully embedded terminal surfaces inside the unified WPF application.
- **Native ConPTY Integration:** Terminal processes are executed using the Windows Pseudo Console (ConPTY) subsystem (`CreatePseudoConsole`).
  - Full VT100 / ANSI escape sequence support (cursor navigation, 24-bit color, screen clearing, alternate buffer).
  - True TTY compliance (`isatty == true`), ensuring agent CLIs activate interactive mode instead of falling back to headless scripts.
  - Bidirectional streaming: keystrokes (arrows, tab completion, multi-line, Ctrl+C interrupt) sent to `InPipe`; ANSI rendering stream received from `OutPipe`.
- **Dynamic Terminal Sizing:** Handle terminal resizing smoothly via `ResizePseudoConsole` synchronized with WPF viewport layout changes.

### 1.4 Attention & Notification Engine
- **Attention Detection:** Continuous analysis of terminal output streams to detect when an agent is blocked awaiting user input:
  - Terminal Bell (`\a` / `0x07`).
  - Common confirmation heuristics: `[y/N]`, `(Y/n)`, `Allow [Y]es / [N]o`, `Press Enter to continue`.
  - Input-idle state: Agent process active, no stream output for $> N$ seconds, cursor stationary at input prompt.
- **Visual Alerting:**
  - Sidebar session indicators: Pulsing green (executing), flashing amber bell (attention needed), solid blue (done), red (failed).
  - Windows Toast / Taskbar badge update when AgentHub is backgrounded or minimized.

### 1.5 Layout Flexibility & Multiplexing UI
- **Split / Grid View:** Support viewing multiple active sessions concurrently (1x1 full, 1x2 horizontal split, 2x2 grid).
- **Master-Detail Sidebar:** A quick session list showing repo name, branch, active agent, duration, and status badge with 1-click focus switching.

---

## 2. System Architecture Design

### 2.1 Component Architecture

```text
┌────────────────────────────────────────────────────────────────────────┐
│                          AgentHub WPF Client                           │
│                                                                        │
│   ┌─────────────────────┐  ┌───────────────────────────────────────┐   │
│   │   SessionSidebar    │  │       TerminalGrid / Multiplexer      │   │
│   │                     │  │                                       │   │
│   │ [●] Repo A (agy)    │  │ ┌─────────────────┬─────────────────┐ │   │
│   │ [🔔] Repo B (claude) │  │ │ Embedded Term 1 │ Embedded Term 2 │ │   │
│   │                     │  │ └────────▲────────┴────────▲────────┘ │   │
│   └──────────┬──────────┘  └──────────┼─────────────────┼──────────┘   │
│              │                        │ I/O Events      │              │
└──────────────┼────────────────────────┼─────────────────┼──────────────┘
               │                        │                 │
    ┌──────────▼──────────┐   ┌─────────▼────────┐ ┌──────▼──────────┐
    │   SessionService    │   │  ConPtySession 1 │ │ ConPtySession 2 │
    │  & AttentionDetector│   └─────────▲────────┘ └──────▲──────────┘
    └──────────┬──────────┘             │ Pipes           │ Pipes
               │              ┌─────────▼────────┐ ┌──────▼──────────┐
    ┌──────────▼──────────┐   │  Antigravity CLI │ │ Claude Code CLI │
    │   WorktreeService   │   │  (in Worktree A) │ │ (in Worktree B) │
    └─────────────────────┘   └──────────────────┘ └─────────────────┘
```

### 2.2 Core Data Models

```csharp
public enum SessionStatus
{
    Starting,
    Running,
    NeedsAttention,
    Completed,
    Failed,
    Terminated
}

public sealed class AgentSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TaskTitle { get; set; } = string.Empty;
    public string RepositoryId { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public bool IsWorktree { get; init; }
    public AgentDefinition Agent { get; init; } = default!;
    
    public SessionStatus Status { get; set; } = SessionStatus.Starting;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public int? ProcessId { get; set; }
    public bool HasUnreadAttention { get; set; }
}
```

### 2.3 Launching a shell in an arbitrary folder (shipped v0.1.6)

Cockpit sessions were originally tied to the pane's **repository dropdown**: the working directory
was always a registered repo's `LocalPath` (or the process CWD when none existed). Users needed a
plain PowerShell in any folder without registering it as a repository first.

```text
Cockpit toolbar "📂 PowerShell in Folder…"          Pane toolbar "📂"
        │                                                  │  (event: FolderLaunchRequested)
        ▼                                                  ▼
MainWindow.OpenPowerShellInFolder_Click        MainWindow.OnPaneFolderLaunchRequested
        │                                                  │  confirm if pane.IsRunning
        └──────────────► PickWorkingFolderAsync ◄──────────┘
                          │  Forms.FolderBrowserDialog (Vista-style Explorer picker)
                          │  starts at settings.LastTerminalFolder, else Desktop
                          │  persists the chosen folder (best-effort SaveAsync)
                          ▼
              new pane (<6) or first idle pane      pane.StartSession(pane.SelectedShell ?? PowerShell,
              StartSession("powershell.exe -NoLogo",                  folder, "Terminal N · <shell>", <folder name>)
                           folder, "Terminal N · PowerShell", <folder name>)
                          ▼
              TerminalPaneControl.StartSession -> EmbeddedTerminalControl -> ConPtySession (cwd = folder)
```

Behaviour and rules:

- **Toolbar button = always plain PowerShell** (`powershell.exe -NoLogo`), in a **new** pane. When the
  6-pane cap is reached the first pane with no running process is reused; if every pane is busy the
  user is told to close one or use the per-pane button. A running agent session is never replaced
  silently.
- **Per-pane 📂 = that pane's selected shell/agent** (PowerShell, cmd, Codex, Claude Code, Antigravity
  as configured in settings). If the pane is running, a Yes/No prompt precedes replacement.
- **Folder memory:** `AppSettings.LastTerminalFolder` is the only new persisted field. It is
  validated with `Directory.Exists` before use so a deleted folder falls back to the Desktop.
- **Repo dropdown stays consistent:** `TerminalPaneControl.StartSession` already selects the matching
  registered repository when the chosen folder equals a repo's `LocalPath`; otherwise the dropdown is
  left as-is and the header shows the folder name as the subtitle.
- **Paths with spaces** (e.g. `OneDrive - IGO Limited\Projects\…`) are passed to ConPTY as the
  working directory, not embedded in a command line, so no quoting is involved.

Code: `MainWindow.xaml.cs` (`PickWorkingFolderAsync`, `FolderDisplayName`,
`OpenPowerShellInFolder_Click`, `OnPaneFolderLaunchRequested`, `PlainPowerShellCommand`),
`Terminal/TerminalPaneControl.xaml(.cs)` (`FolderLaunchRequested` event, `SelectedShell`,
`LaunchInFolder_Click`), `Models/AppSettings.cs` (`LastTerminalFolder`).

Not done: no most-recently-used folder list (single last folder only); no drag-and-drop of a folder
onto a pane; the picker is modal (WinForms `FolderBrowserDialog`), which is consistent with the
existing Scan/Add/Clone dialogs.

---

## 3. Build & Implementation Plan

### Phase 1: Session Management & Git Worktree Engine
- Implement `ISessionService` and `SessionManager` in `AgentHub.Services`.
- Implement `WorktreeService` (`git worktree add`, list, prune, check dirty state).
- Protect against concurrent edits in the same working tree.

### Phase 2: Windows ConPTY Subsystem
- Implement `ConPtyProcess` using Windows P/Invoke APIs (`CreatePseudoConsole`, `ResizePseudoConsole`, `ClosePseudoConsole`).
- Asynchronous input/output pipe streaming.

### Phase 3: Embedded Terminal View Component
- Integrate `WebView2` + `xterm.js` renderer in WPF.
- Bidirectional pipe forwarding between ConPTY and the terminal view.
- Support resizing, dark theme palette, and clipboard operations.

### Phase 4: Cockpit Multiplexer UI & Attention Detection
- Build `CockpitView.xaml` with collapsible session sidebar and split grid layouts (1x1, 1x2, 2x2).
- Implement `AttentionDetector` to flag prompts (`[y/N]`, terminal bell, prompt idle) with amber indicator badges.

---

## 4. Staging Plan: Demonstrating Capabilities

1. **Stage 1: ConPTY & Dual-Terminal Host**
   - Demonstrate two embedded terminals running concurrently inside AgentHub.
2. **Stage 2: Interactive CLI Compatibility & ANSI Verification**
   - Run interactive CLIs (`agy`, `claude`) inside the embedded view; verify arrows, colors, and live output work.
3. **Stage 3: Multi-Session Cockpit & Attention Alerts**
   - Run concurrent sessions across two repositories/worktrees; demonstrate amber attention badge firing when a prompt awaits confirmation.
4. **Stage 4: Automated Worktree Task Flow & Handoff**
   - New task creation -> Git worktree provisioning -> agent execution -> `.agenthub/handoff.md` synchronization -> clean worktree merge.

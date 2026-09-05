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

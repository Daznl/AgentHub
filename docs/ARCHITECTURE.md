# Architecture

## Core rule

**Repo first, agent second.**

AgentHub owns the orchestration templates and the orchestrators attached to Cockpit sessions. Git owns code history. GitHub owns the canonical remote. Provider CLIs own model authentication and execution.

## Current components

```text
MainWindow (WPF)
    |
    +-- SettingsService ------- %APPDATA%/AgentHub/settings.json
    +-- GitService ------------ git.exe (status, fetch, pull --ff-only, push)
    +-- GitHubService --------- gh.exe (auth status/accounts, browser device-flow login, account switch, GraphQL repo query, cloning)
    |     +-- GitHubLoginWindow  modal that drives `gh auth login --web` and shows the one-time code
    +-- RepoDiscoveryService -- local folder scanner & auto-detection
    +-- UsageService --------- clean per-provider usage readers + normalized snapshots
    +-- AgentLauncher --------- wt.exe / powershell.exe external launcher
    +-- RepoContextService ---- .agenthub/handoff.md + Explorer/browser
    +-- ContextWindow --------- InstructionFiles (CLAUDE.md / AGENTS.md / GEMINI.md) + InstructionReview (claude -p via ClaudePrint)
    +-- TimelineWindow -------- TimelineSource (git log, for-each-ref, gh pr list, transcripts) -> TimelineBuilder (branch graph, attribution)
    |     +-- SquashMatcher     squash merges (no merge commit): pull request head -> squash commit, else git diff --numstat match
    |     +-- TimelineReview     checks + digest -> ClaudePrint -> scored report; rules go to ContextWindow as review hunks
    |
    +-- Multi-Session Cockpit (WPF Grid + Splitters)
         |
         +-- "PowerShell in Folder…" / per-pane 📂  -- FolderBrowserDialog -> session cwd (any folder, not only repos)
         +-- TerminalPaneControl (x N panes)
              |
              +-- EmbeddedTerminalControl (WebView2 + xterm.js)
                   |
                   +-- ITerminalSession
                        |
                        +-- HostedSession (default) -- named pipe --> AgentHub.SessionHost.exe (one per session)
                        |                                                  |
                        |                                                  +-- ReplayBuffer (last 2 MB of output)
                        |                                                  +-- ConPtySession (Win32 PseudoConsole HPCON)
                        |                                                       |
                        |                                                       +-- Process (powershell, cmd, codex, claude, agy)
                        |
                        +-- LocalSession (fallback: no host) -- ConPtySession in-process, ends with AgentHub

AgentHub.Hosting (class library shared by both executables)
    +-- ConPtySession / ConPtyNative (kernel32 CreatePseudoConsole, CreateProcess, anonymous pipes)
    +-- SessionFrame (pipe protocol), ReplayBuffer, SessionRecord / SessionStore (C:\data\agenthub\sessions\<id>\)

External tools
    +-- git
    +-- gh (GitHub CLI)
    +-- codex (OpenAI Codex CLI)
    +-- claude (Anthropic Claude Code CLI)
    +-- agy (Google Antigravity CLI)
```

## Why shell out to Git

Using the user's installed Git keeps behavior consistent with PowerShell/GitHub Desktop and respects their credential manager, SSH setup and global Git config. It also avoids a native Git library dependency in the first version.

## GitHub integration (accounts, sign-in, access-aware browser)

Full detail lives in [GITHUB_INTEGRATION.md](GITHUB_INTEGRATION.md). The essentials:

```text
GitHub Repos view
    |
    +-- Account combo / Sign in / Sign out
    |     +-- gh auth status --json hosts      (who is signed in, which is active)
    |     +-- gh auth switch / gh auth logout  (per host + login)
    |     +-- GitHubLoginWindow -> gh auth login --hostname H --git-protocol https --web --skip-ssh-key
    |          (stdin closed => gh prints the one-time code + URL, never prompts; dialog shows code, opens browser)
    |
    +-- GitHubService.StreamAllRepositoriesAsync
          +-- gh api graphql --paginate  viewer.repositories(affiliations: OWNER, COLLABORATOR, ORGANIZATION_MEMBER)
          +-- one JSON object per line -> ParseRepositoryLine -> Progress<T> -> batched UI render
          +-- viewerPermission + owner + isArchived => CanPush / Category
```

1. **gh owns credentials.** AgentHub never sees a token; it drives `gh` non-interactively and parses
   what `gh` prints. The one-time device code is the only secret-adjacent value shown, and it is
   meant to be shown.
2. **GitHub decides push rights.** `viewerPermission` (ADMIN/MAINTAIN/WRITE => push; TRIAGE/READ =>
   clone/pull only; archived => never push) is read straight from GitHub and drives the Push button,
   the access badge and the group the repo appears in. Everyone can clone anything they can see.
3. **`gh repo list` is not "everything I can see".** Without an owner it lists only personally owned
   repos, which is why organisation-only accounts saw nothing. The GraphQL query with all
   affiliations replaces it.
4. **Stream, don't wait.** Pages arrive as NDJSON lines; the view re-renders every 25 repos / 400 ms
   into a virtualised, grouped `ListBox`. Loads are cancellable so account switches never mix lists.

## Terminal subsystem & ConPTY integration

AgentHub hosts interactive character-mode processes directly inside WPF using:
1. **Windows PseudoConsole (ConPTY):** Win32 API (`CreatePseudoConsole`, `CreateProcess` with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`) handles low-level VT/ANSI translation, process lifecycle, and buffer virtualization.
2. **Synchronous anonymous pipes:** Bidirectional streaming with dedicated background reading threads to avoid deadlocks. Pseudoconsole ends of pipes are closed immediately after child process creation to allow proper EOF detection.
3. **Microsoft WebView2 + xterm.js:** Renders the terminal frontend with hardware-accelerated canvas rendering, full ANSI color palette, and dynamic resizing via `fitAddon`.
4. **Lifecycle & event idempotency:** WPF `Loaded` events fire repeatedly across tab switches and visual tree rearrangements. `EmbeddedTerminalControl` utilizes strict initialization state flags (`_isInitialized`, `_isInitializing`) and idempotent event subscriptions to guarantee zero duplicate input handlers or ghost processes.
5. **Working directory is a first-class input, not derived from repos.** A session's cwd normally comes from the pane's repository dropdown, but the Cockpit toolbar's "📂 PowerShell in Folder…" and each pane's 📂 button let the user pick any folder through the Windows folder picker. The toolbar button always opens plain PowerShell in a new (or idle) pane; the pane button launches that pane's selected shell/agent and confirms before replacing a running session. The last folder is persisted as `AppSettings.LastTerminalFolder`. Details in [MULTI_SESSION_COCKPIT.md §2.3](MULTI_SESSION_COCKPIT.md).

## Sessions that outlive AgentHub (the session host)

A pseudo-console cannot be handed to another process, so a CLI started inside AgentHub died with it. Now the console lives in a separate process, the terminal-multiplexer shape:

- **One `AgentHub.SessionHost.exe` per session.** AgentHub writes `C:\data\agenthub\sessions\<id>\session.json` (command line, folder, title, size, start time) and starts the host with the id. The host creates the pseudo-console and the process, writes its own pid and the CLI's into the record, keeps a 2 MB `ReplayBuffer` of output, and serves the pipe `\\.\pipe\agenthub-session-<id>`. It lives exactly as long as the CLI: on exit it records the exit code, saves the buffer as `replay.bin`, tells an attached client, and stops. One crash takes one session; hosts share nothing.
- **Protocol** (`AgentHub.Hosting.SessionFrame`): length-prefixed frames, one type byte and a little-endian 32-bit length. Client → host: `Hello` (protocol version, client pid, and the stream offset the client already shows, -1 for none), `Input`, `Resize`, `Kill`, `Detach`. Host → client: `Welcome` (version, pid, running, exit code, size, the offset the session's output has reached, and the offset the replay starts at) or `Refused`, then `Replay` (everything after the client's offset when the ring still holds it, else the whole ring trimmed to a UTF-8 character boundary), then `Output` frames and one `Exited`. Snapshot and hand-over happen under the host's output lock, so a reattaching client misses and repeats nothing. The offset fields were added at the end of `Hello` and `Welcome` without a version bump: a short payload reads as "unknown", so a host from the first build still serves a newer AgentHub. One client at a time; a second AgentHub connecting while one is attached times out and treats the session as busy.
- **Exact restore.** Replaying raw output into a fresh terminal only reproduces the screen when the stream is complete and the geometry matches; a 2 MB ring of a TUI's redraws usually starts mid-frame. So on close each pane saves its terminal exactly as shown (`screen.json`: the xterm.js buffer serialised by the serialize addon, with scrollback, colours and cursor, plus the size and the output offset it stands at, which `EmbeddedTerminalControl` tracks on the UI thread as pieces are posted to the page). `MainWindow.OnClosing` holds the close for up to two seconds while the pages answer, then `OnClosed` detaches. On attach the pane sends that offset in `Hello`; when the host's replay starts exactly there and the sizes match, the page is reset, sized as the session's console is, given the saved screen and then only the output since (the `restore` message in `terminal.html`; replies the terminal would send to old queries in that output are dropped, and fitting waits until it is all in), so nothing is drawn twice or in the wrong place. Otherwise the ring is replayed at the console's size and the console is made to repaint everything: by the ordinary `Resize` when the pane's size differs, or by a one-row resize and back when it does not. A session that ended while AgentHub was closed gets the same treatment from the saved screen and the tail of `replay.bin` (the record's `ReplayStart` says where that file begins).
- **In AgentHub**, `ITerminalSession` sits between `EmbeddedTerminalControl` and the process: `HostedSession` (the pipe client; `Detach` releases without ending, `Kill` ends) and `LocalSession` (the old in-process `ConPtySession`, used when the host executable is missing or fails to start; the pane says so). `SessionHostService` starts and attaches hosts and lists live ones (`Process.GetProcessById` + process name check on each record).
- **Closing AgentHub detaches; only explicit actions kill.** `MainWindow.OnClosed` saves the pane layout and detaches every pane. A pane's ✕ (confirmed when a session runs), **Stop**, **Relaunch** (confirmed) and **■ Stop all** (confirmed) send `Kill`. AgentHub never ends a host it did not start in this run without asking.
- **Reattach on start** (`MainWindow.RestorePanes`): one pane per `AppSettings.PaneLayout` entry; a live host → attach and restore (the context badge and last prompt come back on their own, since the record keeps the transcript path and the tracker is started with the recorded start time); a dead host → the record's exit code and its last screen are shown and the launcher opens on the same folder and CLI pointing at the transcript (the launcher hides the terminal while open); no session → the launcher on the saved folder and CLI. Live hosts the layout does not mention get panes too, so a lost layout never hides a running session. A fresh launch through the host also sends the pane's size once attached, since the pane may have been resized while the host was starting.
- **Live publish safety.** The host executable ships beside `AgentHub.exe` (a project reference) but runs from `C:\data\agenthub\host\<hash of its own dll>\`, copied on first use. A publish over the live folder never touches a running host, and an old host keeps its old code while a newer AgentHub attaches; the `Hello`/`Welcome` version field lets a mismatch refuse politely.

Tests: the framing (including the first build's shorter `Hello` and `Welcome`), the ring buffer (slices from an offset) and the record store (records, screen snapshots) are pure (`SessionFrameTests`, `ReplayBufferTests`, `SessionStoreTests`); `SessionHostLiveTests` runs the real host with PowerShell in a folder whose path has spaces, attaches, types, detaches, reattaches from its offset and gets only the delta, resizes and sees the shell report the new width, reattaches without an offset and gets the whole ring, exits, and checks the record and replay on disk.

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

AgentHub monitors provider rate limits without burning prompt quota or storing API keys. The
guiding principle (learned the hard way): **prefer clean structured sources over scraping redrawn
terminal frames.** Each provider is read from the cleanest signal it exposes:

```text
Cockpit usage dashboard
    |
    +-- UsagePollingBadge (user-set interval, persisted to settings.json)
    +-- Usage interval box (0.25m - 120m; Enter / focus-out applies + refreshes)
    +-- ToggleRawOutputBtn / RawOutputPanel (inspect the raw source text)
    |
    +-- UsageService (parallel collection, isolated per-provider failures)
         |
         +-- CodexUsageReader  ---- ~/.codex/sessions/**/rollout-*.jsonl (on-disk rate_limits)
         +-- ClaudeUsageReader ---- Anthropic OAuth usage endpoint (JSON)
         +-- ProcessRunner + UsageOutputParser -- agy -p /usage (non-interactive stdout)
              |
              +-- UsageSnapshot[] -> WPF display models (cards, % left bars, reset timestamps)
```

1. **Codex — on-disk rate limits (no CLI call):** Codex records the latest server-reported
   `rate_limits` block (primary = 5-hour, secondary = weekly) into its session rollout JSONL files
   as an API side effect. `CodexUsageReader` reads these directly. Because any write bumps a file's
   mtime and several sessions can be open at once, it selects the reading with the newest **recorded
   timestamp** across recent files (not the newest file mtime), and tolerates files locked by a live
   Codex session (shared read). Because Codex only writes a reading on an API call, a window whose
   `resets_at` has already passed is treated as rolled over and fully available again (shown as 100%
   left) rather than surfacing the stale pre-reset number.
2. **Claude — OAuth usage endpoint:** `ClaudeUsageReader` reads Claude's structured usage JSON
   (session/5-hour and weekly utilization + reset times) rather than driving the interactive
   `/status` TUI.
3. **Antigravity — native headless expansion:** `agy -p /usage` prints tab-separated live limits for
   Gemini and Claude/GPT models in ~1s without prompting the LLM; `UsageOutputParser` strips ANSI and
   extracts the limits.
4. **User-controlled polling:** A single refresh interval (minutes) is user-set, persisted to
   `settings.json`, and clamped to a 15-second floor / 120-minute ceiling. The badge shows the active
   terminal count and the current cadence; editing the interval box applies and refreshes immediately.
5. **Presentation:** Bars show percentage **left** (full = plenty remaining, amber ≤ 35%, red ≤ 15%),
   with human-friendly reset text (near-term windows count down; further-out ones show day + clock).
6. **Safety:** AgentHub stores no API keys or provider credentials. Every displayed value identifies
   the local source that supplied it, and parser/read failures are surfaced per-provider instead of
   being presented as a balance. Default providers are preconfigured in `settings.json` and migrated
   on launch.

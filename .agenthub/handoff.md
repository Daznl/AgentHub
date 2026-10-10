# AgentHub Handoff Context

**Updated:** 2026-10-10
**Current branch:** `compact-header` (built on `turn-capture`; neither is on `main` yet)
**Current work:** Session tracker, stage 1 (turn capture + 20-word summaries), and a one-row header

---

## 1. What AgentHub is

A Windows-first **.NET 8 WPF** control centre for local and GitHub repositories that hosts interchangeable coding CLIs (Codex, Claude Code, Antigravity, PowerShell, Cmd) in a multi-pane ConPTY + WebView2/xterm.js Cockpit. `README.md` has the full feature list; `docs/ARCHITECTURE.md` the component map.

---

## 2. The session tracker (what this work is for)

The user runs several Claude sessions at once and cannot watch them all. The goal is a per-repository tracker that sits on top of each coding session, **never tells the session what to do**, and tells the user "you asked X, Claude did Y, you should probably do Z next".

Ground rules agreed with the user:

- **Per repository**, shared by every session in that repo. Runs **every turn**.
- **Never feed it the whole conversation.** Per turn it sees only the user's prompt and Claude's closing plain-text reply (the text after the last tool call), plus a compact rolling project state.
- **Structured source, not terminal scraping.** Read Claude Code's own transcript JSONL.
- **Subscription, not API key.** Model calls go through `claude -p` on the claude.ai login. `ANTHROPIC_API_KEY` is set on this machine as a user variable, so it must be removed from those calls' environment.

### Stages

| Stage | What | State |
|---|---|---|
| 1 | Turn capture: known session id, transcript watcher, pane strip, `.agenthub/journal.jsonl` | **Done** (`turn-capture`) |
| 1b | 20-word summary of each reply via headless Haiku | **Done** (`turn-capture`) |
| 2 | `CLAUDE.md` rule so Claude ends turns with a consistent summary (optional `TL;DR:` line, Haiku as fallback) | Not started |
| 3 | Next-step suggestion from a bounded prompt (rolling `.agenthub/state.md` + recent turns + new turn) | Not started |
| 4 | Overview of every tracked repository | Not started |
| 5 | Codex support (`~/.codex/sessions`), "watch" rules (the user mentioned watching certain things; not yet defined) | Not started |

---

## 3. How stage 1 works (`src/AgentHub/Tracking/`)

- **`ClaudeSessionLaunch`** appends `--session-id <guid>` to a fresh interactive `claude` launch when the pane's folder is a Git repo root. Skips `--resume`, `-c`, `-p`, existing `--session-id`. Restart generates a new id. Transcript path: `~/.claude/projects/<cwd with every non-alphanumeric char → '-'>/<id>.jsonl` (`CLAUDE_CONFIG_DIR` respected).
- **`TranscriptWatcher`** polls every 500 ms, reads only new bytes, holds back an unfinished last line. The folder-wide fallback search runs every 20th poll until the file exists.
- **`ClaudeTranscriptParser`** opens a turn on a human prompt (`origin.kind == "human"`; skips tool results, `isMeta`, `isSidechain`; strips `<system-reminder>`; slash commands become `/name args`). Ends it on `stop_reason: end_turn` **on a text line**: one reply is written as one line per content block and every line carries the stop reason, so a thinking-only line must not end the turn. The next prompt marks an unfinished turn as interrupted. `Settle()` (on the pane's waiting signal or exit) is the fallback.
- **`TurnJournal`** appends to `<repo>/.agenthub/journal.jsonl` and adds that path to `.git/info/exclude` (local only, never shows as a change). A turn is appended again when it continues or when its summary arrives; readers keep the last line per `turnId`.
- **`TurnSummarizer`** runs `claude -p --model haiku --tools "" --no-session-persistence --strict-mcp-config --disable-slash-commands --settings {"disableAllHooks":true} --system-prompt ...` in `%LOCALAPPDATA%\AgentHub\summarizer`, reply on stdin, `ANTHROPIC_API_KEY`/`ANTHROPIC_AUTH_TOKEN` removed. Capped at 20 words in code. Replies already ≤ 20 words skip the call. About 3–6 s per call. **Do not use `--bare`**: it signs in only with an API key.
- **Pane strip** (`TerminalPaneControl`): shows **👀 Tracking** as soon as a tracked session launches (Claude writes nothing until the first prompt), then **📝 You asked** / **⏳ Claude is working…** / **✅ Claude said** with the summary. Click to expand to the full reply.
- **`App` clears inherited Claude session markers** (`CLAUDECODE`, `CLAUDE_CODE_CHILD_SESSION`, ...) at startup. Without this, an AgentHub started from inside a Claude session gives every pane's Claude "transcript saving off", and tracking sees nothing. This cost a long debugging detour.
- Settings: `turnTrackingEnabled`, `turnSummariesEnabled`, `turnSummaryModel` (default `haiku`).

### Known gaps

- `/clear` or `/resume` inside a pane switches Claude to another session id, so tracking stops for that pane.
- Codex and Antigravity panes are not tracked.
- The summary appears a few seconds after the reply, and each one uses a little of the Claude subscription limits.

---

## 4. Other changes in this work

- **One-row header** (`compact-header`): view actions, divider and navigation share one row; logo 40 px; Cockpit description is one trimmed line. Gives the terminals about 40 px. On the Manage GitHub Repos view the row is wide and may clip on narrow windows (not checked).
- `ProcessRunner.RunAsync` gained optional `standardInput` and `removeEnvironmentVariables` parameters.

---

## 5. Working on this repo

- **Branching (user's global rule):** never commit to `main`; each stage on its own branch; when a stage is complete, fast-forward `main` to it and push.
- **Build lock:** a running `AgentHub.exe` from `src/AgentHub/bin/Debug` locks the build output. Check for a running Claude session in AgentHub before closing it (the user may be mid-conversation). To compile without closing it: `dotnet build src/AgentHub/AgentHub.csproj -p:OutDir=<scratch>/`; for tests, build `tests/AgentHub.Tests` with the same `OutDir` and run `dotnet test <scratch>/AgentHub.Tests.dll`.
- **Do not launch AgentHub from inside a Claude Code session expecting real pane behaviour** unless the marker-clearing fix is in the build.
- There is an older copy of the app at `Desktop\AgentHub-App-SSH\AgentHub.exe` (not a git repo, no tracking). Both windows have the same title, so it is easy to type in the wrong one.
- WPF + WinForms are both enabled; alias UI types (`UserControl`, `Color`, `MessageBox`, `Timer`) explicitly.
- Pull is fast-forward only; destructive Git operations only on explicit user action; usage figures must name their source.
- Tests: `dotnet test tests/AgentHub.Tests` (75 passing). Tracking tests are in `TurnTrackingTests.cs`.

---

## 6. Next steps

1. Ask whether to fast-forward `main` to `compact-header` (includes `turn-capture`) and push.
2. Session tracker stage 3: per-turn "what's next" suggestion with a rolling `.agenthub/state.md`, using the same headless subscription call as `TurnSummarizer`.
3. Stage 2 (`CLAUDE.md` summary rule) if summary latency or usage becomes a concern.
4. Clarify with the user what "watch certain things" should mean.

# Roadmap

## v0.1 — Repo + agent launcher (included)

- Repo registry
- Clone/add existing
- Git status
- Fetch / fast-forward pull / push
- Open GitHub/folder
- Configurable Codex / Claude / Antigravity launch
- Shared handoff file

## v0.2 — Task worktrees

- Create task from selected repo
- Automatically create branch + Git worktree
- List active tasks/worktrees beneath each repo
- Launch any configured agent in a task worktree
- Archive/delete completed worktree after merge
- Detect dirty worktrees and protect against accidental removal

## v0.3 — Session manager

- Track agent process/session start and end
- [x] Session history per repo: 🕘 History window and per-pane button, full timeline (prompts, replies, thinking, tool calls/results, events) from Claude Code and Codex transcripts, followed live, filter/search/Markdown export (`SessionHistoryReader`, `HistoryWindow`). Remaining: per task/worktree, Claude sub-agent transcripts (`<session>/subagents/`), Antigravity
- [x] Past sessions on a fresh install (`SessionFolderDiscovery`, 2026-11-16): every folder with Claude Code or Codex sessions on this computer (live transcripts plus the archive, folder taken from each transcript's recorded `cwd`) is offered in launcher step ① and in History under "Past sessions on this computer", newest first; scratch folders under AppData/temp and AgentHub's own working folders are left out; a Git folder can be added to the repositories from History. Remaining: Claude Code sessions recorded with a Git Bash style folder (`/c/Users/...`) are filed under another folder name and not credited; sessions from other computers (the CLIs keep them locally)
- [x] Permanent history archive (`HistoryArchiver`): append-only mirror of `~/.claude/projects` and `~/.codex/sessions` into `HistoryArchiveDirectory`, never deletes, keeps superseded copies; History lists archive-only sessions. Remaining: archive while AgentHub is closed (scheduled task), compression
- [x] Project timeline, phase 1 (`TimelineBuilder`, `BranchGraph`, `TimelineSource`, `TimelineGutter`, `TimelineWindow`): pane header **🌳** opens one window per repository (all worktrees); three verticals (branch graph with `main` as trunk and every branch a lane, session cards, commit cards level with their session) from the first commit to now; commits attributed by time window then hash mention; expandable cards with prompts, **Open in History**, re-read every 20 s; built from git and transcripts, plus `gh pr list` for squash merges (`SquashMatcher`: pull request head → squash commit, else a change match against "(#N)" trunk commits) so squash-merged lanes curve back into the trunk instead of ending open; **✕ Close** button and Esc close the window. Remaining (phase 2): per-reply "what was built" summaries by an agent (`claude -p`, same lock-down as Review) with a cached backfill, a global Timeline button with a repository picker, Antigravity sessions
- [x] Timeline reviews (`TimelineReview`, `ReviewKind`, `ReviewTimings`, `ClaudePrint`, `TranscriptTexts`): two actions at the top of the Timeline helper pane (opened by the crab beside Refresh, which paces while one runs; `AppHelperPane` with `Actions`, `CrabMascot.IsBusy`), **✨ You & Claude** and **✨ GitHub cycle**, each sending its own budgeted digest to `claude -p` (sessions with prompts and last replies, or branches and `gh` pull requests; plus AgentHub's own checks, the IGO rules and the user-wide `CLAUDE.md`) and showing its own panel beside the tree: a score out of 10 (from the model for the sessions, computed from the checks for GitHub), findings with evidence linked to session cards, and rules for `CLAUDE.md` applied through the Context editor's Apply boxes; elapsed time and a size-based estimate while it runs; report and digest saved under `C:\data\agenthub\timeline`. Remaining: the rules digest is hard-coded from the dev kit (refresh by hand), repo `CLAUDE.md` as a second target for additions, Codex sessions' last replies, a model setting
- Quick switch: Codex → Claude → Antigravity in same worktree
- Generate a handoff template from `git diff`, recent commits and task notes
- [x] View and edit context files (`InstructionFiles`, `ContextWindow`): header **📄 Context** for each CLI's user-wide file, pane header **📁 Repo context** for the pane folder's `CLAUDE.md` / `AGENTS.md` / `GEMINI.md`; saves keep line endings, are atomic and never silently replace a file changed elsewhere. Remaining: Markdown preview, Claude Code `@import` and nested-folder files
- [x] Review context files (`InstructionReview`, `LineDiff`): **✨ Review** asks Claude Code (`claude -p`, tools and user settings off, own login, no API key) for schema-checked edits named by first and last line, shown per change with Apply boxes; applied changes go to the editor, never straight to disk; an edit with an empty start on a file with text appends at the end (used by the timeline review's rules). Remaining: Codex / Gemini as reviewer, review of a running session's conversation (when to compact)
- Optional provider-specific context files (`AGENTS.md`, `CLAUDE.md`, etc.) without duplicating project truth

## v0.4 — Usage + budget dashboard

- [x] Configurable Codex, Claude and Gemini CLI usage commands
- [x] Background pseudoconsole (ConPTY) terminal probe for interactive slash commands (`/status`)
- [x] Native headless Antigravity expansion (`agy -p /usage`) with multi-model limit tracking
- [x] Normalized remaining-limit dashboard (percentage **left**) with reset times, progress bars, refresh state and isolated provider failures
- [x] Clean per-provider usage sources instead of TUI scraping: Codex on-disk `rate_limits` (newest reading across concurrent sessions), Claude usage endpoint, Antigravity `agy -p /usage`
- [x] User-configurable, persisted usage polling interval (15s - 120m) replacing the earlier adaptive engine
- [x] Collapsible raw source feed panel for live output verification
- Pluggable non-CLI usage adapters
- Record observed CLI/session consumption where available
- Manual remaining-quota override when providers expose no machine-readable balance
- Session burn-rate estimates
- Recommend an agent based on user-defined priorities: capability, remaining quota, reset time, cost
- Clear confidence/source label beside every usage figure

## v0.5 — GitHub workflow (Partially completed in v0.1.5 / v0.1.6)
 
- [x] GitHub CLI (`gh`) integration & repository browser (v0.1.5: remote repo search, filter, 1-click clone, live sync state badges, in-card fast-forward pull, and 1-click push)
- [x] In-app GitHub sign-in with real browser verification (v0.1.6): `gh auth login --web` driven non-interactively, one-time code shown in a dialog, browser opened, `gh` keeps the token. Terminal route kept as fallback.
- [x] Multiple GitHub accounts (v0.1.6): account dropdown from `gh auth status --json`, switch via `gh auth switch`, sign out via `gh auth logout`, GitHub Enterprise hosts accepted.
- [x] Load every repository the account can see (v0.1.6): single paginated GraphQL query (own + shared + organisation), streamed page by page into a virtualised list. Replaces `gh repo list`, which only returned personally owned repos.
- [x] Access-aware categorisation (v0.1.6): GitHub `viewerPermission` + owner + archived state group repos into mine / org you can push / org clone-only / shared / archived, with per-card role badges, access-based filters, and Push shown only where GitHub will accept it. Everyone can clone anything visible. See `docs/GITHUB_INTEGRATION.md`.
- Opt-in `gh auth setup-git` button so `git push` credentials follow the active gh account
- PR create/view/open
- CI status
- Issue/task association
- Branch protection awareness

## v0.6 — Orchestration (included)

- [x] **Pane orchestrator** (`PaneOrchestrator`, `ClaudeJsonSession`, `OrchestratorBoxView`): 🧭 Orchestrate on any running pane puts a headless Claude session between the owner and that session, with no run, idea or spec; it answers the owner and instructs the session; ✕ Detach takes it off. Its first message carries the session's conversation from the start (`ConversationHistory`: every message sent, the end of each reply). Its box shows its own context bar (read from its transcript after each decision). Context handoff restarts the session in the same pane (tested live, 3 panes in parallel), at a limit the box's **Handoff at** field changes live (checked against the latest reading at once; kept in settings for the next one), or early when the orchestrator asks (`new_session`: on the owner's say-so, or on its own for new work once the context holds 150k tokens; the new task is carried to the fresh session; refused before a fresh session has worked and during an advanced-template run); security checks stay the owner's (WAITING FOR YOU, messages held). Turn ends come from the terminal or the transcript, including turns the session starts by itself, with an idle check after 10 minutes (added 2026-11-13 after a finished part sat unseen for 21 minutes). Its prompt carries fixed safety rules and the worker's instruction files (`OrchestratorRules`; added 2026-11-09 after it told a worker to force push). Remaining: no usage wait; not kept across an AgentHub restart
- [x] **Orchestration templates** (`OrchestratorWorkflow`, `WorkflowCatalogue`, 📚 `WorkflowsWindow`): Builder, Data Processing and Demo Builder; duplicate, fill in inputs and save your own (in git-ignored `workflows/local/`); each is a context the 🧭 Orchestrate menu offers
- [x] **Retired 2026-11-09** (in git history before then): the idea queue (`ProjectIdea`, `IdeaService`, `IdeasWindow`), the run engine (`OrchestratorEngine`, `OrchestratorFleet`, `ClaudeOrchestratorBrain`, `OrchestratorDecision`), steers, the usage-cap wait, stages with a fresh worker per stage, worktrees per run and the run archive (`RunRecorder`). Orchestration is assigned to a session in the Cockpit instead
- [ ] **Advanced templates** (`AdvancedTemplate`, `AdvancedTemplateStore`; [ADVANCED_TEMPLATES.md](ADVANCED_TEMPLATES.md)): a sequence of stages and typed steps the orchestrator follows one by one, kept as JSON in the project's own repository (`orchestration/<id>.json`). Shipped 2026-11-13: the file format, its checks (ids, what each step kind needs, no loops), stage order with parallel stages allowed, and the store with drafts that replace the template only when saved; a window that draws a project's templates as trees (`AdvancedTemplatesWindow`, 🧭 Orchestrate → 🗺 Advanced templates of this project…); runs (`TemplateRun`, 🧭 Orchestrate → 🗺 Follow: …): the orchestrator follows the template step by step and reports each step done, continuing or escalated, with Go, Pause and go on from a step in the tree, which shows the run live; runs kept on disk (`RunStore`, `RunRecorder`) and picked up again after a detach or a restart from ⏯ Runs (`RunsWindow`) or the 🧭 Orchestrate menu, with the same orchestrator session while its transcript exists; editing in the tree through drafts (✏ Edit, 💾 Save, 🗑 Discard; `TemplateEditing`), and a saved change applied to a run that stands still (⤵ Apply to this run; `TemplateRun.Rebase`), steps it has reached locked. Shipped 2026-11-14: the Template helper, the crab of the Advanced templates window (`TemplateHelper`, `AdvancedTemplatesWindow.Helper.cs`), which builds a template from a written plan and changes the one on screen, into a draft opened in the editor; tested live with Claude Code. Remaining: a live run with a real Claude session, resume into a session AgentHub starts itself, parallel stages across panes
- [ ] Orchestrator model and decision cap as settings (now the template's model or `sonnet`, 300 in code)
- [ ] Keep an attached orchestrator across an AgentHub restart (its session id and pane). Done for one that follows an advanced template (its run is kept and resumed, 2026-11-13); not for a plain one

## Multi-Session Cockpit Stages (Active)

- **Stage 1 (Completed in v0.1.5):** Embedded dynamic multi-terminal multiplexer via Windows ConPTY + WebView2/xterm.js. Side-by-side terminal panes for concurrent interactive sessions with 3+ default terminals, dynamic "➕ Add Terminal" pane creation, individual "✕ Close" controls, draggable splitters, and robust lifecycle idempotency preventing input duplication.
- **Stage 1b (Completed in v0.1.6):** Open a shell in any folder: Cockpit toolbar "📂 PowerShell in Folder…" (folder picker → plain PowerShell in a new pane, idle pane reused when all 6 exist) and a per-pane 📂 button launching that pane's selected shell/agent in a chosen folder (confirms before replacing a running session). Last folder persisted as `LastTerminalFolder` in settings.
- **Stage 2 (Next):** Interactive CLI compatibility testing & ANSI escape sequence verification across Antigravity (`agy`), Claude Code (`claude`), and Codex (`codex`).
- **Stage 3 (In progress):** Attention detector & alerting engine. Shipped: `AttentionDetector` (input-idle after a work burst, terminal bell) armed only for agent CLI sessions, amber pane pulse plus a persistent **⏳ WAITING FOR YOU** badge, and taskbar flashing when AgentHub is backgrounded; thresholds in `AttentionIdleSeconds` / `AttentionFlashEnabled`. Remaining: bell chime, `[y/N]`-style prompt heuristics, Windows Toast.
- **Stage 3b (Shipped):** Per-pane context window badge for Claude Code and Codex, read from each CLI's own session transcript (`SessionContextReader` / `ContextWindowTracker`). Claude window size is a setting (`ClaudeContextWindowTokens`) because Claude Code does not record it. Resumed sessions bind to their exact transcript; new ones only to transcripts created after launch, so a session running elsewhere in the same folder is never shown. Remaining: track CLIs started by hand inside a shell pane, Antigravity.
- **Stage 3c (Shipped):** Pane launcher in three steps: repository, then CLI, then session (new, or resume any recorded one via `SessionResume`, restoring archive-only sessions first). Also a per-pane **last prompt** strip from the same transcripts (`ReadClaudePrompt` / `ReadCodexPrompt`), with expand and copy.
- **Stage 4:** Automated Git worktree isolation per task with cross-agent handoff orchestration.
- **Stage 5 (Shipped 2026-11-04):** Sessions that outlive AgentHub. Each Cockpit session runs in its own `AgentHub.SessionHost` process that owns the pseudo-console and a 2 MB replay buffer; AgentHub attaches over a named pipe, detaches on close instead of killing, and reattaches on start from the saved pane layout (`PaneLayout` in settings), so AgentHub can be closed, updated and reopened while Claude Code keeps running. The pane comes back exactly as it was: each pane saves its terminal state on close (`screen.json`) and the host replays only the output written since; a pane resized while the host was starting now tells the host its size. Ended sessions come back with their last screen and a resume offer; the host runs from a versioned copy under `C:\data\agenthub\host\` so a live publish never touches it. Design in `docs/ARCHITECTURE.md` ("Sessions that outlive AgentHub"). Remaining: a "busy" message when another AgentHub instance holds the session; pruning old host version folders.

## Later

- Notifications when an agent exits or needs attention via Windows Toast
- Repository templates and bootstrap rules
- Multiple agent profiles per provider (e.g. conservative vs autonomous)
- Model selection metadata where a CLI exposes a stable interface
- Optional local SQLite database once settings/session data outgrow JSON

# Agentic Session Layer — Design Draft

> **Status: DRAFT / not implemented.** This captures the intended shape only. The
> interfaces live in [`src/AgentHub/Sessions/`](../src/AgentHub/Sessions/) as contracts with
> no behaviour yet. Nothing here is wired into the app or DI. No build target depends on it.

## Purpose

Let an external orchestrator — a user-run on-prem agent pipeline — observe and drive AgentHub's
sessions safely, without every agent independently scraping raw terminal frames.

## Two planes

Sessions split into two kinds (`SessionKind`):

- **Human plane** — today's interactive ConPTY + xterm.js terminals. Unchanged; for the user to drive.
- **Agent plane** — sessions the orchestrator runs **headlessly/programmatically** (Claude
  `--output-format stream-json`, Codex `exec`, MCP). These emit structured events, so the
  orchestrator usually does **not** need to detect halts by inference at all.

Guiding principle (learned the hard way with usage collection): **prefer structured signals over
TUI scraping.** Scraping redrawn frames is fragile — use it only where no clean signal exists.

## The four-method contract

Everything the orchestrator needs reduces to four things the substrate must expose. Each maps to a
member in [`ISessionManager`](../src/AgentHub/Sessions/ISessionManager.cs) /
[`ISessionBuffer`](../src/AgentHub/Sessions/ISessionManager.cs):

1. **Sanitized buffer + monotonic offsets** — `ISessionBuffer` (one per session; the single source
   of truth so the watcher and extractor agree on where "the previous halt" was). Sanitize with the
   existing `UsageOutputParser.StripAnsi`.
2. **Deterministic attention event** — `ISessionManager.Attention` (Stage 0; PTY quiescence / prompt
   match / structured headless event — never a model).
3. **`read_since(offset)` delta** — `ISessionManager.ReadSince` (send only what is new since the last
   decision, not the whole scrollback).
4. **Guarded `send_input`** — `ISessionManager.SendInputAsync` with `ExpectedStateHash`
   (optimistic concurrency, so an agent can't act on a screen that already moved on).

## How the on-prem orchestrator sits on top

The user's pipeline becomes an **escalation pipeline**, not three always-on inferencers:

| Stage | Runs on | Trigger | Job |
|-------|---------|---------|-----|
| 0 — Detect | no model (`IAttentionDetector`) | quiescence / prompt / headless event | raise `AttentionEvent` |
| 1 — Stage | mid on-prem model | on attention event | `ReadSince` the delta, classify, summarise |
| 2 — Decide | power model | only when Stage 1 is unsure or the action is consequential | choose next input |
| Act | `SendInputAsync` (guarded) | Stage 2 output | inject, or surface to the human |

This keeps the expensive tiers asleep until Stage 0 fires — far cheaper than "constantly inferring",
same capability.

## Safety (first-class, not a later patch)

The decide→inject loop can run away. Brakes live in
[`ISessionSafety.cs`](../src/AgentHub/Sessions/ISessionSafety.cs):

- **`AutonomyLevel`** per session: `Observe → Suggest → ActWithConfirm → Autonomous`.
- **`ISessionActionPolicy`** — command allowlist, **never auto-confirm destructive operations**
  (generalises the existing fast-forward-only / no-destructive-git rule), spawn constrained to
  registered repos + known CLIs.
- **Loop-breaker** — `EvaluateAutonomousBudget` forces a human hand-off after N unattended actions.
- **Staleness guard** — the `ExpectedStateHash` on `SendInputAsync`.
- **`ISessionAuditLog`** — append-only record of every action for review.

## Build order (when we pick this up)

1. Extract a headless `SessionManager` + `ISessionBuffer` out of `EmbeddedTerminalControl`; point the
   Cockpit UI at it (pure refactor, no new capability).
2. Read-only surface first: `ListSessions`, `ReadSince`, `Attention` (+ usage), token-gated on `127.0.0.1`.
3. Add mutating ops (`Spawn`, `SendInput`, `Stop`) behind `ISessionActionPolicy` + audit log.
4. Add a headless agent-plane runner (stream-json / exec) so orchestration is event-driven.
5. Layer per-task git worktree isolation (roadmap v0.2) for parallel agent sessions.

## Open questions

- **In-app supervisor vs external-driven backend?** Changes whether the boundary is in-process calls
  or a local MCP/WebSocket server with auth. Leaning: expose an **MCP server** over `ISessionManager`
  (standard, tool-scoped, any agent can consume) + optional local WebSocket for live output streaming.
- Prompt/quiescence detection heuristics per CLI (Claude vs Codex vs shells).
- Where session buffers persist (in-memory ring vs on-disk) for crash recovery and audit.

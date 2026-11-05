using System.Text;
using AgentHub.Models;

namespace AgentHub.Services.Orchestrator;

/// <summary>What happened that AgentHub is asking the orchestrator about.</summary>
public enum OrchestratorEvent
{
    /// <summary>A new run: the orchestrator writes the worker's first instruction.</summary>
    RunStarted,
    /// <summary>A fresh worker session after a handoff or usage reset: the orchestrator writes its first instruction.</summary>
    NewWorkerSession,
    /// <summary>The worker finished a turn.</summary>
    WorkerTurnEnded,
    /// <summary>The worker has sat idle since the orchestrator chose to wait.</summary>
    WorkerStillIdle,
    /// <summary>Daniel sent a steer that should not wait for the worker's turn end (urgent, or the worker is idle).</summary>
    SteerReceived,
    /// <summary>Daniel resumed a paused build.</summary>
    Resumed
}

/// <summary>Everything one message to the orchestrator carries. <see cref="OrchestratorMessages.Compose"/> renders it.</summary>
public sealed record OrchestratorInput(OrchestratorEvent Event)
{
    public string? EventDetail { get; init; }
    public string? Spec { get; init; }
    public string? Handoff { get; init; }
    public string? WorkerReply { get; init; }
    public IReadOnlyList<SteeringNote> Steers { get; init; } = [];
    public bool UrgentSteer { get; init; }
    public string Status { get; init; } = "";
    public IReadOnlyList<string> RecentCommits { get; init; } = [];
    public bool WorkerBusy { get; init; }
}

public static class OrchestratorMessages
{
    /// <summary>Longest worker reply passed on; the end of a reply carries the summary, so the middle is cut.</summary>
    public const int MaxWorkerReplyChars = 8000;
    public const int MaxHandoffChars = 8000;

    /// <summary>
    /// Appended to every first message a worker session gets, by AgentHub itself, so the worker always knows the house
    /// rules no matter what the orchestrator writes.
    /// </summary>
    public const string AutonomyRules =
        "(AgentHub rules for this session: you are running unattended. Instructions come from an orchestrator, not a person, " +
        "so make reasonable decisions yourself instead of asking, and note them in .agenthub/handoff.md. Commit your work to git as you go. " +
        "Keep .agenthub/handoff.md current: what is done, what is next, how to run the tests. End each turn with a short summary of what you did, " +
        "what you verified and what you plan next; that summary is all the orchestrator sees. Only when every requirement is implemented and " +
        "tested, put the line [COMPLETED] at the top of .agenthub/handoff.md.)";

    /// <summary>Sent to the worker by AgentHub (not the orchestrator) when a handoff is due.</summary>
    public const string HandoffPrompt =
        "[AGENTHUB] Time to hand off: this session ends after this turn and a fresh session continues from your notes. Stop starting new work. " +
        "Run the tests, commit, and rewrite .agenthub/handoff.md so a newcomer can continue: what was built, architecture notes, test status, " +
        "decisions made, and the next tasks in order. Then end your turn.";

    /// <summary>Sent to the worker by AgentHub when the Claude usage cap is about to be hit.</summary>
    public const string UsageWrapUpPrompt =
        "[AGENTHUB] Claude usage is almost used up, so this session is about to stop. Commit what you have and update .agenthub/handoff.md " +
        "with the next tasks now, then end your turn.";

    public static string SystemPrompt(string ideaTitle, string targetDirectory) =>
        $"""
        You are the ORCHESTRATOR of an autonomous software build that AgentHub runs for Daniel. The project is "{ideaTitle}" in {targetDirectory}.
        A separate Claude Code session, the WORKER, does all the building there. You direct it; you never build anything yourself.

        How this conversation works:
        - Every message comes from AgentHub, the program that connects you to the worker. It has labelled sections:
          [EVENT] what happened, [STATUS] numbers about the run, [SPEC] the requirements, [HANDOFF.MD] the worker's notes file,
          [RECENT COMMITS], [WORKER FINAL REPLY] the worker's last message, [STEER FROM DANIEL] direction from the project owner.
        - Daniel is not in this conversation and never reads your replies. Steers are his instructions to you about the build.
          Decide how and when they reach the worker: by default fold them, in your own words, into your next message to the worker
          when its current turn is over. If a steer is marked URGENT, relay it now. Never quote these labels to the worker.
        - The worker only sees "to_worker". It cannot see your reasoning, the status, or the steers unless you put them in.

        Your only output is the decision JSON:
        - send: to_worker is the next instruction. One clear, concrete next step or small batch of steps, building on what the worker
          just reported. Point out problems you noticed. Do not repeat an instruction it already carried out.
        - wait: the worker said it is waiting for a background job it started (training, a long test run), or it is mid-task.
          AgentHub asks you again when the worker finishes a turn or stays idle.
        - handoff: the worker is going in circles, confused, or its session is long. AgentHub has it write its handoff and starts a
          fresh worker; you then write the new worker's first instruction. (AgentHub also forces a handoff itself when the worker's
          context window is nearly full.)
        - complete: every requirement in the spec is implemented and tested, and you have checked that yourself. AgentHub stops the build.
        - pause_for_daniel: only when the build cannot sensibly continue without a decision only Daniel can make (spending money,
          credentials or accounts, anything irreversible outside the project folder). progress holds the question.

        Checking the worker: you can read the project folder (Read, Grep, Glob). Use that briefly to check claims before accepting them,
        for example that tests exist, or that the handoff matches what was said. Keep tool use short; the worker does the heavy work.

        Keep the worker moving toward the spec with steady, verifiable progress: working code, tests, commits. Prefer small verified steps
        over big leaps. If the worker asks a question, answer it yourself with the sensible option. Keep "progress" to one line.
        """;

    /// <summary>Renders one message for the orchestrator.</summary>
    public static string Compose(OrchestratorInput input)
    {
        var sb = new StringBuilder();
        sb.Append("[EVENT] ").Append(EventText(input)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(input.Status))
            sb.Append("[STATUS] ").Append(input.Status.Trim()).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(input.Spec))
            sb.Append("[SPEC]\n").Append(input.Spec.Trim()).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(input.Handoff))
            sb.Append("[HANDOFF.MD]\n").Append(Cut(input.Handoff.Trim(), MaxHandoffChars, keepTail: false)).Append("\n\n");

        if (input.RecentCommits.Count > 0)
        {
            sb.Append("[RECENT COMMITS] newest first\n");
            foreach (var c in input.RecentCommits) sb.Append(c).Append('\n');
            sb.Append('\n');
        }

        if (input.WorkerReply is not null)
        {
            sb.Append("[WORKER FINAL REPLY]\n")
              .Append(string.IsNullOrWhiteSpace(input.WorkerReply) ? "(the worker ended its turn without a text reply)" : Cut(input.WorkerReply.Trim(), MaxWorkerReplyChars, keepTail: true))
              .Append("\n\n");
        }

        if (input.Steers.Count > 0)
        {
            sb.Append("[STEER FROM DANIEL]").Append(input.UrgentSteer ? " URGENT: relay now" : "").Append('\n');
            for (var i = 0; i < input.Steers.Count; i++)
                sb.Append(i + 1).Append(". (").Append(input.Steers[i].CreatedAt.ToString("HH:mm")).Append(") ").Append(input.Steers[i].Text.Trim()).Append('\n');
            sb.Append('\n');
        }

        sb.Append("Reply with the decision JSON.");
        return sb.ToString();
    }

    private static string EventText(OrchestratorInput input) => input.Event switch
    {
        OrchestratorEvent.RunStarted =>
            "run_started: a new build run. The worker session has not been given anything yet. Write its first instruction (action send).",
        OrchestratorEvent.NewWorkerSession =>
            $"new_worker_session ({input.EventDetail ?? "fresh session"}): a fresh worker with an empty context is starting. It has not been given anything yet. " +
            "Write its first instruction (action send); it should start by reading .agenthub/handoff.md.",
        OrchestratorEvent.WorkerTurnEnded => "worker_turn_ended: the worker finished a turn and is waiting for its next instruction.",
        OrchestratorEvent.WorkerStillIdle =>
            $"worker_still_idle: you chose to wait, and the worker has printed nothing for {input.EventDetail ?? "a while"}. Decide again.",
        OrchestratorEvent.SteerReceived => input.WorkerBusy
            ? "steer_received: Daniel sent a steer while the worker is mid-turn. A send now is typed in and the worker reads it at its next step."
            : "steer_received: Daniel sent a steer; the worker is idle at its prompt.",
        OrchestratorEvent.Resumed => "resumed: Daniel resumed the build after a pause. The worker is at its prompt.",
        _ => input.Event.ToString()
    };

    /// <summary>Shortens text to about <paramref name="max"/> characters, keeping the start (and the end, when <paramref name="keepTail"/>).</summary>
    public static string Cut(string text, int max, bool keepTail)
    {
        if (text.Length <= max) return text;
        if (!keepTail) return text[..max].TrimEnd() + "\n…(cut)";
        var head = max / 4;
        var tail = max - head;
        return text[..head].TrimEnd() + "\n…(middle cut)…\n" + text[^tail..].TrimStart();
    }
}

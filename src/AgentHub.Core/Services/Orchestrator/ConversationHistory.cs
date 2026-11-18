using System.Text;
using AgentHub.Services.History;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// The worker session's conversation before the orchestrator was attached, for its first message (owner feedback 2026-11-09:
/// "It should receive everything the user has said for that session and the last part of a reply from the session ...
/// going back to the start"). Every message the session was sent, oldest first, each followed by the end of the session's
/// reply: the end of a reply carries its summary. Tool calls, tool output and thinking are left out.
/// </summary>
public static class ConversationHistory
{
    /// <summary>Upper bound on the whole block; earlier replies, then messages, are shortened to stay under it.</summary>
    public const int MaxChars = 60_000;

    /// <summary>Per-turn sizes, tried in order until the block fits: (message head kept, reply end kept).</summary>
    private static readonly (int Message, int Reply)[] Tiers = [(4000, 1500), (1500, 400), (600, 0)];

    public const string Intro =
        "What happened in the worker session before you were attached, oldest first: every message the worker was sent, each " +
        "followed by the last part of its reply. This is background, not a new request. The owner's job and standing " +
        "instructions are usually in here; later messages override earlier ones, and work reported done is done. Messages are " +
        "what the owner typed into the session, except those starting [AGENTHUB] (typed by AgentHub) and any an earlier " +
        "orchestrator sent.";

    public const string End = "--- End of conversation history ---";

    /// <summary>One exchange: what the session was sent, its last text in reply (null when none), and what happened meanwhile.</summary>
    public sealed record Turn(string Message, string? Reply, bool Interrupted, bool CompactedBefore);

    /// <summary>
    /// The turns in <paramref name="entries"/> (a transcript as <see cref="SessionHistoryReader"/> reads it). Prompts with no
    /// reply between them (an attached image, a message queued during a turn) are one message.
    /// </summary>
    public static List<Turn> Turns(IEnumerable<HistoryEntry> entries)
    {
        var turns = new List<Turn>();
        StringBuilder? message = null;
        string? reply = null;
        bool interrupted = false, compactedBefore = false, compactedPending = false;

        void Flush()
        {
            if (message != null) turns.Add(new Turn(message.ToString().Trim(), reply, interrupted, compactedBefore));
            message = null; reply = null; interrupted = false; compactedBefore = false;
        }

        foreach (var e in entries)
        {
            if (e.IsSubAgent) continue;
            switch (e.Kind)
            {
                case HistoryKind.Prompt:
                    if (message != null && reply == null && !interrupted) { message.Append('\n').Append(e.Text); break; }
                    Flush();
                    message = new StringBuilder(e.Text);
                    compactedBefore = compactedPending;
                    compactedPending = false;
                    break;
                case HistoryKind.Reply when message != null && e.Text.Trim().Length > 0:
                    reply = e.Text.Trim();
                    break;
                case HistoryKind.Event when e.Text == "Interrupted by user" && message != null:
                    interrupted = true;
                    break;
                case HistoryKind.Event when e.Text.StartsWith("Conversation compacted", StringComparison.Ordinal):
                    compactedPending = true;
                    break;
            }
        }
        Flush();
        return turns;
    }

    /// <summary>
    /// The block for the orchestrator; null when the session has no messages yet.
    /// </summary>
    /// <param name="lastReplyFollows">The same message carries the latest reply in full as [WORKER FINAL REPLY], so it is not repeated.</param>
    /// <param name="lastTurnRunning">The worker is still working on the latest message: its reply so far is not final.</param>
    public static string? Format(IEnumerable<HistoryEntry> entries, bool lastReplyFollows, bool lastTurnRunning)
    {
        var turns = Turns(entries);
        if (turns.Count == 0) return null;

        string[] parts = [];
        foreach (var (messageMax, replyMax) in Tiers)
        {
            parts = turns.Select((t, i) => Render(t, i + 1, i == turns.Count - 1, messageMax, replyMax, lastReplyFollows, lastTurnRunning)).ToArray();
            if (parts.Sum(p => p.Length) <= MaxChars) return Join(parts, left: 0);
        }

        // Still too long: keep the first message (usually the job) and as many of the latest as fit.
        var kept = parts[0].Length;
        var from = parts.Length;
        while (from > 1 && kept + parts[from - 1].Length <= MaxChars) kept += parts[--from].Length;
        return Join([parts[0], .. parts[from..]], left: from - 1);
    }

    private static string Render(Turn t, int n, bool last, int messageMax, int replyMax, bool lastReplyFollows, bool lastTurnRunning)
    {
        var sb = new StringBuilder();
        if (t.CompactedBefore) sb.Append("(The worker's context was compacted here: from now on it only remembers a summary of the messages above.)\n\n");
        sb.Append($"--- Message {n} to the worker ---\n");
        sb.Append(OrchestratorMessages.Cut(t.Message, last ? Math.Max(messageMax, 4000) : messageMax, keepTail: false)).Append("\n\n");

        if (last && lastReplyFollows)
            sb.Append($"--- Worker's reply to message {n} ---\n(its full final reply is in [WORKER FINAL REPLY] below)\n\n");
        else if (last && lastTurnRunning)
            sb.Append($"--- Worker's reply to message {n} ---\n")
              .Append(t.Reply == null ? "(none yet: the worker is still working on this message)" : "(still working on this message; its latest text so far:)\n" + Tail(t.Reply, 1500))
              .Append("\n\n");
        else if (t.Reply == null)
            sb.Append($"--- Worker's reply to message {n} ---\n(no text reply{(t.Interrupted ? "; the owner interrupted it" : "")})\n\n");
        else if (last || replyMax > 0)
            sb.Append($"--- Worker's reply to message {n} (last part) ---\n")
              .Append(Tail(t.Reply, last ? OrchestratorMessages.MaxWorkerReplyChars : replyMax))
              .Append(t.Interrupted ? "\n(the owner interrupted this turn)" : "")
              .Append("\n\n");
        return sb.ToString();
    }

    private static string Join(IReadOnlyList<string> parts, int left)
    {
        var sb = new StringBuilder(Intro).Append("\n\n");
        for (var i = 0; i < parts.Count; i++)
        {
            sb.Append(parts[i]);
            if (i == 0 && left > 0) sb.Append($"(… {left} more messages and replies left out to fit …)\n\n");
        }
        return sb.Append(End).ToString();
    }

    /// <summary>The last <paramref name="max"/> characters of <paramref name="text"/>, marked when cut.</summary>
    private static string Tail(string text, int max) =>
        text.Length <= max ? text : "…" + text[^max..].TrimStart();
}

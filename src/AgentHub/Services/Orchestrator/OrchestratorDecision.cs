using System.Text.Json;

namespace AgentHub.Services.Orchestrator;

public enum OrchestratorAction
{
    /// <summary>Type <see cref="OrchestratorDecision.ToWorker"/> into the worker.</summary>
    Send,
    /// <summary>Nothing now: the worker is busy or waiting on its own background job. AgentHub asks again if it stays idle.</summary>
    Wait,
    /// <summary>Ask the worker to write its handoff, then start a fresh worker session.</summary>
    Handoff,
    /// <summary>The idea is finished: stop the worker and close the run.</summary>
    Complete,
    /// <summary>Only Daniel can decide something: pause the build and show <see cref="OrchestratorDecision.Progress"/>.</summary>
    PauseForDaniel
}

/// <summary>
/// The orchestrator's one and only output, per message it is sent. The orchestrator never talks to Daniel or the worker
/// directly: AgentHub carries the decision out.
/// </summary>
public sealed record OrchestratorDecision(
    OrchestratorAction Action,
    string? ToWorker,
    string SteerHandling,
    string Reason,
    string Progress)
{
    public static string ActionName(OrchestratorAction action) => action switch
    {
        OrchestratorAction.Send => "send",
        OrchestratorAction.Wait => "wait",
        OrchestratorAction.Handoff => "handoff",
        OrchestratorAction.Complete => "complete",
        _ => "pause_for_daniel"
    };

    /// <summary>JSON schema handed to <c>claude -p --json-schema</c>.</summary>
    public const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "action": { "type": "string", "enum": ["send", "wait", "handoff", "complete", "pause_for_daniel"] },
            "to_worker": { "type": ["string", "null"], "description": "The exact message for the worker. Required for send, null otherwise." },
            "steer_handling": { "type": "string", "enum": ["relayed", "held", "none"], "description": "What you did with steers in this message: relayed in to_worker, held for a later message, or none received." },
            "reason": { "type": "string", "description": "One sentence: why this action now." },
            "progress": { "type": "string", "description": "One line on where the build stands, shown in AgentHub. For pause_for_daniel: the question for Daniel." }
          },
          "required": ["action", "to_worker", "steer_handling", "reason", "progress"]
        }
        """;

    /// <summary>
    /// Reads a decision. Throws <see cref="FormatException"/> with a message the orchestrator can act on when the JSON
    /// does not follow the rules (unknown action, send without a message).
    /// </summary>
    public static OrchestratorDecision Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The reply must be a JSON object.");

        string? Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var actionText = Str("action")?.Trim().ToLowerInvariant();
        var action = actionText switch
        {
            "send" => OrchestratorAction.Send,
            "wait" => OrchestratorAction.Wait,
            "handoff" => OrchestratorAction.Handoff,
            "complete" => OrchestratorAction.Complete,
            "pause_for_daniel" => OrchestratorAction.PauseForDaniel,
            _ => throw new FormatException($"\"action\" must be one of send, wait, handoff, complete, pause_for_daniel (got \"{actionText}\").")
        };

        var toWorker = Str("to_worker")?.Trim();
        if (action == OrchestratorAction.Send && string.IsNullOrWhiteSpace(toWorker))
            throw new FormatException("\"to_worker\" must hold the message when \"action\" is send.");

        return new OrchestratorDecision(
            action,
            action == OrchestratorAction.Send ? toWorker : null,
            Str("steer_handling")?.Trim().ToLowerInvariant() ?? "none",
            Str("reason")?.Trim() ?? "",
            Str("progress")?.Trim() ?? "");
    }
}

namespace AgentHub.Services.Orchestrator;

/// <summary>The orchestrator's mind: given one message, returns one decision. One instance per run.</summary>
public interface IOrchestratorBrain
{
    /// <summary>The orchestrator's own session id (for its transcript).</summary>
    string SessionId { get; }

    /// <summary>Throws <see cref="OrchestratorBrainException"/> when no valid decision could be had.</summary>
    Task<BrainReply> DecideAsync(string message, CancellationToken cancellationToken);
}

/// <summary>A decision, plus whether it took a retry to get valid JSON.</summary>
public sealed record BrainReply(OrchestratorDecision Decision, int Attempts, TimeSpan Elapsed);

public sealed class OrchestratorBrainException(string message, bool usageLimit = false) : Exception(message)
{
    /// <summary>The CLI reported a usage limit: the build should wait for the reset rather than pause.</summary>
    public bool UsageLimit { get; } = usageLimit;
}

/// <summary>
/// The run orchestrator: a <see cref="ClaudeJsonSession"/> whose every reply is an <see cref="OrchestratorDecision"/>.
/// It runs in the run's own folder so its transcript is kept apart from the project's sessions.
/// </summary>
public sealed class ClaudeOrchestratorBrain : IOrchestratorBrain
{
    public static readonly TimeSpan Timeout = ClaudeJsonSession.Timeout;
    public static readonly string[] StrippedEnvironment = ClaudeJsonSession.StrippedEnvironment;

    private readonly ClaudeJsonSession _session;

    public string SessionId => _session.SessionId;

    public ClaudeOrchestratorBrain(ProcessRunner runner, string command, string model, string workingDirectory, string projectDirectory,
        string systemPrompt, string? sessionId = null, bool alreadyStarted = false)
    {
        _session = new ClaudeJsonSession(runner, command, model, workingDirectory, projectDirectory, systemPrompt, OrchestratorDecision.Schema, sessionId, alreadyStarted);
    }

    public static IReadOnlyList<string> Arguments(string model, string sessionId, bool resume, string projectDirectory, string systemPrompt) =>
        ClaudeJsonSession.Arguments(model, sessionId, resume, projectDirectory, systemPrompt, OrchestratorDecision.Schema);

    public async Task<BrainReply> DecideAsync(string message, CancellationToken cancellationToken)
    {
        var (decision, attempts, elapsed) = await _session.AskAsync(message, OrchestratorDecision.Parse, cancellationToken);
        return new BrainReply(decision, attempts, elapsed);
    }

    public static bool LooksLikeUsageLimit(string text) => ClaudeJsonSession.LooksLikeUsageLimit(text);
}

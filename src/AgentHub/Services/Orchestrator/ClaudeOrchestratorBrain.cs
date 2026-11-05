using System.ComponentModel;
using System.IO;
using System.Text.Json;
using AgentHub.Services.Instructions;

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
/// A headless Claude Code session (<c>claude -p</c>) on the user's own login: the first call creates it with a fixed
/// session id, later calls resume it, so it remembers the whole build. Read-only tools on the project folder only, no
/// user settings, hooks or MCP servers, and the reply is checked against <see cref="OrchestratorDecision.Schema"/>.
/// It runs in the run's own folder so its transcript is kept apart from the project's sessions.
/// </summary>
public sealed class ClaudeOrchestratorBrain : IOrchestratorBrain
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);

    /// <summary>Inherited variables that would make the CLI bill an API key or think it is nested in another session.</summary>
    public static readonly string[] StrippedEnvironment =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_CHILD_SESSION"];

    private readonly ProcessRunner _runner;
    private readonly string _command;
    private readonly string _model;
    private readonly string _workingDirectory;
    private readonly string _projectDirectory;
    private readonly string _systemPrompt;
    private bool _started;

    public string SessionId { get; }

    public ClaudeOrchestratorBrain(ProcessRunner runner, string command, string model, string workingDirectory, string projectDirectory,
        string systemPrompt, string? sessionId = null, bool alreadyStarted = false)
    {
        _runner = runner;
        _command = command;
        _model = model;
        _workingDirectory = workingDirectory;
        _projectDirectory = projectDirectory;
        _systemPrompt = systemPrompt;
        SessionId = sessionId ?? Guid.NewGuid().ToString();
        _started = alreadyStarted;
    }

    public static IReadOnlyList<string> Arguments(string model, string sessionId, bool resume, string projectDirectory, string systemPrompt) =>
    [
        "-p", "--model", model,
        resume ? "--resume" : "--session-id", sessionId,
        "--output-format", "json", "--json-schema", OrchestratorDecision.Schema,
        "--tools", "Read,Grep,Glob", "--allowedTools", "Read", "Grep", "Glob",
        "--permission-mode", "dontAsk",
        "--setting-sources", "", "--strict-mcp-config",
        "--add-dir", projectDirectory,
        "--append-system-prompt", systemPrompt
    ];

    public async Task<BrainReply> DecideAsync(string message, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        Directory.CreateDirectory(_workingDirectory);
        var request = message;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var args = Arguments(_model, SessionId, _started, _projectDirectory, _systemPrompt);
            (int ExitCode, string StdOut, string StdErr) run;
            try
            {
                run = await _runner.RunAsync(_command, args, _workingDirectory, cancellationToken, Timeout, standardInput: request,
                    removeEnvironment: StrippedEnvironment);
            }
            catch (Win32Exception)
            {
                throw new OrchestratorBrainException($"Claude Code ({_command}) was not found, so the orchestrator cannot run.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            string problem;
            try
            {
                using var doc = ClaudePrint.ParseEnvelope(run.StdOut);
                // Whatever came back, the session now exists on disk: later calls resume it.
                if (run.StdOut.Contains("\"session_id\"", StringComparison.Ordinal)) _started = true;
                if (doc is null)
                {
                    problem = $"no JSON reply (exit code {run.ExitCode}): {ClaudePrint.Trim(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}";
                    if (LooksLikeUsageLimit(run.StdOut + run.StdErr)) throw new OrchestratorBrainException(problem, usageLimit: true);
                    if (attempt == 2) throw new OrchestratorBrainException($"The orchestrator gave no usable decision after a retry: {problem}");
                    continue;
                }
                var decision = OrchestratorDecision.Parse(doc.RootElement);
                return new BrainReply(decision, attempt, DateTimeOffset.Now - started);
            }
            catch (FormatException ex)
            {
                problem = ex.Message;
                request = $"[AGENTHUB] Your last reply could not be used: {ex.Message} Reply again with the decision JSON for the previous message.";
            }
            catch (InvalidOperationException ex)
            {
                // ParseEnvelope: the CLI reported an error (not signed in, usage limit, …).
                if (LooksLikeUsageLimit(ex.Message)) throw new OrchestratorBrainException(ex.Message, usageLimit: true);
                problem = ex.Message;
            }

            if (attempt == 2) throw new OrchestratorBrainException($"The orchestrator gave no usable decision after a retry: {problem}");
        }

        throw new OrchestratorBrainException("The orchestrator gave no usable decision after a retry.");
    }

    public static bool LooksLikeUsageLimit(string text) =>
        text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("limit reached", StringComparison.OrdinalIgnoreCase);
}

using System.ComponentModel;
using System.IO;
using System.Text.Json;
using AgentHub.Services.Instructions;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// A headless Claude Code session (<c>claude -p</c>) on the user's own login that answers every message with one JSON
/// object of a fixed schema. The first call creates it with a fixed session id, later calls resume it, so it remembers
/// the whole conversation. Read-only tools on the project folder only, no user settings, hooks or MCP servers. A reply
/// the parser refuses is sent back once with the reason, so the model can correct it. Shared by the run orchestrator
/// (<see cref="ClaudeOrchestratorBrain"/>) and the pane orchestrator (<see cref="PaneOrchestrator"/>).
/// </summary>
public sealed class ClaudeJsonSession
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
    private readonly string _schema;
    private bool _started;

    public string SessionId { get; }

    /// <param name="workingDirectory">Where the CLI runs, so its transcript is kept apart from the project's own sessions.</param>
    /// <param name="projectDirectory">The folder it may read.</param>
    public ClaudeJsonSession(ProcessRunner runner, string command, string model, string workingDirectory, string projectDirectory,
        string systemPrompt, string schema, string? sessionId = null, bool alreadyStarted = false)
    {
        _runner = runner;
        _command = command;
        _model = model;
        _workingDirectory = workingDirectory;
        _projectDirectory = projectDirectory;
        _systemPrompt = systemPrompt;
        _schema = schema;
        SessionId = sessionId ?? Guid.NewGuid().ToString();
        _started = alreadyStarted;
    }

    public static IReadOnlyList<string> Arguments(string model, string sessionId, bool resume, string projectDirectory, string systemPrompt, string schema) =>
    [
        "-p", "--model", model,
        resume ? "--resume" : "--session-id", sessionId,
        "--output-format", "json", "--json-schema", schema,
        "--tools", "Read,Grep,Glob", "--allowedTools", "Read", "Grep", "Glob",
        "--permission-mode", "dontAsk",
        "--setting-sources", "", "--strict-mcp-config",
        "--add-dir", projectDirectory,
        "--append-system-prompt", systemPrompt
    ];

    /// <summary>
    /// Sends one message and returns the parsed reply. <paramref name="parse"/> throws <see cref="FormatException"/> with a
    /// message the model can act on when the JSON does not follow the rules. Throws <see cref="OrchestratorBrainException"/>
    /// when no valid reply could be had.
    /// </summary>
    public async Task<(T Value, int Attempts, TimeSpan Elapsed)> AskAsync<T>(string message, Func<JsonElement, T> parse, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        Directory.CreateDirectory(_workingDirectory);
        var request = message;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var args = Arguments(_model, SessionId, _started, _projectDirectory, _systemPrompt, _schema);
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
                    if (attempt == 2) throw new OrchestratorBrainException($"The orchestrator gave no usable reply after a retry: {problem}");
                    continue;
                }
                return (parse(doc.RootElement), attempt, DateTimeOffset.Now - started);
            }
            catch (FormatException ex)
            {
                problem = ex.Message;
                request = $"[AGENTHUB] Your last reply could not be used: {ex.Message} Reply again with the JSON for the previous message.";
            }
            catch (InvalidOperationException ex)
            {
                // ParseEnvelope: the CLI reported an error (not signed in, usage limit, …).
                if (LooksLikeUsageLimit(ex.Message)) throw new OrchestratorBrainException(ex.Message, usageLimit: true);
                problem = ex.Message;
            }

            if (attempt == 2) throw new OrchestratorBrainException($"The orchestrator gave no usable reply after a retry: {problem}");
        }

        throw new OrchestratorBrainException("The orchestrator gave no usable reply after a retry.");
    }

    public static bool LooksLikeUsageLimit(string text) =>
        text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("limit reached", StringComparison.OrdinalIgnoreCase);
}

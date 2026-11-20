using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace AgentHub.Services.Instructions;

/// <summary>
/// Runs Claude Code in print mode (<c>claude -p</c>) on the user's own login for a one-off, schema-checked answer. No API
/// key: the request goes through the installed CLI. All tools are off and no settings or CLAUDE.md of the user's own are
/// loaded, so it can only read the text it is sent and reply; it never edits files. Shared by the instruction-file Review
/// and the timeline Review.
/// </summary>
public static class ClaudePrint
{
    /// <summary>Where the CLI runs: an empty folder, so no repository's own CLAUDE.md or settings are picked up.</summary>
    public static string WorkingDirectory { get; } = AgentHub.Hosting.AgentHubData.Folder("review");

    /// <summary>Generous: a large request can take a few minutes, and the windows show the time running.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Print mode, JSON envelope with schema-checked output, no tools, no MCP servers, no saved session, and no settings
    /// or CLAUDE.md of the user's own (they would leak into the answer), with the given system prompt.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string systemPrompt, string schema) =>
    [
        "-p", "--output-format", "json", "--json-schema", schema,
        "--tools", "", "--strict-mcp-config", "--no-session-persistence", "--setting-sources", "",
        "--system-prompt", systemPrompt
    ];

    /// <summary>
    /// Sends <paramref name="request"/> on stdin and returns the schema-checked reply (the caller disposes it). Throws
    /// <see cref="OperationCanceledException"/> when cancelled, <see cref="InvalidOperationException"/> when the CLI is
    /// missing, reports an error (not signed in, usage limit) or replies in an unexpected form.
    /// </summary>
    public static async Task<JsonDocument> RunAsync(ProcessRunner runner, string claudeCommand, string systemPrompt, string schema, string request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkingDirectory);
        (int ExitCode, string StdOut, string StdErr) run;
        try
        {
            run = await runner.RunAsync(claudeCommand, Arguments(systemPrompt, schema), WorkingDirectory, cancellationToken, Timeout, standardInput: request);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                $"Claude Code ({claudeCommand}) was not found. Install it, or set its command under Settings → Agents.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        return ParseEnvelope(run.StdOut) ?? throw new InvalidOperationException(
            $"Claude Code did not return a reply (exit code {run.ExitCode}).\n{Trim(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}");
    }

    /// <summary>
    /// Reads Claude Code's JSON envelope: the schema-checked <c>structured_output</c>, else a JSON <c>result</c>, as a
    /// document of its own. Null when stdout is not an envelope. Throws when the CLI reports an error, with its message.
    /// </summary>
    public static JsonDocument? ParseEnvelope(string stdout)
    {
        var json = stdout.Trim();
        if (!json.StartsWith('{')) return null;
        JsonDocument envelope;
        try { envelope = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (envelope)
        {
            var root = envelope.RootElement;
            var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException($"Claude Code reported an error: {Trim(result ?? json)}");

            if (root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
                return JsonDocument.Parse(structured.GetRawText());
            if (result is not null && result.TrimStart().StartsWith('{'))
            {
                try { return JsonDocument.Parse(result); }
                catch (JsonException) { return null; }
            }
            throw new InvalidOperationException($"The reply came back in an unexpected form, so nothing was changed.\n{Trim(result ?? json)}");
        }
    }

    public static string Trim(string text) => text.Length > 600 ? text[..600].TrimEnd() + "…" : text.Trim();
}

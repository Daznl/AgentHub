using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentHub.Services.Instructions;

namespace AgentHub.Services.Help;

/// <summary>One earlier line of the helper conversation, sent back so follow-up questions make sense.</summary>
public sealed record HelpTurn(bool FromUser, string Text);

/// <summary>The helper's reply and up to three follow-up questions the user may want to ask next.</summary>
public sealed record HelpAnswer(string Answer, IReadOnlyList<string> Suggestions);

/// <summary>
/// The AgentHub helper (the crab at the top right of the main window): answers questions about using AgentHub, and only that. It knows the app
/// from the README and docs packed into this build (so it always matches the installed version) and a short summary of
/// the app's current state. Runs through <see cref="ClaudePrint"/>: print mode on the user's own Claude login, all tools
/// off, no settings or CLAUDE.md of the user's own, so it can only read what it is sent and reply.
/// </summary>
public static class AppHelp
{
    public const int MaxHistoryTurns = 10;
    public const int MaxTurnLength = 3000;
    public const int MaxQuestionLength = 4000;
    public const int MaxSuggestions = 3;
    private const string DocsPrefix = "AgentHub.Docs.";

    public const string SystemPrompt = """
        You are the AgentHub helper: the pixel-art crab at the top right of AgentHub's main window, which opened this
        pane when clicked. The same crab sits in the Context window, where it is the Context helper that edits
        instruction files; here you help with the app as a whole. AgentHub is a Windows desktop app that
        runs coding agent CLIs (Claude Code, Codex, Gemini / Antigravity) in terminal panes, manages GitHub repositories,
        edits the CLIs' instruction files, shows session history and runs orchestrations. Your only job is to help the
        user understand and use AgentHub.

        - Answer from the AgentHub documentation and app state in the message. They describe this exact build.
        - Name buttons, windows, panels and settings exactly as the documentation does, in bold (for example
          **⚡ Cockpit**, **📄 Context**, **⚙ Settings**), and say where they are on screen.
        - Be short and practical: the steps to do it, or the direct answer. Use "- " bullets or "1." steps for
          sequences. Stay under about 150 words unless the user asks for detail.
        - If the documentation does not cover it, say so plainly and suggest where in the app to look. Never invent
          buttons, settings, shortcuts or behaviour. If the roadmap lists it as planned, say it is planned, not available.
        - If the question is not about AgentHub (general coding, writing, facts, other apps, the user's own projects or
          code), do not answer it: say in one sentence that you only help with AgentHub, and that a Claude Code session in
          a terminal pane can help with that instead. Questions about the CLIs are in scope only as far as how AgentHub
          starts, shows or manages them.
        - Use the app state to make answers specific (for example "your pane 2 runs Claude Code in raptor-project"), but
          do not recite it unasked. You cannot click or change anything; tell the user what to do.
        - Format: plain text, **bold** for names of things on screen, `code` for paths, commands and setting names,
          "- " bullets. No headings, tables or links.
        - "suggestions": up to three short follow-up questions about AgentHub the user might ask next, written as the
          user would ask them. Empty when none fit, and always empty after an off-topic question.
        """;

    /// <summary>The reply shape Claude Code is held to (<c>--json-schema</c>).</summary>
    public const string ReplySchema = """
        {"type":"object","additionalProperties":false,"required":["answer","suggestions"],"properties":{
          "answer":{"type":"string"},
          "suggestions":{"type":"array","maxItems":3,"items":{"type":"string"}}}}
        """;

    private static readonly Lazy<string> DocsText = new(LoadDocs);

    /// <summary>The packed documentation: README first, then the docs in name order, each in its own tag.</summary>
    public static string Docs => DocsText.Value;

    /// <summary>Names of the packed documents (README.md, ARCHITECTURE.md, ...).</summary>
    public static IReadOnlyList<string> DocNames() => typeof(AppHelp).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(DocsPrefix, StringComparison.Ordinal))
        .Select(n => n[DocsPrefix.Length..])
        .OrderBy(n => n == "README.md" ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string LoadDocs()
    {
        var assembly = typeof(AppHelp).Assembly;
        var text = new StringBuilder();
        foreach (var name in DocNames())
        {
            using var stream = assembly.GetManifestResourceStream(DocsPrefix + name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text.Append("<doc name=\"").Append(name).Append("\">\n").Append(reader.ReadToEnd().Replace("\r\n", "\n").Trim()).Append("\n</doc>\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// The message sent on stdin: the documentation, the app's state, the conversation so far (newest
    /// <see cref="MaxHistoryTurns"/> turns, each cut to <see cref="MaxTurnLength"/>), then the question.
    /// </summary>
    public static string BuildRequest(IReadOnlyList<HelpTurn> history, string question, string? appState, string? docs = null)
    {
        static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;
        var request = new StringBuilder();
        request.Append("<agenthub_docs>\n").Append(docs ?? Docs).Append("</agenthub_docs>\n\n");
        if (!string.IsNullOrWhiteSpace(appState)) request.Append("<app_state>\n").Append(appState.Trim()).Append("\n</app_state>\n\n");
        var recent = history.Skip(Math.Max(0, history.Count - MaxHistoryTurns)).ToList();
        if (recent.Count > 0)
        {
            request.Append("<conversation>\n");
            foreach (var turn in recent)
                request.Append(turn.FromUser ? "User: " : "Helper: ").Append(Cut(turn.Text.Trim(), MaxTurnLength).Replace("\r\n", "\n")).Append('\n');
            request.Append("</conversation>\n\n");
        }
        request.Append("<question>\n").Append(Cut(question.Trim(), MaxQuestionLength).Replace("\r\n", "\n")).Append("\n</question>\n");
        return request.ToString();
    }

    /// <summary>Asks the question. Throws <see cref="OperationCanceledException"/> when cancelled, <see cref="InvalidOperationException"/> on failure.</summary>
    public static async Task<HelpAnswer> RunAsync(ProcessRunner runner, string claudeCommand, IReadOnlyList<HelpTurn> history, string question,
        string? appState, CancellationToken cancellationToken)
    {
        using var reply = await ClaudePrint.RunAsync(runner, claudeCommand, SystemPrompt, ReplySchema, BuildRequest(history, question, appState), cancellationToken);
        return Read(reply.RootElement);
    }

    /// <summary>Reads a schema-checked reply.</summary>
    public static HelpAnswer Read(JsonElement element)
    {
        var answer = element.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        var suggestions = new List<string>();
        if (element.TryGetProperty("suggestions", out var list) && list.ValueKind == JsonValueKind.Array)
            suggestions.AddRange(list.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String)
                .Select(s => (s.GetString() ?? "").Trim())
                .Where(s => s.Length > 0)
                .Take(MaxSuggestions));
        return new HelpAnswer(answer.Trim(), suggestions);
    }
}

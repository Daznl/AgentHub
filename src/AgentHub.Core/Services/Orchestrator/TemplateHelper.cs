using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentHub.Services.Help;
using AgentHub.Services.Instructions;

namespace AgentHub.Services.Orchestrator;

/// <summary>What the template helper did with a request.</summary>
public enum TemplateHelperAction
{
    /// <summary>An answer or a question back; no template.</summary>
    None,
    /// <summary>A whole new template.</summary>
    Create,
    /// <summary>The template on screen, changed: every stage and step of it, not a difference.</summary>
    Change,
}

/// <summary>The helper's reply: words for the owner, follow-up questions, and a template when it built or changed one.</summary>
public sealed record TemplateHelperReply(string Answer, IReadOnlyList<string> Suggestions, TemplateHelperAction Action, AdvancedTemplate? Template, string? TemplateError);

/// <summary>The template on screen, as the helper is shown it: the draft when one is waiting, else the saved file.</summary>
public sealed record TemplateHelperTarget(string Id, AdvancedTemplate Template, bool IsDraft, bool IsNew);

/// <summary>A run that follows the template on screen: where it stands and the steps it has reached, which must stay as they are.</summary>
public sealed record TemplateHelperRun(string Pane, string State, string Progress, IReadOnlyList<string> ReachedStepIds);

/// <summary>Everything the helper is told besides the conversation.</summary>
public sealed record TemplateHelperContext(
    string ProjectFolder,
    IReadOnlyList<AdvancedTemplateFile> Files,
    TemplateHelperTarget? Target,
    TemplateHelperRun? Run,
    IReadOnlyList<string> Plans,
    IReadOnlyCollection<string> TakenStepIds);

/// <summary>
/// The template helper: the crab of the Advanced templates window (stage 6). Given a written plan it builds an advanced
/// template; given a request ("add a review step after each build") it changes the one on screen. It runs Claude Code in
/// print mode on the owner's own login with read-only tools (Read, Grep, Glob) on the project, so it can read the plan
/// and the code but change nothing; AgentHub writes what it returns as a draft only
/// (<see cref="AdvancedTemplateStore.SaveDraftAsync"/>), after <see cref="Prepare"/> has made its ids safe. The owner
/// looks at the draft in the editor and saves or discards it, so stage 5's editor is how the helper's work is checked.
/// </summary>
public static class TemplateHelper
{
    public const int MaxRequestLength = 8000;
    public const int MaxSuggestions = 3;

    /// <summary>Where the CLI runs: a folder of its own, so no repository's CLAUDE.md or settings are picked up and its prompt file has a home.</summary>
    public static string WorkingDirectory { get; } = AgentHub.Hosting.AgentHubData.Folder("template-helper");

    /// <summary>A plan of many stages, read from the project, can take a few minutes.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The helper's CLI: <paramref name="configured"/> (Settings → Agents), or AGENTHUB_TEMPLATE_HELPER_COMMAND for UI tests with a scripted helper.</summary>
    public static string Command(string configured) =>
        Environment.GetEnvironmentVariable("AGENTHUB_TEMPLATE_HELPER_COMMAND") is { Length: > 0 } c ? c : configured;

    /// <summary>What each step kind does, as the helper is told (generated from <see cref="StepKind"/>, so a new kind cannot be missed).</summary>
    public static string KindMeaning(StepKind kind) => kind switch
    {
        StepKind.Instruct => "`text` goes to the worker as its next instruction; the orchestrator judges its reply. Needs `text`.",
        StepKind.WaitForWorker => "nothing is sent; the orchestrator watches until the worker's work is finished. `text`, optional, says what to check.",
        StepKind.AskOwner => "the run stops and `text` is put to the owner; their answer carries it on. Needs `text`.",
        StepKind.NewSession => "AgentHub starts a fresh worker session in the pane (empty context).",
        StepKind.CloseSession => "AgentHub ends the worker session.",
        StepKind.Handoff => "the worker writes its handoff notes and updates the project's documents for the next session.",
        StepKind.CreateBranch => "the worker creates `branch` and switches to it. Needs `branch`.",
        StepKind.Commit => "the worker commits its work to the current branch.",
        StepKind.MergeBranch => "the worker merges `branch` into `target`, never with force. Needs `branch` and `target`.",
        StepKind.MoveWorkToBranch => "the worker moves its uncommitted work onto `branch`. Needs `branch`.",
        StepKind.OpenPullRequest => "the worker pushes the branch and opens a pull request into `target` (the trunk when left out).",
        _ => "",
    };

    public static string SystemPrompt => SystemPromptText.Value;

    private static readonly Lazy<string> SystemPromptText = new(() =>
    {
        var kinds = string.Join("\n", Enum.GetValues<StepKind>().Select(k => $"- `{AdvancedTemplate.KindName(k)}`: {KindMeaning(k)}"));
        return $$"""
            # You are the Template helper

            You are the pixel-art crab in AgentHub's Advanced templates window. The owner clicked you to open this pane
            beside a project's advanced templates. An advanced template is the sequence an orchestrator follows on a
            terminal pane, step by step: stages, and in each its steps, carried out by a coding agent (the "worker") that
            the orchestrator instructs and watches. Your job is to build these templates and change them, and to answer
            questions about them. Nothing else.

            You may read the project with Read, Grep and Glob (its folder is in the message), for example the written plan
            the owner names, its handoff notes and the code a stage touches. You cannot change any file. AgentHub takes the
            template you reply with, writes it as a draft beside the template, and opens it in the editor, where the owner
            looks it over and saves or discards it. So never say a template is saved: say the draft is open to check and save.

            ## What to reply

            - `action` "create": a new template, built from a plan or a description. `template` is the whole template.
            - `action` "change": the template on screen (given in the message as <current_template>), changed as asked.
              `template` is the whole changed template, every stage and step, not just the difference.
            - `action` "none": a question answered, or you need something from the owner first (which plan? which
              branch names?). `template` is null. Ask instead of guessing when the request leaves something important open,
              but do not ask about what a sensible default settles.
            - `answer`: short plain text for the owner (under about 120 words): what you built or changed and the choices
              they should check (branch names, where it pauses for them, the trunk). No JSON in it, no headings. Do
              not say where the draft is or how to save it: AgentHub adds that line itself.
            - `suggestions`: up to three short follow-up requests the owner might make next, as they would type them.

            ## The template

            `name` (needed), `description` (one sentence), `stages`. A stage: `id`, `title`, `steps`, and `after` only
            when the owner asks for stages that can run side by side (otherwise leave `after` null: stages run top to
            bottom). A step: `id`, `kind`, and by kind `title`, `text`, `branch`, `target`, `escalate`.

            Step kinds:
            {{kinds}}

            `escalate: true` makes a step an escalation point: anything there the orchestrator cannot settle from the
            owner's instructions pauses the run and asks the owner. Put one on each wait-for-worker step after real work.

            Rules:
            - Ids are lower-case letters, digits and dashes. Stage ids are unique; step ids are unique in the whole template.
            - A run's progress is recorded against step ids. When changing a template, keep the id of every step you keep,
              even when you change its text, and give every new step an id that is not in <taken_step_ids>. Never give the
              id of a removed step to another step.
            - When <run> lists reached steps, a run on a pane has already reached them: keep them exactly as they are,
              field for field, in their place, and put new steps only after the last of them.
            - The worker cannot see the template. Every `instruct` text must stand on its own: say what to read, what to
              build, what not to do yet ("Don't start stage 3."), and to test what it builds.
            - The worker carries out every step, git ones included. AgentHub runs no git itself.
            - No loops: a template cannot repeat a stage. Write a stage per piece of the plan instead.
            - Keep step titles short (under 60 characters); leave `title` null where the kind and text say it.

            ## Building from a plan

            Read the plan file yourself. One stage per stage (or milestone, or phase) of the plan, in its order, titled
            as the plan titles it. Unless the owner or the plan says otherwise, each stage is:
            create-branch (`feature/<short-name>`), instruct "read the context" (the plan, the handoff notes, the code the
            stage touches; change nothing yet), instruct "build" (that stage only, with tests), wait-for-worker with
            escalate, handoff, commit; then close-session, and the next stage starts with new-session. After the last
            stage: open-pull-request into the trunk (or what the owner's workflow says). Follow the owner's own workflow
            where the plan or the project's instructions state one (for example a pull request per stage, or merging each
            stage into main). Plan files AgentHub found are listed in <plan_files>.

            Questions that are not about advanced templates or this window: say in one sentence that you only help with
            advanced templates, and that a Claude Code session in a terminal pane can help with that instead.
            """;
    });

    /// <summary>The reply shape Claude Code is held to (<c>--json-schema</c>). No characters a command line treats specially.</summary>
    public static string ReplySchema => SchemaText.Value;

    private static readonly Lazy<string> SchemaText = new(() =>
    {
        var kinds = string.Join(",", Enum.GetValues<StepKind>().Select(k => $"\"{AdvancedTemplate.KindName(k)}\""));
        const string text = "{\"type\":[\"string\",\"null\"]}";
        static string Obj(string required, string properties) =>
            "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[" + required + "],\"properties\":{" + properties + "}}";
        var step = Obj("\"id\",\"kind\",\"title\",\"text\",\"branch\",\"target\",\"escalate\"",
            "\"id\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[" + kinds + "]},\"title\":" + text + ",\"text\":" + text +
            ",\"branch\":" + text + ",\"target\":" + text + ",\"escalate\":{\"type\":\"boolean\"}");
        var stage = Obj("\"id\",\"title\",\"after\",\"steps\"",
            "\"id\":{\"type\":\"string\"},\"title\":" + text + ",\"after\":{\"type\":[\"array\",\"null\"],\"items\":{\"type\":\"string\"}},\"steps\":{\"type\":\"array\",\"items\":" + step + "}");
        var template = Obj("\"name\",\"description\",\"stages\"",
            "\"name\":{\"type\":\"string\"},\"description\":" + text + ",\"stages\":{\"type\":\"array\",\"items\":" + stage + "}");
        return Obj("\"answer\",\"suggestions\",\"action\",\"template\"",
            "\"answer\":{\"type\":\"string\"},\"suggestions\":{\"type\":\"array\",\"maxItems\":3,\"items\":{\"type\":\"string\"}}," +
            "\"action\":{\"type\":\"string\",\"enum\":[\"none\",\"create\",\"change\"]},\"template\":{\"anyOf\":[{\"type\":\"null\"}," + template + "]}");
    });

    /// <summary>Print mode with read-only tools on the project, no settings, MCP servers or saved session, the prompt from a file.</summary>
    public static IReadOnlyList<string> Arguments(string projectFolder, string systemPromptFile) =>
    [
        "-p", "--output-format", "json", "--json-schema", ReplySchema,
        "--tools", "Read,Grep,Glob", "--allowedTools", "Read", "Grep", "Glob",
        "--permission-mode", "dontAsk",
        "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence",
        "--add-dir", projectFolder,
        "--append-system-prompt-file", systemPromptFile
    ];

    // ---- The request ----

    /// <summary>
    /// The message sent on stdin: the project, its templates, the one on screen (as JSON) and a run that follows it, the
    /// ids already taken, the plan files found, the conversation so far, then the request.
    /// </summary>
    public static string BuildRequest(TemplateHelperContext context, IReadOnlyList<HelpTurn> history, string request)
    {
        static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;
        var sb = new StringBuilder();
        sb.Append("<project folder=\"").Append(context.ProjectFolder).Append("\">")
          .Append(Path.GetFileName(context.ProjectFolder.TrimEnd('\\', '/'))).Append("</project>\n\n");

        sb.Append("<templates>\n");
        if (context.Files.Count == 0) sb.Append("(none yet)\n");
        foreach (var file in context.Files)
        {
            var t = file.Template ?? file.NewDraft;
            sb.Append("- ").Append(file.Id).Append(": ");
            if (t is null) sb.Append("cannot be read (").Append(file.Error).Append(')');
            else
            {
                sb.Append(t.Name).Append(" · ").Append(AdvancedTemplateTree.Summary(t));
                if (file.NewDraft is not null) sb.Append(" · new, a draft not saved yet");
                else if (file.HasDraft) sb.Append(" · a draft of a change is waiting");
                if (t.Validate().Count > 0) sb.Append(" · has problems");
            }
            if (context.Target?.Id == file.Id) sb.Append(" · ON SCREEN");
            sb.Append('\n');
        }
        sb.Append("</templates>\n\n");

        if (context.Target is { } target)
        {
            sb.Append("<current_template id=\"").Append(target.Id).Append("\" source=\"")
              .Append(target.IsDraft ? "the draft waiting to be saved" : "the saved file").Append("\">\n")
              .Append(target.Template.Serialize()).Append("</current_template>\n\n");
        }
        else sb.Append("<current_template>none: no template is on screen, so a change means building a new one</current_template>\n\n");

        if (context.Run is { } run)
        {
            sb.Append("<run pane=\"").Append(run.Pane).Append("\" state=\"").Append(run.State).Append("\">\n")
              .Append("Where it stands: ").Append(run.Progress).Append('\n')
              .Append("Reached steps (keep exactly as they are, in their place): ")
              .Append(run.ReachedStepIds.Count == 0 ? "none yet" : string.Join(", ", run.ReachedStepIds)).Append('\n')
              .Append("</run>\n\n");
        }

        if (context.TakenStepIds.Count > 0)
            sb.Append("<taken_step_ids>").Append(string.Join(", ", context.TakenStepIds.Order(StringComparer.Ordinal))).Append("</taken_step_ids>\n\n");

        sb.Append("<plan_files>").Append(context.Plans.Count == 0 ? "none found" : string.Join(", ", context.Plans)).Append("</plan_files>\n\n");

        var recent = history.Skip(Math.Max(0, history.Count - AppHelp.MaxHistoryTurns)).ToList();
        if (recent.Count > 0)
        {
            sb.Append("<conversation>\n");
            foreach (var turn in recent)
                sb.Append(turn.FromUser ? "Owner: " : "Helper: ").Append(Cut(turn.Text.Trim(), AppHelp.MaxTurnLength).Replace("\r\n", "\n")).Append('\n');
            sb.Append("</conversation>\n\n");
        }
        sb.Append("<request>\n").Append(Cut(request.Trim(), MaxRequestLength).Replace("\r\n", "\n")).Append("\n</request>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Plan files worth offering: Markdown at the top of the project and in <c>docs/</c> whose name says plan, roadmap,
    /// spec, stages, milestones or design, as paths relative to the project; a PLAN first. At most four.
    /// </summary>
    public static IReadOnlyList<string> FindPlans(string projectFolder)
    {
        string[] words = ["plan", "roadmap", "spec", "stage", "milestone", "design"];
        var found = new List<string>();
        foreach (var dir in new[] { projectFolder, Path.Combine(projectFolder, "docs") })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.md"))
                {
                    var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    if (words.Any(name.Contains)) found.Add(Path.GetRelativePath(projectFolder, path).Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found
            .OrderBy(p => Path.GetFileNameWithoutExtension(p).Contains("plan", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    // ---- Asking ----

    /// <summary>Asks the helper. Throws <see cref="OperationCanceledException"/> when cancelled, <see cref="InvalidOperationException"/> on failure.</summary>
    public static async Task<TemplateHelperReply> RunAsync(ProcessRunner runner, string command, TemplateHelperContext context,
        IReadOnlyList<HelpTurn> history, string request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkingDirectory);
        // A file per request: two windows may ask at once.
        var promptFile = Path.Combine(WorkingDirectory, $"system-prompt-{Environment.ProcessId}-{Guid.NewGuid():n}.md");
        await File.WriteAllTextAsync(promptFile, SystemPrompt, cancellationToken);
        try
        {
            (int ExitCode, string StdOut, string StdErr) run;
            try
            {
                run = await runner.RunAsync(command, Arguments(context.ProjectFolder, promptFile), WorkingDirectory, cancellationToken, Timeout,
                    standardInput: BuildRequest(context, history, request), removeEnvironment: ClaudeJsonSession.StrippedEnvironment);
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException($"Claude Code ({command}) was not found. Install it, or set its command under Settings → Agents.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var reply = ClaudePrint.ParseEnvelope(run.StdOut) ?? throw new InvalidOperationException(
                $"Claude Code did not return a reply (exit code {run.ExitCode}).\n{ClaudePrint.Trim(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}");
            return Read(reply.RootElement);
        }
        finally
        {
            try { File.Delete(promptFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads a schema-checked reply. A template that cannot be read leaves <see cref="TemplateHelperReply.Template"/> null and says why.</summary>
    public static TemplateHelperReply Read(JsonElement element)
    {
        var answer = element.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String ? (a.GetString() ?? "").Trim() : "";
        var suggestions = new List<string>();
        if (element.TryGetProperty("suggestions", out var list) && list.ValueKind == JsonValueKind.Array)
            suggestions.AddRange(list.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String)
                .Select(s => (s.GetString() ?? "").Trim())
                .Where(s => s.Length > 0)
                .Take(MaxSuggestions));
        var action = element.TryGetProperty("action", out var act) && act.ValueKind == JsonValueKind.String ? act.GetString() switch
        {
            "create" => TemplateHelperAction.Create,
            "change" => TemplateHelperAction.Change,
            _ => TemplateHelperAction.None,
        } : TemplateHelperAction.None;

        AdvancedTemplate? template = null;
        string? error = null;
        if (action != TemplateHelperAction.None)
        {
            if (!element.TryGetProperty("template", out var t) || t.ValueKind != JsonValueKind.Object)
                error = "The reply said it built a template but held none.";
            else
            {
                try { template = AdvancedTemplate.Parse(t.GetRawText(), ""); }
                catch (FormatException ex) { error = ex.Message; }
            }
            if (template is null) action = TemplateHelperAction.None;
        }
        return new TemplateHelperReply(answer, suggestions, action, template, error);
    }

    // ---- Making it safe to write ----

    /// <summary>
    /// Makes the helper's template safe to write as a draft, and says what was changed: ids made valid, a step id that
    /// was used twice, or that belonged to a step removed earlier (in <paramref name="taken"/> but not in
    /// <paramref name="based"/>), is given a new one, so no run's progress is ever read against the wrong step. The
    /// version is the current one. The template may still have problems (<see cref="AdvancedTemplate.Validate"/>):
    /// a draft with problems is fine to show, not to save.
    /// </summary>
    /// <param name="based">The template the helper changed; null when it built a new one.</param>
    /// <param name="taken">Every step id of the saved template, of its draft and of a run of it.</param>
    public static IReadOnlyList<string> Prepare(AdvancedTemplate template, AdvancedTemplate? based, IEnumerable<string> taken)
    {
        var fixes = new List<string>();
        template.Version = AdvancedTemplate.CurrentVersion;
        template.Name = template.Name.Trim();
        if (string.IsNullOrWhiteSpace(template.Description)) template.Description = null;

        // Stage ids: valid and unique. A renamed stage takes the references to it along.
        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        var n = 0;
        foreach (var stage in template.Stages)
        {
            n++;
            var id = Valid(stage.Id, $"stage-{n}");
            for (var k = 2; !stageIds.Add(id); k++) id = $"{Valid(stage.Id, $"stage-{n}")}-{k}";
            if (id != stage.Id)
            {
                if (stage.Id.Length > 0) fixes.Add($"Stage \"{stage.Id}\" was given the id \"{id}\".");
                // References follow the stage unless its old id is another stage's (it was a duplicate).
                if (!stageIds.Contains(stage.Id))
                    foreach (var other in template.Stages)
                        if (other.After is { } after)
                            for (var i = 0; i < after.Count; i++) if (after[i] == stage.Id) after[i] = id;
                stage.Id = id;
            }
        }

        // Step ids: valid, unique, and never one a removed step had.
        var keep = based?.AllSteps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var forbidden = new HashSet<string>(taken, StringComparer.Ordinal);
        forbidden.ExceptWith(keep);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in template.Stages)
        {
            foreach (var step in stage.Steps)
            {
                var id = Valid(step.Id, "");
                var why = id.Length == 0 ? "had no id"
                    : used.Contains(id) ? "was used twice"
                    : forbidden.Contains(id) ? "belonged to a step removed earlier"
                    : id != step.Id ? "was not a valid id"
                    : null;
                if (why is null)
                {
                    used.Add(id);
                    continue;
                }
                // A valid form of an invalid id will do, unless it is taken too.
                var fresh = id.Length > 0 && !used.Contains(id) && !forbidden.Contains(id) && !keep.Contains(id)
                    ? id
                    : TemplateEditing.NewStepId(template, stage, used.Concat(forbidden).Concat(keep));
                if (step.Id.Length > 0) fixes.Add($"Step \"{step.Id}\" {why}, so it is \"{fresh}\" now.");
                step.Id = fresh;
                used.Add(fresh);
            }
        }

        // Fields a kind does not use are dropped, as the editor drops them.
        foreach (var step in template.AllSteps)
        {
            if (step.Kind is not (StepKind.CreateBranch or StepKind.MoveWorkToBranch or StepKind.MergeBranch)) step.Branch = null;
            if (step.Kind is not (StepKind.MergeBranch or StepKind.OpenPullRequest)) step.Target = null;
            step.Title = string.IsNullOrWhiteSpace(step.Title) ? null : step.Title.Trim();
            step.Text = string.IsNullOrWhiteSpace(step.Text) ? null : step.Text.Trim();
            step.Branch = string.IsNullOrWhiteSpace(step.Branch) ? null : step.Branch.Trim();
            step.Target = string.IsNullOrWhiteSpace(step.Target) ? null : step.Target.Trim();
        }
        return fixes;
    }

    /// <summary><paramref name="id"/> when it is a valid id, else a slug of it; <paramref name="fallback"/> when nothing of it is left.</summary>
    private static string Valid(string? id, string fallback)
    {
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        if (System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]*$")) return id;
        return id.Any(char.IsAsciiLetterOrDigit) ? OrchestratorMessages.Slug(id) : fallback;
    }

    /// <summary>An id for a new template from its name: one no file or draft in the folder has.</summary>
    public static string NewTemplateId(string name, IEnumerable<string> existing)
    {
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var baseId = Valid(name, "template");
        var id = baseId;
        for (var k = 2; used.Contains(id); k++) id = $"{baseId}-{k}";
        return id;
    }
}

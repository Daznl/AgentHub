using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentHub.Services.Orchestrator;

/// <summary>What one step of an advanced template has the orchestrator do. Written in files as kebab-case ("wait-for-worker").</summary>
public enum StepKind
{
    /// <summary>Send <see cref="TemplateStep.Text"/> to the worker as its next instruction.</summary>
    Instruct,
    /// <summary>Watch the worker until its work for the preceding steps is finished.</summary>
    WaitForWorker,
    /// <summary>Stop and put <see cref="TemplateStep.Text"/> to the owner; carry on with their answer.</summary>
    AskOwner,
    /// <summary>Start a fresh worker session in the pane.</summary>
    NewSession,
    /// <summary>End the worker session.</summary>
    CloseSession,
    /// <summary>Have the worker write its handoff notes and update the project's documents for the next session.</summary>
    Handoff,
    /// <summary>Have the worker create <see cref="TemplateStep.Branch"/> and switch to it.</summary>
    CreateBranch,
    /// <summary>Have the worker commit its work to the current branch.</summary>
    Commit,
    /// <summary>Have the worker merge <see cref="TemplateStep.Branch"/> into <see cref="TemplateStep.Target"/>.</summary>
    MergeBranch,
    /// <summary>Have the worker move its uncommitted work onto <see cref="TemplateStep.Branch"/>.</summary>
    MoveWorkToBranch,
    /// <summary>Have the worker open a pull request into <see cref="TemplateStep.Target"/> (the trunk when not given).</summary>
    OpenPullRequest,
}

/// <summary>One step of a stage. The worker carries out every step; the orchestrator judges from the worker's reply when it is done.</summary>
public sealed class TemplateStep
{
    /// <summary>Unique in the whole template and never reused: a run's progress is recorded against it, so it survives edits.</summary>
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public StepKind Kind { get; set; }
    /// <summary>The label on the tree; <see cref="DisplayTitle"/> makes one up when empty.</summary>
    public string? Title { get; set; }
    /// <summary>The instruction for the worker, the question for the owner, or extra detail for the other kinds.</summary>
    public string? Text { get; set; }
    public string? Branch { get; set; }
    public string? Target { get; set; }
    /// <summary>An escalation point: anything here the orchestrator cannot answer from the owner's instructions pauses the run and messages the owner.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Escalate { get; set; }

    /// <summary>The same step, field for field (surrounding spaces aside): what a run that has started it can keep.</summary>
    public bool SameAs(TemplateStep other)
    {
        static string N(string? s) => s?.Trim() ?? "";
        return Id == other.Id && Kind == other.Kind && N(Title) == N(other.Title) && N(Text) == N(other.Text) &&
               N(Branch) == N(other.Branch) && N(Target) == N(other.Target) && Escalate == other.Escalate;
    }

    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
            var text = string.IsNullOrWhiteSpace(Text) ? null : OrchestratorMessages.Cut(Text.Trim().ReplaceLineEndings(" "), 60, keepTail: false).Replace("\n…(cut)", "…");
            return Kind switch
            {
                StepKind.Instruct => text ?? "Instruct the worker",
                StepKind.WaitForWorker => "Wait for the worker to finish",
                StepKind.AskOwner => text ?? "Ask the owner",
                StepKind.NewSession => "Start a new session",
                StepKind.CloseSession => "Close the session",
                StepKind.Handoff => "Hand off and update the documents",
                StepKind.CreateBranch => $"Create branch {Branch}",
                StepKind.Commit => "Commit to the branch",
                StepKind.MergeBranch => $"Merge {Branch} into {Target}",
                StepKind.MoveWorkToBranch => $"Move the work onto {Branch}",
                StepKind.OpenPullRequest => $"Open a pull request into {(string.IsNullOrWhiteSpace(Target) ? "the trunk" : Target)}",
                _ => Kind.ToString(),
            };
        }
    }
}

/// <summary>A named group of steps, run in order. Stages form a graph through <see cref="After"/>, with no loops.</summary>
public sealed class TemplateStage
{
    [JsonRequired] public string Id { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>
    /// The stages that must be finished before this one starts. Left out: the stage before it in the file (none for the
    /// first). Empty: nothing, so it can start at once, beside other stages. Two stages that do not depend on each other
    /// may run in parallel.
    /// </summary>
    public List<string>? After { get; set; }
    public List<TemplateStep> Steps { get; set; } = [];

    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Id : Title.Trim();
}

/// <summary>
/// An advanced template: the sequence an orchestrator follows step by step, as against an orchestration template
/// (<see cref="OrchestratorWorkflow"/>), which only gives it instructions and leaves the sequence to it. Stored as JSON in
/// the project's own repository (<see cref="AdvancedTemplateStore"/>) so it travels with the project. The file holds the
/// plan only; how far a run has got is recorded elsewhere, against the step ids.
/// </summary>
public sealed partial class AdvancedTemplate
{
    public const int CurrentVersion = 1;

    /// <summary>The file name without ".json"; not written into the file.</summary>
    [JsonIgnore] public string Id { get; set; } = "";
    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>The orchestrator's model; the setting's when left out.</summary>
    public string? Model { get; set; }
    public List<TemplateStage> Stages { get; set; } = [];

    [JsonIgnore] public IEnumerable<TemplateStep> AllSteps => Stages.SelectMany(s => s.Steps);

    /// <summary>
    /// The pattern the owner described (2026-11-13) as a starting point to edit: per stage of a written plan, a branch, a
    /// fresh session that reads the context and builds the stage, a watch with an escalation point, a handoff, a commit
    /// and the session closed; a pull request after the last stage.
    /// </summary>
    public static AdvancedTemplate StagedBuildExample(int stages = 2, string plan = "docs/PLAN.md")
    {
        var template = new AdvancedTemplate
        {
            Name = "Staged build",
            Description = $"Builds {plan} one stage at a time: a branch and a fresh session per stage, a handoff and a commit at the end of each.",
        };
        for (var n = 1; n <= stages; n++)
        {
            var p = $"s{n}-";
            var stage = new TemplateStage { Id = $"stage-{n}", Title = $"Stage {n}" };
            if (n > 1) stage.Steps.Add(new() { Id = p + "session", Kind = StepKind.NewSession });
            stage.Steps.AddRange(
            [
                new() { Id = p + "branch", Kind = StepKind.CreateBranch, Branch = $"feature/stage-{n}" },
                new() { Id = p + "read", Kind = StepKind.Instruct, Title = "Read the context", Text = $"Read {plan} and .agenthub/handoff.md, and the code stage {n} touches. Don't change anything yet." },
                new() { Id = p + "build", Kind = StepKind.Instruct, Title = $"Build stage {n}", Text = $"Build stage {n} of {plan}, with tests. Don't start stage {n + 1}." },
                new() { Id = p + "watch", Kind = StepKind.WaitForWorker, Escalate = true },
                new() { Id = p + "handoff", Kind = StepKind.Handoff },
                new() { Id = p + "commit", Kind = StepKind.Commit },
            ]);
            stage.Steps.Add(n < stages
                ? new() { Id = p + "close", Kind = StepKind.CloseSession }
                : new() { Id = p + "pr", Kind = StepKind.OpenPullRequest, Target = "main" });
            template.Stages.Add(stage);
        }
        return template;
    }

    // ---- File format ----

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a file people read: no ' for an apostrophe
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    /// <summary>Reads a template. Unknown properties are ignored; anything else wrong with the shape is a <see cref="FormatException"/> that says what.</summary>
    public static AdvancedTemplate Parse(string text, string id)
    {
        AdvancedTemplate? t;
        try { t = JsonSerializer.Deserialize<AdvancedTemplate>(text, Json); }
        catch (JsonException ex) { throw new FormatException($"Not a valid advanced template: {ex.Message}", ex); }
        if (t is null) throw new FormatException("Not a valid advanced template: the file is empty.");
        if (t.Version > CurrentVersion)
            throw new FormatException($"This template is version {t.Version}; this AgentHub reads up to version {CurrentVersion}. Update AgentHub.");
        t.Id = id;
        t.Stages ??= [];
        t.Stages.RemoveAll(s => s is null);
        foreach (var stage in t.Stages)
        {
            stage.Steps ??= [];
            stage.Steps.RemoveAll(s => s is null);
        }
        return t;
    }

    /// <summary>The file's text: the same template always gives the same text (line feeds, fixed order), so git shows real changes only.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Json).ReplaceLineEndings("\n") + "\n";

    /// <summary>A copy that shares nothing with this one: what a draft starts from.</summary>
    public AdvancedTemplate Clone() => Parse(Serialize(), Id);

    // ---- Checks ----

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    /// <summary>Everything that stops this template being run, in words for the owner; empty when it is sound.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("The template has no name.");
        if (Stages.Count == 0) problems.Add("The template has no stages.");

        void CheckId(string id, string what)
        {
            if (!IdPattern().IsMatch(id)) problems.Add($"{what} id \"{id}\" must be lower-case letters, digits and dashes.");
        }

        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in Stages)
        {
            CheckId(stage.Id, "Stage");
            if (!stageIds.Add(stage.Id)) problems.Add($"Two stages have the id \"{stage.Id}\".");
            if (stage.Steps.Count == 0) problems.Add($"Stage \"{stage.Id}\" has no steps.");
            foreach (var step in stage.Steps)
            {
                CheckId(step.Id, "Step");
                if (!stepIds.Add(step.Id)) problems.Add($"Two steps have the id \"{step.Id}\".");
                var missing = step.Kind switch
                {
                    StepKind.Instruct or StepKind.AskOwner when string.IsNullOrWhiteSpace(step.Text) => "text",
                    StepKind.CreateBranch or StepKind.MoveWorkToBranch or StepKind.MergeBranch when string.IsNullOrWhiteSpace(step.Branch) => "a branch",
                    StepKind.MergeBranch when string.IsNullOrWhiteSpace(step.Target) => "a target",
                    _ => null,
                };
                if (missing != null) problems.Add($"Step \"{step.Id}\" ({KindName(step.Kind)}) needs {missing}.");
            }
        }

        foreach (var stage in Stages)
        {
            foreach (var after in stage.After ?? [])
            {
                if (after == stage.Id) problems.Add($"Stage \"{stage.Id}\" comes after itself.");
                else if (!stageIds.Contains(after)) problems.Add($"Stage \"{stage.Id}\" comes after \"{after}\", which does not exist.");
            }
        }
        if (problems.Count == 0 && Order() is null) problems.Add("The stages form a loop: each waits for another. A template cannot loop.");
        return problems;
    }

    /// <summary>The name a step kind has in the file ("wait-for-worker").</summary>
    public static string KindName(StepKind kind) => JsonNamingPolicy.KebabCaseLower.ConvertName(kind.ToString());

    // ---- Order ----

    /// <summary>The stages <paramref name="stage"/> waits for (see <see cref="TemplateStage.After"/>).</summary>
    public IReadOnlyList<string> Predecessors(TemplateStage stage)
    {
        if (stage.After is { } after) return after;
        var index = Stages.IndexOf(stage);
        return index > 0 ? [Stages[index - 1].Id] : [];
    }

    /// <summary>
    /// The stages in an order that respects every <see cref="TemplateStage.After"/>; among stages free to start, the one
    /// earlier in the file comes first. This is the order a single worker follows.
    /// </summary>
    /// <exception cref="InvalidOperationException">The stages form a loop or name a stage that does not exist (<see cref="Validate"/> reports both).</exception>
    public IReadOnlyList<TemplateStage> ExecutionOrder() =>
        Order() ?? throw new InvalidOperationException("The template's stages cannot be put in order; see Validate().");

    /// <summary>Some stages do not depend on each other, so they could run side by side on separate workers.</summary>
    public bool HasParallelStages
    {
        get
        {
            // The order is the only possible one exactly when every stage waits directly for the one before it.
            var order = ExecutionOrder();
            for (var i = 1; i < order.Count; i++)
                if (!Predecessors(order[i]).Contains(order[i - 1].Id)) return true;
            return false;
        }
    }

    private List<TemplateStage>? Order()
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var left = Stages.ToList();
        var order = new List<TemplateStage>(left.Count);
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(s => Predecessors(s).All(done.Contains));
            if (next is null) return null;
            left.Remove(next);
            done.Add(next.Id);
            order.Add(next);
        }
        return order;
    }
}

/// <summary>One file in a project's template folder: the template, or why it could not be read.</summary>
public sealed record AdvancedTemplateFile(string Id, string Path, AdvancedTemplate? Template, string? Error, bool HasDraft);

/// <summary>
/// A project's advanced templates: <c>orchestration/&lt;id&gt;.json</c> in its repository, committed with it (not under
/// <c>.agenthub/</c>, which holds the owner's own notes and is often git-ignored). A change, by hand or by the helper, is
/// first written beside the template as <c>&lt;id&gt;.draft.json</c>; the view shows the draft and <see cref="AcceptDraftAsync"/>
/// puts it in the template's place once the owner saves. Drafts are git-ignored by a <c>.gitignore</c> in the folder.
/// </summary>
public sealed class AdvancedTemplateStore(string repositoryDirectory)
{
    public const string FolderName = "orchestration";
    private const string Extension = ".json";
    private const string DraftExtension = ".draft.json";

    public string Folder { get; } = Path.Combine(repositoryDirectory, FolderName);

    public string PathFor(string id) => Path.Combine(Folder, id + Extension);
    public string DraftPathFor(string id) => Path.Combine(Folder, id + DraftExtension);
    public bool HasDraft(string id) => File.Exists(DraftPathFor(id));

    /// <summary>Every template in the folder by name; a file that cannot be read is listed with its error, never left out.</summary>
    public IReadOnlyList<AdvancedTemplateFile> List()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<AdvancedTemplateFile>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*" + Extension))
        {
            if (path.EndsWith(DraftExtension, StringComparison.OrdinalIgnoreCase)) continue;
            var id = Path.GetFileNameWithoutExtension(path);
            try { list.Add(new(id, path, AdvancedTemplate.Parse(File.ReadAllText(path), id), null, HasDraft(id))); }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { list.Add(new(id, path, null, ex.Message, HasDraft(id))); }
        }
        return list.OrderBy(f => f.Template?.Name ?? f.Id, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AdvancedTemplate Read(string id) => AdvancedTemplate.Parse(File.ReadAllText(PathFor(id)), id);

    /// <summary>The draft waiting to be saved over the template; null when there is none.</summary>
    public AdvancedTemplate? ReadDraft(string id) => HasDraft(id) ? AdvancedTemplate.Parse(File.ReadAllText(DraftPathFor(id)), id) : null;

    /// <summary>Writes a sound template in place; a new one gets its id from its name.</summary>
    /// <exception cref="InvalidOperationException">The template has problems (<see cref="AdvancedTemplate.Validate"/>); nothing is written.</exception>
    public async Task SaveAsync(AdvancedTemplate template)
    {
        ThrowIfUnsound(template);
        if (string.IsNullOrEmpty(template.Id))
        {
            var baseId = OrchestratorMessages.Slug(template.Name);
            var id = baseId;
            for (var n = 2; File.Exists(PathFor(id)); n++) id = $"{baseId}-{n}";
            template.Id = id;
        }
        await WriteAsync(PathFor(template.Id), template.Serialize());
    }

    /// <summary>Writes a proposed change beside the template without touching it. A draft may be unfinished: it is checked when accepted.</summary>
    public async Task SaveDraftAsync(string id, AdvancedTemplate draft)
    {
        await WriteAsync(DraftPathFor(id), draft.Serialize());
        await IgnoreDraftsAsync();
    }

    /// <summary>The owner saved: the draft replaces the template in one step and is gone.</summary>
    /// <exception cref="FormatException">The draft cannot be read; the template is untouched and the draft kept.</exception>
    /// <exception cref="InvalidOperationException">No draft, or it has problems; the template is untouched and the draft kept.</exception>
    public async Task<AdvancedTemplate> AcceptDraftAsync(string id)
    {
        var draft = ReadDraft(id) ?? throw new InvalidOperationException($"There is no draft of \"{id}\" to save.");
        ThrowIfUnsound(draft);
        await WriteAsync(PathFor(id), draft.Serialize());
        File.Delete(DraftPathFor(id));
        return draft;
    }

    public void DiscardDraft(string id) => File.Delete(DraftPathFor(id));

    private static void ThrowIfUnsound(AdvancedTemplate template)
    {
        var problems = template.Validate();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
    }

    private async Task WriteAsync(string path, string text)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, text);
        await Hosting.SharedFile.ReplaceAsync(temp, path);
    }

    /// <summary>Keeps drafts out of the project's commits (a worker's "git add -A" would take them).</summary>
    private async Task IgnoreDraftsAsync()
    {
        const string pattern = "*" + DraftExtension;
        var path = Path.Combine(Folder, ".gitignore");
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path) : [];
        if (lines.Any(l => l.Trim() == pattern)) return;
        await File.WriteAllTextAsync(path, string.Join("\n", [.. lines, "# AgentHub: changes to a template waiting to be saved", pattern, "*.tmp"]) + "\n");
    }
}

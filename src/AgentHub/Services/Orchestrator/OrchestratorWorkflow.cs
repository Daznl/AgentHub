using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Services.Orchestrator;

public enum WorkflowHandoff
{
    /// <summary>A fresh worker only when its context window reaches the threshold (or the orchestrator asks).</summary>
    OnContext,
    /// <summary>Also a fresh worker every time the orchestrator reports a stage done.</summary>
    AfterEachStage
}

/// <summary>
/// One workflow from the catalogue: what kind of job the orchestrator is running. The core (decision JSON, message
/// format, hard limits) is the same for all; a workflow adds the orchestrator's instructions, extra worker rules and a
/// few settings. Stored as a Markdown file with a small front matter block:
/// <code>
/// ---
/// name: Data Processing
/// description: …
/// use-when: …
/// model: sonnet
/// handoff: after-each-stage
/// context-threshold: 75
/// input.pipeline_location: C:\work\pipeline.xlsx
/// ---
/// # Orchestrator instructions
/// … {{pipeline_location}} …
/// # Worker rules
/// …
/// </code>
/// </summary>
public sealed class OrchestratorWorkflow
{
    public const string InstructionsHeading = "# Orchestrator instructions";
    public const string WorkerRulesHeading = "# Worker rules";
    public const string TemplatePrefix = "template:";
    public const string DefaultId = TemplatePrefix + "builder";

    /// <summary>"template:builder" for a built-in template, else the file name without .md.</summary>
    public string Id { get; set; } = "";
    public bool IsTemplate => Id.StartsWith(TemplatePrefix, StringComparison.Ordinal);
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string UseWhen { get; set; } = "";
    /// <summary>The workflow this one was copied from (its name), for the record.</summary>
    public string? BasedOn { get; set; }
    public string Model { get; set; } = "sonnet";
    /// <summary>Overrides the idea's Agent CLI when set.</summary>
    public string? WorkerCommand { get; set; }
    public WorkflowHandoff Handoff { get; set; } = WorkflowHandoff.OnContext;
    /// <summary>Context window fraction for the forced handoff; null uses the idea's own setting.</summary>
    public double? ContextThreshold { get; set; }
    /// <summary>Named values the instructions use as {{name}} (a pipeline location, a demo user…), in file order.</summary>
    public List<KeyValuePair<string, string>> Inputs { get; set; } = [];
    public string Instructions { get; set; } = "";
    public string WorkerRules { get; set; } = "";

    public string DisplayName => IsTemplate ? $"📋 {Name} (template)" : Name;
    public override string ToString() => DisplayName;

    /// <summary>Inputs still empty or holding the template's "&lt;paste …&gt;" placeholder.</summary>
    public IReadOnlyList<string> MissingInputs() =>
        Inputs.Where(i => string.IsNullOrWhiteSpace(i.Value) || i.Value.TrimStart().StartsWith('<')).Select(i => i.Key).ToList();

    /// <summary>The orchestrator instructions with every {{input}} filled in.</summary>
    public string ResolvedInstructions() => Resolve(Instructions);
    public string ResolvedWorkerRules() => Resolve(WorkerRules);

    private string Resolve(string text)
    {
        foreach (var (key, value) in Inputs) text = text.Replace("{{" + key + "}}", value.Trim(), StringComparison.Ordinal);
        return text.Trim();
    }

    /// <summary>Short hash of the file text, so runs can be grouped by the exact version of a workflow they ran.</summary>
    public string Version() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize())))[..8].ToLowerInvariant();

    // ---- File format ----

    public static OrchestratorWorkflow Parse(string text, string id)
    {
        var w = new OrchestratorWorkflow { Id = id, Name = id.Replace(TemplatePrefix, "") };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;

        if (i < lines.Length && lines[i].Trim() == "---")
        {
            for (i++; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "name": w.Name = value; break;
                    case "description": w.Description = value; break;
                    case "use-when": w.UseWhen = value; break;
                    case "based-on": w.BasedOn = value.Length == 0 ? null : value; break;
                    case "model": if (value.Length > 0) w.Model = value; break;
                    case "worker-command": w.WorkerCommand = value.Length == 0 ? null : value; break;
                    case "handoff": w.Handoff = value.Equals("after-each-stage", StringComparison.OrdinalIgnoreCase) ? WorkflowHandoff.AfterEachStage : WorkflowHandoff.OnContext; break;
                    case "context-threshold":
                        if (double.TryParse(value.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                            w.ContextThreshold = Math.Clamp(t > 1 ? t / 100 : t, 0.40, 0.95);
                        break;
                    default:
                        if (key.StartsWith("input.") && key.Length > 6)
                            w.Inputs.Add(new(line[..colon].Trim()[6..], value));
                        break;
                }
            }
            i++;
        }

        var body = string.Join("\n", lines.Skip(i));
        w.Instructions = Section(body, InstructionsHeading) ?? body.Trim();
        w.WorkerRules = Section(body, WorkerRulesHeading) ?? "";
        return w;
    }

    private static string? Section(string body, string heading)
    {
        var start = body.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += heading.Length;
        var next = body.IndexOf("\n# ", start, StringComparison.Ordinal);
        return (next < 0 ? body[start..] : body[start..next]).Trim();
    }

    public string Serialize()
    {
        static string One(string s) => s.Replace("\r", "").Replace('\n', ' ').Trim();
        var sb = new StringBuilder("---\n");
        sb.Append("name: ").Append(One(Name)).Append('\n');
        sb.Append("description: ").Append(One(Description)).Append('\n');
        sb.Append("use-when: ").Append(One(UseWhen)).Append('\n');
        if (!string.IsNullOrWhiteSpace(BasedOn)) sb.Append("based-on: ").Append(One(BasedOn)).Append('\n');
        sb.Append("model: ").Append(One(Model)).Append('\n');
        if (!string.IsNullOrWhiteSpace(WorkerCommand)) sb.Append("worker-command: ").Append(One(WorkerCommand)).Append('\n');
        sb.Append("handoff: ").Append(Handoff == WorkflowHandoff.AfterEachStage ? "after-each-stage" : "on-context").Append('\n');
        if (ContextThreshold is { } t) sb.Append("context-threshold: ").Append(Math.Round(t * 100)).Append('\n');
        foreach (var (k, v) in Inputs) sb.Append("input.").Append(One(k)).Append(": ").Append(One(v)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(InstructionsHeading).Append("\n\n").Append(Instructions.Replace("\r\n", "\n").Trim()).Append("\n\n");
        sb.Append(WorkerRulesHeading).Append("\n\n").Append(WorkerRules.Replace("\r\n", "\n").Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>A copy to edit and save as a new workflow of the owner's own.</summary>
    public OrchestratorWorkflow Duplicate(string newName) => new()
    {
        Id = "",
        Name = newName,
        Description = Description,
        UseWhen = UseWhen,
        BasedOn = Name,
        Model = Model,
        WorkerCommand = WorkerCommand,
        Handoff = Handoff,
        ContextThreshold = ContextThreshold,
        Inputs = Inputs.Select(i => new KeyValuePair<string, string>(i.Key, i.Value)).ToList(),
        Instructions = Instructions,
        WorkerRules = WorkerRules
    };

    /// <summary>Inputs as editable text, one "name: value" per line.</summary>
    public string InputsText
    {
        get => string.Join("\n", Inputs.Select(i => $"{i.Key}: {i.Value}"));
        set => Inputs = value.Replace("\r", "").Split('\n')
            .Select(l => (l, c: l.IndexOf(':')))
            .Where(x => x.c > 0)
            .Select(x => new KeyValuePair<string, string>(x.l[..x.c].Trim(), x.l[(x.c + 1)..].Trim()))
            .Where(kv => kv.Key.Length > 0)
            .ToList();
    }
}

/// <summary>
/// The workflow catalogue: built-in templates shipped next to AgentHub.exe (read-only; copy one to change it) plus the
/// owner's own workflows in <see cref="OwnFolder"/>. That is <c>workflows/local/</c> in the AgentHub repository, which git
/// ignores, so a workflow written for one project (paths, pipeline notes) stays on this computer and never reaches
/// anyone the repository is shared with (2026-11-06; until then they were committed to an archive branch).
/// </summary>
public sealed class WorkflowCatalogue
{
    /// <summary>The folder under the repository's <c>workflows/</c> that holds the owner's own (git-ignored) workflows.</summary>
    public const string LocalSubfolder = "local";

    public string TemplatesFolder { get; }
    /// <summary>Where the owner's own workflows live.</summary>
    public string OwnFolder { get; }

    public WorkflowCatalogue(string? templatesFolder = null, string? ownFolder = null)
    {
        TemplatesFolder = templatesFolder ?? Path.Combine(AppContext.BaseDirectory, "workflows");
        OwnFolder = ownFolder ?? DefaultOwnFolder();
    }

    /// <summary>
    /// <c>workflows/local/</c> of the AgentHub repository this build came from (or Desktop\AgentHub); without a repository,
    /// <c>C:\data\agenthub\workflows</c>. <c>AGENTHUB_WORKFLOWS_DIR</c> overrides it.
    /// </summary>
    public static string DefaultOwnFolder() =>
        Environment.GetEnvironmentVariable("AGENTHUB_WORKFLOWS_DIR") is { Length: > 0 } custom ? custom
        : FindAgentHubRepository() is { } repo ? Path.Combine(repo, "workflows", LocalSubfolder)
        : @"C:\data\agenthub\workflows";

    private static string? FindAgentHubRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            // ".git" is a folder in a clone and a file in a git worktree
            if (File.Exists(Path.Combine(dir.FullName, "AgentHub.sln")) && Path.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        }
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "AgentHub");
        return Path.Exists(Path.Combine(desktop, ".git")) ? desktop : null;
    }

    public Task<List<OrchestratorWorkflow>> ListAsync()
    {
        var list = new List<OrchestratorWorkflow>();
        list.AddRange(Read(TemplatesFolder, template: true));
        list.AddRange(Read(OwnFolder, template: false));
        return Task.FromResult(list.OrderBy(w => w.IsTemplate ? 0 : 1).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<OrchestratorWorkflow> GetAsync(string? id)
    {
        var all = await ListAsync();
        return all.FirstOrDefault(w => w.Id == id)
               ?? all.FirstOrDefault(w => w.Id == OrchestratorWorkflow.DefaultId)
               ?? new OrchestratorWorkflow { Id = OrchestratorWorkflow.DefaultId, Name = "Builder" };
    }

    private static IEnumerable<OrchestratorWorkflow> Read(string folder, bool template)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.Equals("README", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            yield return OrchestratorWorkflow.Parse(text, template ? OrchestratorWorkflow.TemplatePrefix + stem : stem);
        }
    }

    /// <summary>Saves one of the owner's workflows (never a template); a new one gets an id from its name. Returns a line for the UI.</summary>
    public async Task<string> SaveAsync(OrchestratorWorkflow workflow)
    {
        if (workflow.IsTemplate) throw new InvalidOperationException("Templates are read-only; duplicate it to make your own.");
        if (string.IsNullOrWhiteSpace(workflow.Name)) throw new InvalidOperationException("Give the workflow a name.");
        if (string.IsNullOrEmpty(workflow.Id))
        {
            var all = await ListAsync();
            var baseId = RunRecorder.Slug(workflow.Name);
            var id = baseId;
            for (var n = 2; all.Any(w => w.Id == id); n++) id = $"{baseId}-{n}";
            workflow.Id = id;
        }

        Directory.CreateDirectory(OwnFolder);
        var path = Path.Combine(OwnFolder, workflow.Id + ".md");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, workflow.Serialize());
        await Hosting.SharedFile.ReplaceAsync(temp, path);
        return $"Saved to {path} (stays on this computer: never committed).";
    }

    public Task<string> DeleteAsync(OrchestratorWorkflow workflow)
    {
        if (workflow.IsTemplate) throw new InvalidOperationException("Templates cannot be deleted.");
        var path = Path.Combine(OwnFolder, workflow.Id + ".md");
        if (File.Exists(path)) File.Delete(path);
        return Task.FromResult("Deleted.");
    }
}

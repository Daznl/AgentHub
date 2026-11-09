using System.IO;
using AgentHub.Services;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class WorkflowCatalogueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AgentHub Workflows " + Guid.NewGuid().ToString("n")[..8]);

    public WorkflowCatalogueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    /// <summary>The templates shipped next to the test assembly (copied there by AgentHub.csproj).</summary>
    private static string TemplatesFolder => Path.Combine(AppContext.BaseDirectory, "workflows");

    [Fact]
    public void Shipped_templates_parse_and_data_processing_asks_for_its_pipeline_location()
    {
        var all = Directory.EnumerateFiles(TemplatesFolder, "*.md")
            .Select(f => OrchestratorWorkflow.Parse(File.ReadAllText(f), OrchestratorWorkflow.TemplatePrefix + Path.GetFileNameWithoutExtension(f)))
            .ToDictionary(w => w.Id);

        Assert.Contains(OrchestratorWorkflow.DefaultId, all.Keys);
        var builder = all["template:builder"];
        Assert.Equal("Builder", builder.Name);
        Assert.Equal(0.75, builder.ContextThreshold);
        Assert.Empty(builder.MissingInputs());
        Assert.Contains("Tell the owner it is done only when", builder.Instructions);

        var data = all["template:data-processing"];
        Assert.Equal(["pipeline_location"], data.MissingInputs());
        Assert.Contains("{{pipeline_location}}", data.Instructions);
        Assert.Contains("one pipeline stage at a time", data.Instructions);

        Assert.Equal(["demo_user"], all["template:demo-builder"].MissingInputs());
        Assert.Contains("Claude in Chrome", all["template:demo-builder"].Instructions);
    }

    [Fact]
    public void A_copy_with_its_inputs_filled_in_round_trips_and_resolves_them()
    {
        var template = OrchestratorWorkflow.Parse(File.ReadAllText(Path.Combine(TemplatesFolder, "data-processing.md")), "template:data-processing");
        var mine = template.Duplicate("Sales pipeline");
        mine.InputsText = @"pipeline_location: C:\Work Files\Sales\pipeline.xlsx";

        Assert.Equal("Data Processing", mine.BasedOn);
        Assert.False(mine.IsTemplate);
        Assert.Empty(mine.MissingInputs());
        Assert.Contains(@"C:\Work Files\Sales\pipeline.xlsx", mine.ResolvedInstructions());
        Assert.DoesNotContain("{{", mine.ResolvedInstructions());

        var again = OrchestratorWorkflow.Parse(mine.Serialize(), "sales-pipeline");
        Assert.Equal(mine.Name, again.Name);
        Assert.Equal(mine.ContextThreshold, again.ContextThreshold);
        Assert.Equal(mine.InputsText, again.InputsText);
        Assert.Equal(mine.Instructions.Trim(), again.Instructions);
        Assert.Equal(mine.Serialize(), again.Serialize());
    }

    [Fact]
    public void Keys_and_sections_from_older_files_are_ignored()
    {
        var old = "---\nname: Old\nmodel: sonnet\nhandoff: after-each-stage\nworker-command: codex\ncontext-threshold: 60\n---\n\n# Orchestrator instructions\n\nDo the thing.\n\n# Worker rules\n\nNever delete data.\n";
        var w = OrchestratorWorkflow.Parse(old, "old");
        Assert.Equal("Do the thing.", w.Instructions);
        Assert.Equal(0.60, w.ContextThreshold);
        Assert.DoesNotContain("handoff:", w.Serialize());
        Assert.DoesNotContain("Worker rules", w.Serialize());
    }

    [Fact]
    public void System_prompt_is_the_fixed_core_followed_by_the_template()
    {
        var w = new OrchestratorWorkflow { Name = "Data Processing", Description = "Stages.", Instructions = "Pipeline at {{loc}}.", Inputs = [new("loc", @"D:\p")] };
        var prompt = PaneOrchestrator.SystemPrompt(@"C:\x", "Terminal 1", w);
        Assert.Contains("Your only output is the JSON", prompt);
        Assert.True(prompt.IndexOf("to_worker", StringComparison.Ordinal) < prompt.IndexOf("===== CONTEXT: Data Processing", StringComparison.Ordinal));
        Assert.Contains(@"Pipeline at D:\p.", prompt);
        Assert.DoesNotContain("=====", PaneOrchestrator.SystemPrompt(@"C:\x", "Terminal 1", null));
    }

    [Fact]
    public async Task Catalogue_lists_templates_first_and_saves_own_workflows_with_ids_from_their_names()
    {
        var catalogue = new WorkflowCatalogue(TemplatesFolder, Path.Combine(_dir, "own"));
        var template = (await catalogue.ListAsync()).First(w => w.Id == "template:data-processing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalogue.SaveAsync(template));

        var first = template.Duplicate("Sales pipeline");
        var second = template.Duplicate("Sales pipeline");
        await catalogue.SaveAsync(first);
        await catalogue.SaveAsync(second);
        Assert.Equal("sales-pipeline", first.Id);
        Assert.Equal("sales-pipeline-2", second.Id);

        var list = await catalogue.ListAsync();
        Assert.True(list.FindIndex(w => w.IsTemplate) < list.FindIndex(w => !w.IsTemplate));
        Assert.Equal("Sales pipeline", (await catalogue.GetAsync("sales-pipeline")).Name);
        Assert.Equal(OrchestratorWorkflow.DefaultId, (await catalogue.GetAsync("gone")).Id);

        await catalogue.DeleteAsync(second);
        Assert.DoesNotContain(await catalogue.ListAsync(), w => w.Id == "sales-pipeline-2");
    }

    /// <summary>
    /// 2026-11-06, before AgentHub was shared with coworkers: the owner's own workflows (project paths, pipeline notes) live
    /// in the repository's workflows/local, which git ignores, so they can never be committed or shared.
    /// </summary>
    [Fact]
    public async Task Own_workflows_live_in_the_repositorys_git_ignored_local_folder()
    {
        var own = WorkflowCatalogue.DefaultOwnFolder();
        var repo = Path.GetDirectoryName(Path.GetDirectoryName(own))!;
        Assert.Equal(Path.Combine(repo, "workflows", WorkflowCatalogue.LocalSubfolder), own);
        Assert.True(File.Exists(Path.Combine(repo, "AgentHub.sln")), $"{own} should be in the AgentHub repository");

        var ignored = await new ProcessRunner().RunAsync("git", ["check-ignore", "-q", "workflows/local/sales-pipeline.md"], repo);
        Assert.Equal(0, ignored.ExitCode);
        var template = await new ProcessRunner().RunAsync("git", ["check-ignore", "-q", "workflows/builder.md"], repo);
        Assert.NotEqual(0, template.ExitCode); // the templates are shared
    }
}

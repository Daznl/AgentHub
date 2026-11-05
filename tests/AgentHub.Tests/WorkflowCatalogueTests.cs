using System.IO;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Ideas;
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
        Assert.Equal(WorkflowHandoff.OnContext, builder.Handoff);
        Assert.Empty(builder.MissingInputs());
        Assert.Contains("Complete only when", builder.Instructions);

        var data = all["template:data-processing"];
        Assert.Equal(WorkflowHandoff.AfterEachStage, data.Handoff);
        Assert.Equal(["pipeline_location"], data.MissingInputs());
        Assert.Contains("{{pipeline_location}}", data.Instructions);
        Assert.Contains("one pipeline stage per session", data.WorkerRules);

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
        Assert.Equal(mine.Handoff, again.Handoff);
        Assert.Equal(mine.ContextThreshold, again.ContextThreshold);
        Assert.Equal(mine.InputsText, again.InputsText);
        Assert.Equal(mine.Instructions.Trim(), again.Instructions);
        Assert.Equal(mine.WorkerRules.Trim(), again.WorkerRules);
        Assert.Equal(mine.Version(), again.Version());
    }

    [Fact]
    public void System_prompt_is_the_fixed_core_followed_by_the_workflow()
    {
        var w = new OrchestratorWorkflow { Name = "Data Processing", Handoff = WorkflowHandoff.AfterEachStage, Instructions = "Pipeline at {{loc}}.", Inputs = [new("loc", @"D:\p")] };
        var prompt = OrchestratorMessages.SystemPrompt("Sales", @"C:\x", w);
        Assert.Contains("Daniel is not in this conversation", prompt);
        Assert.True(prompt.IndexOf("stage_done", StringComparison.Ordinal) < prompt.IndexOf("===== WORKFLOW: Data Processing", StringComparison.Ordinal));
        Assert.Contains(@"Pipeline at D:\p.", prompt);
        Assert.Contains("hands off after every stage", prompt);

        var rules = OrchestratorMessages.WorkerRules(new OrchestratorWorkflow { WorkerRules = "Log row counts.\nNever delete data." });
        Assert.EndsWith("Also: Log row counts. Never delete data.)", rules);
        Assert.Equal(OrchestratorMessages.AutonomyRules, OrchestratorMessages.WorkerRules(null));
    }

    [Fact]
    public async Task Catalogue_lists_templates_first_and_saves_own_workflows_with_ids_from_their_names()
    {
        var catalogue = new WorkflowCatalogue(null, TemplatesFolder, Path.Combine(_dir, "own"));
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

    [Fact]
    public async Task Own_workflows_are_committed_to_the_archive_branch_beside_the_runs()
    {
        var repo = Path.Combine(_dir, "Agent Hub repo");
        Directory.CreateDirectory(repo);
        var runner = new ProcessRunner();
        await runner.RunAsync("git", ["init", "-q", "-b", "main"], repo);
        await runner.RunAsync("git", ["config", "user.email", "t@example.com"], repo);
        await runner.RunAsync("git", ["config", "user.name", "Test"], repo);
        await runner.RunAsync("git", ["commit", "-q", "--allow-empty", "-m", "init"], repo);

        var archive = new AgentHubArchive(runner, repo, Path.Combine(_dir, "archive worktree"));
        var catalogue = new WorkflowCatalogue(archive, TemplatesFolder);
        var mine = (await catalogue.GetAsync("template:data-processing")).Duplicate("Sales pipeline");
        mine.InputsText = @"pipeline_location: C:\Work\pipeline.xlsx";
        Assert.Contains("committed", await catalogue.SaveAsync(mine));

        var files = (await runner.RunAsync("git", ["ls-tree", "-r", "--name-only", AgentHubArchive.Branch], repo)).StdOut;
        Assert.Contains("workflows/sales-pipeline.md", files);
        Assert.Contains("README.md", files);
        var log = (await runner.RunAsync("git", ["log", "--format=%s", AgentHubArchive.Branch], repo)).StdOut;
        Assert.Contains("Workflow saved: Sales pipeline", log);
        Assert.Equal("main", (await runner.RunAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], repo)).StdOut.Trim());

        // A second catalogue on the same archive (AgentHub restarted) sees it.
        var reopened = new WorkflowCatalogue(new AgentHubArchive(runner, repo, Path.Combine(_dir, "archive worktree")), TemplatesFolder);
        Assert.Contains(await reopened.ListAsync(), w => w.Id == "sales-pipeline" && w.MissingInputs().Count == 0);
    }
}

public class WorkflowLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AgentHub Stages " + Guid.NewGuid().ToString("n")[..8]);

    public WorkflowLoopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    private static OrchestratorDecision Send(string text, string? stage = null, bool done = false) =>
        new(OrchestratorAction.Send, text, "none", "next", "working", stage, done);

    private async Task<(OrchestratorEngine, IdeaService, ProjectIdea, WorkflowCatalogue)> SetupAsync(ScriptedBrain brain)
    {
        var runner = new ProcessRunner();
        var catalogue = new WorkflowCatalogue(null, Path.Combine(AppContext.BaseDirectory, "workflows"), Path.Combine(_dir, "own"));
        var pipeline = (await catalogue.GetAsync("template:data-processing")).Duplicate("Test pipeline");
        pipeline.InputsText = @"pipeline_location: C:\Work Files\pipeline.xlsx";
        await catalogue.SaveAsync(pipeline);

        var ideas = new IdeaService(Path.Combine(_dir, "ideas.json"));
        var idea = new ProjectIdea { Title = "Sales data", Description = "Process the sales data", TargetDirectory = Path.Combine(_dir, "Sales data"), WorkflowId = pipeline.Id };
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        return (MakeEngine(ideas, brain, catalogue), ideas, idea, catalogue);
    }

    private OrchestratorEngine MakeEngine(IdeaService ideas, ScriptedBrain brain, WorkflowCatalogue catalogue)
    {
        var runner = new ProcessRunner();
        return new OrchestratorEngine(ideas, new GitService(runner), runner, new SteeringService(Path.Combine(_dir, "steering.json")))
        {
            RunsRoot = Path.Combine(_dir, "runs"),
            Workflows = catalogue,
            BrainFactory = (_, _) => brain,
            UsageReader = () => Task.FromResult<UsageSnapshot?>(null),
            TurnSettle = TimeSpan.Zero,
            WorkerStartupDelay = TimeSpan.Zero,
            LoopInterval = TimeSpan.FromMilliseconds(50),
            IdleRecheck = TimeSpan.FromHours(1)
        };
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    private RunRecord Record() => RunRecorder.ReadRecord(Path.Combine(Directory.GetDirectories(Path.Combine(_dir, "runs")).Single(), "run.json"))!;

    [Fact]
    public async Task A_finished_stage_starts_a_fresh_worker_and_the_run_remembers_the_stages()
    {
        var brain = new ScriptedBrain(
            Send("Read the pipeline and list the stages, then run stage 1 only.", stage: "1 clean"),
            Send("(ignored: stage done means a fresh worker)", stage: "1 clean", done: true),
            Send("Report on stage 1 first.", stage: "2 join"));
        var (engine, _, idea, _) = await SetupAsync(brain);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");
            Assert.Contains(@"C:\Work Files\pipeline.xlsx", OrchestratorMessages.SystemPrompt(idea.Title, idea.TargetDirectory, engine.CurrentWorkflow));
            Assert.Contains("one pipeline stage per session", worker.Received[0]);
            Assert.Contains("[WORKFLOW] Test pipeline (fresh worker after each stage)", brain.Messages[0]);

            worker.FinishTurn("Stage 1 done: 10,000 rows cleaned, output in out/1.");
            await Until(() => worker.Received.Count == 2, "handoff prompt");
            Assert.Equal(OrchestratorMessages.HandoffPrompt, worker.Received[1]);

            worker.FinishTurn("Handoff written.");
            await Until(() => worker.Received.Count == 3, "the fresh worker's first instruction");
            Assert.Equal(2, worker.Starts);
            Assert.StartsWith("[EVENT] new_worker_session", brain.Messages[2]);
            Assert.Contains("stages done: 1 clean", brain.Messages[2]);
            Assert.StartsWith("Report on stage 1 first.", worker.Received[2]);

            var r = engine.CurrentRun!;
            Assert.Equal(["1 clean"], r.StagesDone);
            Assert.Equal("2 join", r.CurrentStage);
            Assert.Equal("Test pipeline", r.Workflow);
        }
        Assert.True(File.Exists(Path.Combine(Directory.GetDirectories(Path.Combine(_dir, "runs")).Single(), "workflow.md")));
    }

    [Fact]
    public async Task Closing_AgentHub_leaves_the_run_open_and_building_again_picks_it_up_with_the_same_orchestrator()
    {
        var day1 = new ScriptedBrain(Send("Run stage 1 only.", stage: "1 clean"));
        var (engine, ideas, idea, catalogue) = await SetupAsync(day1);
        var worker = new ScriptedWorker();
        await engine.BuildIdeaImmediatelyAsync(idea, worker);
        await Until(() => worker.Received.Count == 1, "day 1 instruction");
        engine.Dispose(); // AgentHub closed for the night

        var suspended = Record();
        Assert.Equal("suspended", suspended.Outcome);
        Assert.Equal("1 clean", suspended.CurrentStage);

        var day2 = new ScriptedBrain(Send("Report on stage 1, then carry on.", stage: "1 clean"));
        var sessions = new List<string>();
        using var engine2 = MakeEngine(ideas, day2, catalogue);
        engine2.BrainFactory = (record, _) => { sessions.Add(record.OrchestratorSessionId); return day2; };
        var worker2 = new ScriptedWorker();
        await engine2.BuildIdeaImmediatelyAsync(idea, worker2);
        await Until(() => worker2.Received.Count == 1, "day 2 instruction");

        Assert.StartsWith("[EVENT] run_reopened", day2.Messages[0]);
        Assert.Contains("current stage: 1 clean", day2.Messages[0]);
        Assert.Equal(["scripted"], sessions); // the orchestrator session id of day 1, handed back
        var reopened = engine2.CurrentRun!;
        Assert.Equal(suspended.RunId, reopened.RunId);
        Assert.Equal(1, reopened.Reopened);
        Assert.Equal(2, reopened.WorkerSessions.Count);
        Assert.Single(Directory.GetDirectories(Path.Combine(_dir, "runs")));
    }

    [Fact]
    public async Task Work_mode_skips_an_idea_whose_workflow_inputs_are_not_filled_in()
    {
        var brain = new ScriptedBrain();
        var (engine, ideas, idea, _) = await SetupAsync(brain);
        idea.WorkflowId = "template:data-processing"; // the template, still "<paste …>"
        await ideas.UpdateIdeaAsync(idea);
        using (engine)
        {
            await engine.StartAsync(); // no pane yet: nothing to do
            await engine.BuildIdeaImmediatelyAsync(idea, new ScriptedWorker());
            await Until(() => engine.State == OrchestratorState.Idle, "idle");
        }
        Assert.Empty(brain.Messages);
        var saved = (await ideas.LoadAsync()).Single();
        Assert.Equal(IdeaStatus.Paused, saved.Status);
        Assert.Contains("pipeline_location", saved.StatusNote);
    }
}

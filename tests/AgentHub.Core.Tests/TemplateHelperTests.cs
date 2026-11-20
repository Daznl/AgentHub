using System.IO;
using System.Text.Json;
using AgentHub.Services;
using AgentHub.Services.Help;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// The Template helper (stage 6): what it is told, the reply it is held to, how its template is made safe to write as a
/// draft (ids above all: a run's progress is recorded against them), and new templates that exist only as a draft.
/// </summary>
public class TemplateHelperTests : IDisposable
{
    // A folder with spaces, as the owner's are.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentHub Helper " + Guid.NewGuid().ToString("n")[..8]);

    public TemplateHelperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private static string[] Ids(AdvancedTemplate t) => t.AllSteps.Select(s => s.Id).ToArray();

    private TemplateHelperContext Context(TemplateHelperTarget? target = null, TemplateHelperRun? run = null, IReadOnlyList<string>? plans = null) =>
        new(_root, [], target, run, plans ?? [], target?.Template.AllSteps.Select(s => s.Id).ToHashSet() ?? []);

    // ---- What it is told and held to ----

    [Fact]
    public void The_reply_schema_is_json_that_names_every_step_kind_and_nothing_a_command_line_treats_specially()
    {
        using var schema = JsonDocument.Parse(TemplateHelper.ReplySchema);
        foreach (var kind in Enum.GetValues<StepKind>()) Assert.Contains($"\"{AdvancedTemplate.KindName(kind)}\"", TemplateHelper.ReplySchema);
        Assert.Equal(["answer", "suggestions", "action", "template"], schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(TemplateHelper.ReplySchema, c => c is '<' or '>' or '|' or '&' or '^' or '%' or '\n');
    }

    [Fact]
    public void The_system_prompt_explains_every_step_kind_and_the_id_rules()
    {
        foreach (var kind in Enum.GetValues<StepKind>())
        {
            Assert.Contains($"`{AdvancedTemplate.KindName(kind)}`", TemplateHelper.SystemPrompt);
            Assert.False(string.IsNullOrEmpty(TemplateHelper.KindMeaning(kind)));
        }
        Assert.Contains("taken_step_ids", TemplateHelper.SystemPrompt);
        Assert.Contains("Never give the", TemplateHelper.SystemPrompt);
        Assert.Contains("draft", TemplateHelper.SystemPrompt);
    }

    [Fact]
    public void It_may_read_the_project_and_nothing_more()
    {
        var args = TemplateHelper.Arguments(_root, @"C:\x\prompt.md");
        Assert.Equal(_root, args[args.ToList().IndexOf("--add-dir") + 1]);
        Assert.Equal("Read,Grep,Glob", args[args.ToList().IndexOf("--tools") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("Write") || a.Contains("Edit") || a.Contains("Bash"));
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("", args[args.ToList().IndexOf("--setting-sources") + 1]);
    }

    [Fact]
    public void The_request_holds_the_template_on_screen_the_run_the_taken_ids_the_plans_the_conversation_and_the_request_in_order()
    {
        var template = AdvancedTemplate.StagedBuildExample();
        template.Id = "staged-build";
        var context = Context(new TemplateHelperTarget("staged-build", template, IsDraft: true, IsNew: false),
            new TemplateHelperRun("Terminal 1", "paused", "step 2 of 15", ["s1-branch", "s1-read"]), ["docs/PLAN.md"]);
        var request = TemplateHelper.BuildRequest(context, [new HelpTurn(true, "Hi"), new HelpTurn(false, "Hello")], "  Add a review step\r\n");

        string[] order = ["<project folder=\"" + _root, "<templates>", "<current_template id=\"staged-build\" source=\"the draft", "\"s1-build\"",
            "<run pane=\"Terminal 1\" state=\"paused\">", "s1-branch, s1-read", "<taken_step_ids>", "<plan_files>docs/PLAN.md", "<conversation>",
            "Owner: Hi", "Helper: Hello", "<request>\nAdd a review step\n</request>"];
        var at = 0;
        foreach (var part in order)
        {
            var i = request.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(i >= 0, $"missing or out of order: {part}");
            at = i;
        }
    }

    [Fact]
    public void With_no_template_on_screen_the_request_says_a_change_means_a_new_one()
    {
        var request = TemplateHelper.BuildRequest(Context(), [], "Build one");
        Assert.Contains("no template is on screen", request);
        Assert.Contains("<templates>\n(none yet)", request);
        Assert.Contains("<plan_files>none found", request);
        Assert.DoesNotContain("<run", request);
    }

    // ---- Its reply ----

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private const string Built = """
        {"answer":"Built it.","suggestions":["Add a review","",  "Pull request per stage"],"action":"create","template":
          {"name":"Plan build","description":null,"stages":[
            {"id":"stage-1","title":"Data model","after":null,"steps":[
              {"id":"s1-branch","kind":"create-branch","title":null,"text":null,"branch":"feature/data","target":null,"escalate":false},
              {"id":"s1-build","kind":"instruct","title":"Build it","text":"Build stage 1.","branch":null,"target":null,"escalate":false},
              {"id":"s1-watch","kind":"wait-for-worker","title":null,"text":null,"branch":null,"target":null,"escalate":true}]}]}}
        """;

    [Fact]
    public void A_built_template_is_read_from_the_reply()
    {
        var reply = TemplateHelper.Read(Json(Built));
        Assert.Equal(TemplateHelperAction.Create, reply.Action);
        Assert.Equal("Built it.", reply.Answer);
        Assert.Equal(["Add a review", "Pull request per stage"], reply.Suggestions);
        Assert.NotNull(reply.Template);
        Assert.Equal(["s1-branch", "s1-build", "s1-watch"], Ids(reply.Template!));
        Assert.Equal(StepKind.WaitForWorker, reply.Template!.AllSteps.Last().Kind);
        Assert.True(reply.Template.AllSteps.Last().Escalate);
        Assert.Empty(reply.Template.Validate());
    }

    [Fact]
    public void An_answer_alone_has_no_template()
    {
        var reply = TemplateHelper.Read(Json("""{"answer":"It pauses the run.","suggestions":[],"action":"none","template":null}"""));
        Assert.Equal(TemplateHelperAction.None, reply.Action);
        Assert.Null(reply.Template);
        Assert.Null(reply.TemplateError);
    }

    [Theory]
    [InlineData("""{"answer":"x","suggestions":[],"action":"change","template":null}""")]
    [InlineData("""{"answer":"x","suggestions":[],"action":"create","template":{"name":"x","stages":[{"id":"a","steps":[{"id":"b","kind":"dance"}]}]}}""")]
    public void A_template_that_cannot_be_read_is_not_written_and_the_reply_says_why(string json)
    {
        var reply = TemplateHelper.Read(Json(json));
        Assert.Equal(TemplateHelperAction.None, reply.Action);
        Assert.Null(reply.Template);
        Assert.False(string.IsNullOrEmpty(reply.TemplateError));
    }

    // ---- Making it safe ----

    [Fact]
    public void A_change_keeps_the_ids_of_the_steps_it_keeps()
    {
        var based = AdvancedTemplate.StagedBuildExample();
        var changed = based.Clone();
        changed.Stages[0].Steps.Insert(4, new TemplateStep { Id = "s1-review", Kind = StepKind.Instruct, Text = "Review the diff." });
        var fixes = TemplateHelper.Prepare(changed, based, Ids(based));
        Assert.Empty(fixes);
        Assert.Equal(Ids(based).Length + 1, Ids(changed).Length);
        Assert.Contains("s1-review", Ids(changed));
        Assert.Empty(changed.Validate());
    }

    [Fact]
    public void An_id_used_twice_or_left_by_a_removed_step_is_given_a_new_one()
    {
        var based = AdvancedTemplate.StagedBuildExample();
        var changed = based.Clone();
        changed.Stages[0].Steps.Add(new TemplateStep { Id = "s1-build", Kind = StepKind.Instruct, Text = "A second build." });  // used twice
        changed.Stages[1].Steps.Add(new TemplateStep { Id = "old-lint", Kind = StepKind.Instruct, Text = "Lint." });          // a removed step's
        var fixes = TemplateHelper.Prepare(changed, based, Ids(based).Append("old-lint"));

        Assert.Equal(2, fixes.Count);
        Assert.Contains(fixes, f => f.Contains("\"s1-build\" was used twice"));
        Assert.Contains(fixes, f => f.Contains("\"old-lint\" belonged to a step removed earlier"));
        Assert.Equal(Ids(changed).Length, Ids(changed).Distinct().Count());
        Assert.DoesNotContain("old-lint", Ids(changed));
        Assert.Equal("Build stage 1 of docs/PLAN.md, with tests. Don't start stage 2.", changed.AllSteps.First(s => s.Id == "s1-build").Text); // the first keeps it
        Assert.Empty(changed.Validate());
    }

    [Fact]
    public void Ids_that_are_not_valid_are_made_valid_and_references_to_a_stage_follow_it()
    {
        var t = new AdvancedTemplate
        {
            Name = "  Plan  ",
            Stages =
            [
                new TemplateStage { Id = "API Work", Steps = [new TemplateStep { Id = "Step One", Kind = StepKind.Commit, Branch = "x", Text = "  " }] },
                new TemplateStage { Id = "ui", After = ["API Work"], Steps = [new TemplateStep { Id = "", Kind = StepKind.Handoff }] },
            ],
        };
        var fixes = TemplateHelper.Prepare(t, null, []);
        Assert.Equal("Plan", t.Name);
        Assert.Equal("api-work", t.Stages[0].Id);
        Assert.Equal(["api-work"], t.Stages[1].After);
        Assert.Equal("step-one", t.Stages[0].Steps[0].Id);
        Assert.Null(t.Stages[0].Steps[0].Branch); // a commit has no branch
        Assert.Null(t.Stages[0].Steps[0].Text);
        Assert.Equal("ui-step-1", t.Stages[1].Steps[0].Id);
        Assert.Equal(2, fixes.Count); // the empty id needs no word: it had none
        Assert.Empty(t.Validate());
    }

    [Fact]
    public void A_long_valid_id_is_kept_as_it_is()
    {
        var id = "a-very-long-but-perfectly-valid-step-id-that-goes-on-and-on";
        var t = new AdvancedTemplate { Name = "x", Stages = [new TemplateStage { Id = "s", Steps = [new TemplateStep { Id = id, Kind = StepKind.Commit }] }] };
        Assert.Empty(TemplateHelper.Prepare(t, null, []));
        Assert.Equal(id, t.AllSteps.Single().Id);
    }

    [Fact]
    public void A_draft_whose_stages_loop_can_still_be_written_and_the_file_holds_only_the_plan()
    {
        // The helper may return stages that wait for each other: the draft is written and shown with its problem.
        var t = new AdvancedTemplate
        {
            Name = "Loop",
            Stages =
            [
                new TemplateStage { Id = "a", After = ["b"], Steps = [new TemplateStep { Id = "a1", Kind = StepKind.Commit }] },
                new TemplateStage { Id = "b", After = ["a"], Steps = [new TemplateStep { Id = "b1", Kind = StepKind.Commit }] },
            ],
        };
        TemplateHelper.Prepare(t, null, []);
        var text = t.Serialize();
        Assert.DoesNotContain("hasParallelStages", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(t.Clone().Validate(), p => p.Contains("loop"));
        Assert.DoesNotContain("hasParallelStages", AdvancedTemplate.StagedBuildExample().Serialize(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_new_template_gets_an_id_no_file_or_draft_has()
    {
        Assert.Equal("plan-build", TemplateHelper.NewTemplateId("Plan build", ["staged-build"]));
        Assert.Equal("plan-build-3", TemplateHelper.NewTemplateId("Plan build", ["plan-build", "Plan-Build-2"]));
        Assert.Equal("template", TemplateHelper.NewTemplateId("!!", []));
    }

    [Fact]
    public void A_change_a_run_cannot_take_is_written_all_the_same_and_the_run_says_why()
    {
        // The helper may change a template a run follows: as a draft, which Apply then refuses if it touches reached steps.
        var t = SampleTemplates.StagedBuild();
        var run = new TemplateRun(t);
        run.Start();
        var changed = t.Clone();
        changed.AllSteps.First().Text = "Changed by the helper.";
        Assert.Empty(TemplateHelper.Prepare(changed, t, Ids(t)));
        Assert.NotEmpty(run.ProblemsWith(changed));
    }

    // ---- Plans ----

    [Fact]
    public void Plan_files_at_the_top_and_in_docs_are_found_a_plan_first()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "deep"));
        foreach (var f in new[] { "README.md", "ROADMAP.md", "docs/PLAN.md", "docs/ARCHITECTURE.md", "docs/design-notes.md", "docs/deep/plan.md", "plan.txt" })
            File.WriteAllText(Path.Combine(_root, f), "x");
        Assert.Equal(["docs/PLAN.md", "ROADMAP.md", "docs/design-notes.md"], TemplateHelper.FindPlans(_root));
        Assert.Empty(TemplateHelper.FindPlans(Path.Combine(_root, "missing")));
    }

    // ---- A new template is only a draft until it is saved ----

    [Fact]
    public async Task A_new_template_is_listed_from_its_draft_and_becomes_a_file_when_saved()
    {
        var store = new AdvancedTemplateStore(_root);
        await store.SaveAsync(AdvancedTemplate.StagedBuildExample());
        var made = TemplateHelper.Read(Json(Built)).Template!;
        made.Id = "plan-build";
        await store.SaveDraftAsync("plan-build", made);

        var listed = store.List();
        var fresh = listed.Single(f => f.Id == "plan-build");
        Assert.True(fresh.IsNew);
        Assert.Null(fresh.Template);
        Assert.Equal("Plan build", fresh.NewDraft!.Name);
        Assert.False(File.Exists(fresh.Path));
        Assert.False(listed.Single(f => f.Id == "staged-build").IsNew);

        await store.AcceptDraftAsync("plan-build");
        var saved = store.List().Single(f => f.Id == "plan-build");
        Assert.False(saved.IsNew);
        Assert.False(saved.HasDraft);
        Assert.Equal("Plan build", saved.Template!.Name);
    }

    [Fact]
    public async Task A_new_template_whose_draft_cannot_be_read_is_listed_with_its_error()
    {
        var store = new AdvancedTemplateStore(_root);
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllTextAsync(store.DraftPathFor("broken"), "{ not json");
        var broken = store.List().Single();
        Assert.Equal("broken", broken.Id);
        Assert.False(broken.IsNew);
        Assert.Contains("cannot be read", broken.Error);
    }

    // ---- The CLI round trip ----

    /// <summary>
    /// A stand-in for the claude command in a folder with spaces: a batch file on Windows, a shell script on Linux. Each
    /// saves its standard input as request.txt beside itself (when asked to) and prints <paramref name="envelope"/>.
    /// </summary>
    private async Task<string> FakeCliAsync(string envelope, bool keepRequest)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_root, "fake helper.cmd");
            await File.WriteAllTextAsync(cmd, "@echo off\r\nmore > " + (keepRequest ? "\"%~dp0request.txt\"" : "nul") + "\r\necho " + envelope.Replace("%", "%%") + "\r\n");
            return cmd;
        }

        var script = Path.Combine(_root, "fake helper");
        await File.WriteAllTextAsync(script, "#!/bin/sh\ncat > " + (keepRequest ? "\"$(dirname \"$0\")/request.txt\"" : "/dev/null") + "\ncat <<'END_OF_ENVELOPE'\n" + envelope + "\nEND_OF_ENVELOPE\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [Fact]
    public async Task It_runs_the_command_with_the_request_on_stdin_and_reads_its_envelope()
    {
        var envelope = JsonSerializer.Serialize(new { type = "result", is_error = false, result = "", structured_output = JsonDocument.Parse(Built).RootElement });
        var fake = await FakeCliAsync(envelope, keepRequest: true);

        var reply = await TemplateHelper.RunAsync(new ProcessRunner(), fake, Context(), [], "Build one from docs/PLAN.md", CancellationToken.None);
        Assert.Equal(TemplateHelperAction.Create, reply.Action);
        Assert.Equal("Plan build", reply.Template!.Name);
        Assert.Contains("Build one from docs/PLAN.md", await File.ReadAllTextAsync(Path.Combine(_root, "request.txt")));
        Assert.Empty(Directory.GetFiles(TemplateHelper.WorkingDirectory, $"system-prompt-{Environment.ProcessId}-*")); // its prompt file is gone
    }

    /// <summary>The real helper reads a plan from the project and builds a sound template from it, then changes it on request.</summary>
    [LiveFact("claude", optIn: "AGENTHUB_LIVE_REVIEW")]
    public async Task Live_it_builds_a_sound_template_from_a_plan_and_changes_it()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "PLAN.md"), """
            # Recipe box: plan

            ## Stage 1: storage
            A SQLite file of recipes (title, ingredients, steps) with a small repository class and its tests.

            ## Stage 2: command line
            `recipes add`, `recipes list` and `recipes show <id>` on top of the repository, with tests.

            ## Stage 3: search
            `recipes find <word>` over titles and ingredients, with tests.

            Workflow: a branch per stage, a pull request into main at the end of each stage.
            """);
        var context = new TemplateHelperContext(_root, [], null, null, TemplateHelper.FindPlans(_root), new HashSet<string>());
        var built = await TemplateHelper.RunAsync(new ProcessRunner(), "claude", context, [], "Build an advanced template from docs/PLAN.md.", CancellationToken.None);

        Assert.Equal(TemplateHelperAction.Create, built.Action);
        var t = built.Template!;
        TemplateHelper.Prepare(t, null, []);
        Assert.Empty(t.Validate());
        Assert.Equal(3, t.Stages.Count);
        Assert.Equal(3, t.AllSteps.Count(s => s.Kind == StepKind.OpenPullRequest));
        Assert.Contains(t.AllSteps, s => s.Kind == StepKind.WaitForWorker && s.Escalate);

        t.Id = "recipe-box";
        var target = new TemplateHelperTarget("recipe-box", t, IsDraft: false, IsNew: false);
        var ids = t.AllSteps.Select(s => s.Id).ToHashSet();
        var changed = await TemplateHelper.RunAsync(new ProcessRunner(), "claude", context with { Target = target, TakenStepIds = ids },
            [new HelpTurn(true, "Build an advanced template from docs/PLAN.md."), new HelpTurn(false, built.Answer)],
            "Add an ask-owner step before each pull request that asks me to look at the diff first.", CancellationToken.None);

        Assert.Equal(TemplateHelperAction.Change, changed.Action);
        var c = changed.Template!;
        Assert.Empty(TemplateHelper.Prepare(c, t, ids));
        Assert.Empty(c.Validate());
        Assert.Equal(3, c.AllSteps.Count(s => s.Kind == StepKind.AskOwner) - t.AllSteps.Count(s => s.Kind == StepKind.AskOwner));
        Assert.True(ids.IsSubsetOf(c.AllSteps.Select(s => s.Id)), "every step it kept keeps its id");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "agenthub-live-template.json"), c.Serialize());
    }

    [Fact]
    public async Task An_error_from_the_cli_is_a_failure_with_its_message()
    {
        var fake = await FakeCliAsync("{\"type\":\"result\",\"is_error\":true,\"result\":\"Not signed in\"}", keepRequest: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TemplateHelper.RunAsync(new ProcessRunner(), fake, Context(), [], "x", CancellationToken.None));
        Assert.Contains("Not signed in", ex.Message);
    }
}

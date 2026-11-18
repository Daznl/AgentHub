using System.IO;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class AdvancedTemplateTests : IDisposable
{
    // A repository path with spaces, as the owner's are.
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "AgentHub Advanced Templates " + Guid.NewGuid().ToString("n")[..8]);

    public AdvancedTemplateTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch { }
    }

    /// <summary>The owner's example (2026-11-13): branch, read, build, watch, hand off, commit, close; then the same for stage 2.</summary>
    private const string StagedBuild = """
        {
          // comments and trailing commas are read, since people edit these by hand
          "version": 1,
          "name": "Staged build",
          "description": "Builds the plan one stage at a time, a fresh session per stage.",
          "stages": [
            {
              "id": "stage-1",
              "title": "Stage 1: data model",
              "steps": [
                { "id": "s1-branch", "kind": "create-branch", "branch": "feature/data-model" },
                { "id": "s1-read", "kind": "instruct", "text": "Read docs/PLAN.md and .agenthub/handoff.md." },
                { "id": "s1-build", "kind": "instruct", "title": "Build stage 1", "text": "Build stage 1 of docs/PLAN.md. Don't start stage 2." },
                { "id": "s1-watch", "kind": "wait-for-worker", "escalate": true },
                { "id": "s1-handoff", "kind": "handoff" },
                { "id": "s1-commit", "kind": "commit" },
                { "id": "s1-close", "kind": "close-session" },
              ]
            },
            {
              "id": "stage-2",
              "title": "Stage 2: screens",
              "steps": [
                { "id": "s2-new", "kind": "new-session" },
                { "id": "s2-build", "kind": "instruct", "text": "Read .agenthub/handoff.md, then build stage 2 of docs/PLAN.md." },
                { "id": "s2-watch", "kind": "wait-for-worker", "escalate": true },
                { "id": "s2-check", "kind": "ask-owner", "text": "Stage 2 is built. Open the pull request?" },
                { "id": "s2-pr", "kind": "open-pull-request", "target": "main" }
              ]
            }
          ]
        }
        """;

    private static AdvancedTemplate Template(params TemplateStage[] stages) => new() { Name = "T", Stages = [.. stages] };

    private static TemplateStage Stage(string id, string[]? after = null) =>
        new() { Id = id, After = after?.ToList(), Steps = [new TemplateStep { Id = id + "-go", Kind = StepKind.Commit }] };

    [Fact]
    public void The_staged_build_example_reads_is_sound_and_runs_its_stages_in_file_order()
    {
        var t = AdvancedTemplate.Parse(StagedBuild, "staged-build");

        Assert.Equal("staged-build", t.Id);
        Assert.Equal("Staged build", t.Name);
        Assert.Empty(t.Validate());
        Assert.Equal(["stage-1", "stage-2"], t.ExecutionOrder().Select(s => s.Id));
        Assert.False(t.HasParallelStages);
        Assert.Equal(12, t.AllSteps.Count());

        var watch = t.Stages[0].Steps[3];
        Assert.Equal(StepKind.WaitForWorker, watch.Kind);
        Assert.True(watch.Escalate);
        Assert.False(t.Stages[0].Steps[0].Escalate);

        Assert.Equal("Create branch feature/data-model", t.Stages[0].Steps[0].DisplayTitle);
        Assert.Equal("Read docs/PLAN.md and .agenthub/handoff.md.", t.Stages[0].Steps[1].DisplayTitle);
        Assert.Equal("Build stage 1", t.Stages[0].Steps[2].DisplayTitle);
        Assert.Equal("Open a pull request into main", t.Stages[1].Steps[4].DisplayTitle);
    }

    [Fact]
    public void Writing_is_stable_readable_and_keeps_everything()
    {
        var t = AdvancedTemplate.Parse(StagedBuild, "staged-build");
        var text = t.Serialize();
        var again = AdvancedTemplate.Parse(text, "staged-build");

        Assert.Equal(text, again.Serialize());
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\"kind\": \"wait-for-worker\"", text);
        Assert.Contains("Don't start stage 2.", text); // not '
        Assert.DoesNotContain("\"escalate\": false", text);
        Assert.DoesNotContain("null", text);
        Assert.DoesNotContain("\"id\": \"staged-build\"", text); // the id is the file name
        Assert.Equal(12, again.AllSteps.Count());
    }

    [Theory]
    [InlineData("""{ "name": "x", "stages": [ { "id": "a", "steps": [ { "id": "b", "kind": "loop" } ] } ] }""")] // no such kind
    [InlineData("""{ "name": "x", "stages": [ { "id": "a", "steps": [ { "id": "b" } ] } ] }""")] // no kind
    [InlineData("""{ "name": "x", "stages": [ { "id": "a", "steps": [ { "id": "b", "kind": 3 } ] } ] }""")] // a number for a kind
    [InlineData("""{ "name": "x", "stages": [ { "steps": [] } ] }""")] // a stage without an id
    [InlineData("""{ "version": 99, "name": "x", "stages": [] }""")] // from a newer AgentHub
    [InlineData("null")]
    [InlineData("not json")]
    public void A_file_of_the_wrong_shape_is_refused_with_a_reason(string text)
    {
        var ex = Assert.Throws<FormatException>(() => AdvancedTemplate.Parse(text, "x"));
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void Validate_names_every_problem()
    {
        var t = new AdvancedTemplate
        {
            Name = " ",
            Stages =
            [
                new() { Id = "one", Steps = [
                    new() { Id = "a", Kind = StepKind.Instruct },
                    new() { Id = "a", Kind = StepKind.CreateBranch },
                    new() { Id = "Bad Id", Kind = StepKind.MergeBranch, Branch = "feature/x" },
                    new() { Id = "q", Kind = StepKind.AskOwner, Text = " " },
                ] },
                new() { Id = "one", Steps = [] },
                new() { Id = "two", After = ["two", "gone"], Steps = [new() { Id = "c", Kind = StepKind.Commit }] },
            ]
        };

        var problems = t.Validate();

        Assert.Contains("The template has no name.", problems);
        Assert.Contains("Step \"a\" (instruct) needs text.", problems);
        Assert.Contains("Two steps have the id \"a\".", problems);
        Assert.Contains("Step \"a\" (create-branch) needs a branch.", problems);
        Assert.Contains("Step id \"Bad Id\" must be lower-case letters, digits and dashes.", problems);
        Assert.Contains("Step \"Bad Id\" (merge-branch) needs a target.", problems);
        Assert.Contains("Step \"q\" (ask-owner) needs text.", problems);
        Assert.Contains("Two stages have the id \"one\".", problems);
        Assert.Contains("Stage \"one\" has no steps.", problems);
        Assert.Contains("Stage \"two\" comes after itself.", problems);
        Assert.Contains("Stage \"two\" comes after \"gone\", which does not exist.", problems);
        Assert.Contains("The template has no stages.", new AdvancedTemplate { Name = "x" }.Validate());
    }

    [Fact]
    public void Stages_that_wait_for_each_other_are_a_loop_and_a_template_cannot_loop()
    {
        var t = Template(Stage("a", ["c"]), Stage("b"), Stage("c"));

        Assert.Equal(["The stages form a loop: each waits for another. A template cannot loop."], t.Validate());
        Assert.Throws<InvalidOperationException>(() => t.ExecutionOrder());
    }

    [Fact]
    public void Stages_that_do_not_depend_on_each_other_are_parallel_and_still_get_one_order()
    {
        // setup, then api and ui side by side, then release once both are done
        var t = Template(Stage("setup"), Stage("api"), Stage("ui", ["setup"]), Stage("release", ["api", "ui"]));

        Assert.Empty(t.Validate());
        Assert.True(t.HasParallelStages);
        Assert.Equal(["setup", "api", "ui", "release"], t.ExecutionOrder().Select(s => s.Id));
        Assert.Equal(["setup"], t.Predecessors(t.Stages[1])); // left out: the stage before it
        Assert.Empty(t.Predecessors(t.Stages[0]));

        // an empty "after" starts at once, whatever is above it; a later stage can come first in the order
        var free = Template(Stage("late", ["early"]), Stage("early", []));
        Assert.Equal(["early", "late"], free.ExecutionOrder().Select(s => s.Id));
        Assert.False(free.HasParallelStages);
        Assert.True(Template(Stage("a"), Stage("b", [])).HasParallelStages);
    }

    [Fact]
    public async Task Templates_live_in_the_projects_orchestration_folder_and_a_new_one_is_named_after_itself()
    {
        var store = new AdvancedTemplateStore(_repo);
        Assert.Empty(store.List());

        var first = AdvancedTemplate.Parse(StagedBuild, "");
        var second = AdvancedTemplate.Parse(StagedBuild, "");
        await store.SaveAsync(first);
        await store.SaveAsync(second);

        Assert.Equal("staged-build", first.Id);
        Assert.Equal("staged-build-2", second.Id);
        Assert.Equal(Path.Combine(_repo, "orchestration", "staged-build.json"), store.PathFor("staged-build"));
        Assert.True(File.Exists(store.PathFor("staged-build")));
        Assert.Equal(first.Serialize(), store.Read("staged-build").Serialize());

        await File.WriteAllTextAsync(Path.Combine(store.Folder, "broken.json"), "{ \"name\": ");
        var list = store.List();
        Assert.Equal(["broken", "staged-build", "staged-build-2"], list.Select(f => f.Id));
        Assert.Null(list[0].Template);
        Assert.StartsWith("Not a valid advanced template", list[0].Error);
        Assert.Null(list[1].Error);

        var unsound = new AdvancedTemplate { Name = "Empty" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(unsound));
        Assert.Contains("no stages", ex.Message);
        Assert.False(File.Exists(store.PathFor("empty")));
    }

    [Fact]
    public async Task A_change_waits_as_a_draft_and_replaces_the_template_only_when_saved()
    {
        var store = new AdvancedTemplateStore(_repo);
        var original = AdvancedTemplate.Parse(StagedBuild, "");
        await store.SaveAsync(original);
        var before = await File.ReadAllTextAsync(store.PathFor("staged-build"));

        var draft = store.Read("staged-build");
        draft.Stages[1].Steps.Add(new TemplateStep { Id = "s2-merge", Kind = StepKind.MergeBranch, Branch = "feature/screens" }); // no target yet
        await store.SaveDraftAsync("staged-build", draft);

        Assert.Equal(before, await File.ReadAllTextAsync(store.PathFor("staged-build")));
        Assert.True(store.HasDraft("staged-build"));
        Assert.Equal(13, store.ReadDraft("staged-build")!.AllSteps.Count());
        var listed = Assert.Single(store.List()); // the draft is not a template of its own
        Assert.True(listed.HasDraft);
        Assert.Contains("*.draft.json", await File.ReadAllLinesAsync(Path.Combine(store.Folder, ".gitignore")));

        // an unfinished draft cannot be saved over the template, and is kept to finish
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptDraftAsync("staged-build"));
        Assert.Contains("needs a target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(store.PathFor("staged-build")));
        Assert.True(store.HasDraft("staged-build"));

        draft.Stages[1].Steps[^1].Target = "main";
        await store.SaveDraftAsync("staged-build", draft);
        var saved = await store.AcceptDraftAsync("staged-build");

        Assert.Equal(13, saved.AllSteps.Count());
        Assert.Equal(draft.Serialize(), await File.ReadAllTextAsync(store.PathFor("staged-build")));
        Assert.False(store.HasDraft("staged-build"));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(store.Folder, ".gitignore")), l => l == "*.draft.json"); // written once

        await store.SaveDraftAsync("staged-build", original);
        store.DiscardDraft("staged-build");
        Assert.Null(store.ReadDraft("staged-build"));
        Assert.Equal(13, store.Read("staged-build").AllSteps.Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptDraftAsync("staged-build"));
    }
}

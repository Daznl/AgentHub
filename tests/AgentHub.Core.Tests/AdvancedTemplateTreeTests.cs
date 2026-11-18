using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class AdvancedTemplateTreeTests
{
    [Fact]
    public void The_staged_build_example_is_sound_and_follows_the_owners_pattern()
    {
        var t = AdvancedTemplate.StagedBuildExample(stages: 3, plan: "docs/BUILD.md");

        Assert.Empty(t.Validate());
        Assert.Equal(["stage-1", "stage-2", "stage-3"], t.ExecutionOrder().Select(s => s.Id));
        Assert.False(t.HasParallelStages);
        Assert.Equal(
            [StepKind.CreateBranch, StepKind.Instruct, StepKind.Instruct, StepKind.WaitForWorker, StepKind.Handoff, StepKind.Commit, StepKind.CloseSession],
            t.Stages[0].Steps.Select(s => s.Kind));
        Assert.Equal(StepKind.NewSession, t.Stages[1].Steps[0].Kind); // every later stage starts in a fresh session
        Assert.Equal(StepKind.OpenPullRequest, t.Stages[2].Steps[^1].Kind);
        Assert.All(t.AllSteps.Where(s => s.Kind == StepKind.WaitForWorker), s => Assert.True(s.Escalate));
        Assert.Contains("docs/BUILD.md", t.Stages[0].Steps[2].Text);
        Assert.Equal(t.Serialize(), AdvancedTemplate.Parse(t.Serialize(), "").Serialize());
    }

    [Fact]
    public void The_tree_runs_from_start_to_done_with_each_stage_above_its_steps()
    {
        var t = AdvancedTemplate.StagedBuildExample();
        var rows = AdvancedTemplateTree.Rows(t);

        Assert.Equal(TreeRowKind.Start, rows[0].Kind);
        Assert.Equal(TreeRowKind.End, rows[^1].Kind);
        Assert.Equal(2 + t.Stages.Count + t.AllSteps.Count(), rows.Count);
        Assert.Equal("2 stages · 15 steps", AdvancedTemplateTree.Summary(t));

        Assert.Equal(new TreeRow(TreeRowKind.Stage, "stage-1", "Stage 1", "7 steps"), rows[1]);
        Assert.Equal(new TreeRow(TreeRowKind.Step, "s1-branch", "Create branch feature/stage-1", null, "🌿", "Create branch"), rows[2]);

        // a titled step keeps its full text as the quieter line; the escalation point is flagged
        var build = rows.Single(r => r.Id == "s1-build");
        Assert.Equal("Build stage 1", build.Title);
        Assert.StartsWith("Build stage 1 of docs/PLAN.md", build.Detail);
        Assert.True(rows.Single(r => r.Id == "s1-watch").Escalate);
        Assert.False(build.Escalate);

        // every row of a stage sits between its heading and the next
        var ids = rows.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf("stage-1") < ids.IndexOf("s1-close") && ids.IndexOf("s1-close") < ids.IndexOf("stage-2"));
    }

    [Fact]
    public void An_untitled_instruction_is_its_own_title_and_is_not_said_twice()
    {
        var t = new AdvancedTemplate
        {
            Name = "One",
            Stages = [new() { Id = "only", Steps = [new() { Id = "go", Kind = StepKind.Instruct, Text = " Run the tests. " }] }],
        };

        var step = AdvancedTemplateTree.Rows(t).Single(r => r.Kind == TreeRowKind.Step);

        Assert.Equal("Run the tests.", step.Title);
        Assert.Null(step.Detail);
        Assert.Equal("1 stage · 1 step", AdvancedTemplateTree.Summary(t));
    }

    [Fact]
    public void Stages_show_what_they_wait_for_and_come_in_the_order_a_single_worker_follows()
    {
        TemplateStage Stage(string id, string title, string[]? after) =>
            new() { Id = id, Title = title, After = after?.ToList(), Steps = [new() { Id = id + "-go", Kind = StepKind.Commit }] };
        var t = new AdvancedTemplate
        {
            Name = "Parallel",
            Stages = [Stage("release", "Release", ["api", "ui"]), Stage("api", "The API", []), Stage("ui", "The screens", [])],
        };

        var stages = AdvancedTemplateTree.Rows(t).Where(r => r.Kind == TreeRowKind.Stage).ToList();

        Assert.Equal(["api", "ui", "release"], stages.Select(r => r.Id));
        Assert.Equal("1 step · starts at once", stages[0].Detail);
        Assert.Equal("1 step · after The API and The screens", stages[2].Detail);
    }

    [Fact]
    public void A_template_with_problems_is_still_drawn_in_file_order()
    {
        var t = AdvancedTemplate.StagedBuildExample();
        t.Stages[0].After = ["stage-2"]; // a loop: no order exists

        Assert.NotEmpty(t.Validate());
        var stages = AdvancedTemplateTree.Rows(t).Where(r => r.Kind == TreeRowKind.Stage).Select(r => r.Id);
        Assert.Equal(["stage-1", "stage-2"], stages);
    }

    [Fact]
    public void Every_step_kind_has_its_own_icon_and_label()
    {
        var kinds = Enum.GetValues<StepKind>();
        Assert.Equal(kinds.Length, kinds.Select(AdvancedTemplateTree.Icon).Distinct().Count());
        Assert.Equal(kinds.Length, kinds.Select(AdvancedTemplateTree.Label).Distinct().Count());
        Assert.DoesNotContain("•", kinds.Select(AdvancedTemplateTree.Icon));
    }
}

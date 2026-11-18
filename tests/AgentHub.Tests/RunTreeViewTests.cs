using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

/// <summary>The run tree beside a pane: the template's stages and steps, coloured by how far the run has got.</summary>
public class RunTreeViewTests
{
    private static string Row(RunTreeRow r) => $"{(r.Kind == TreeRowKind.Stage ? "#" : "-")}{r.Id}:{r.State}";

    [Fact]
    public void A_run_not_started_shows_every_stage_and_step_to_do_without_start_and_end()
    {
        var run = new TemplateRun(TemplateRunTests.StagedBuild());
        var rows = RunTreeView.Rows(run);

        Assert.Equal(run.Template.Stages.Count + run.Steps.Count, rows.Count);
        Assert.All(rows, r => Assert.Equal(RunTreeState.Pending, r.State));
        Assert.Equal(TreeRowKind.Stage, rows[0].Kind);
        Assert.Equal(run.Steps.Select(s => s.Step.Id), rows.Where(r => r.Kind == TreeRowKind.Step).Select(r => r.Id));
        Assert.Equal("0 of " + run.Steps.Count + " steps done", RunTreeView.Summary(run));
    }

    [Fact]
    public void Done_steps_are_done_the_step_the_run_is_at_is_current_and_so_is_its_stage()
    {
        var run = new TemplateRun(TemplateRunTests.StagedBuild());
        run.Start();
        run.CompleteCurrent();
        run.CompleteCurrent();

        var rows = RunTreeView.Rows(run).Select(Row).ToList();
        Assert.Contains("#stage-1:Current", rows);
        Assert.Contains("-s1-branch:Done", rows);
        Assert.Contains("-s1-build:Done", rows);
        Assert.Contains("-s1-watch:Current", rows);
        Assert.Contains("-s1-handoff:Pending", rows);
        Assert.Contains("#stage-2:Pending", rows);
        Assert.Equal("2 of " + run.Steps.Count + " steps done", RunTreeView.Summary(run));
    }

    [Fact]
    public void A_paused_step_stays_current_and_a_finished_stage_is_done()
    {
        var run = new TemplateRun(TemplateRunTests.StagedBuild());
        var stage1 = run.Template.Stages[0].Steps.Count;
        run.Start();
        for (var i = 0; i < stage1; i++) run.CompleteCurrent();
        run.Pause(PauseCause.Owner, "Paused by you.");

        var rows = RunTreeView.Rows(run);
        Assert.Equal(RunTreeState.Done, rows.Single(r => r.Id == "stage-1").State);
        Assert.Equal(RunTreeState.Current, rows.Single(r => r.Id == "stage-2").State);
        Assert.Equal(RunTreeState.Current, rows.Single(r => r.Id == run.Current!.Step.Id).State);
    }

    [Fact]
    public void Going_on_from_a_later_step_marks_the_steps_between_skipped()
    {
        var run = new TemplateRun(TemplateRunTests.StagedBuild());
        var stage2First = run.Template.Stages[1].Steps[0].Id;
        run.Start(stage2First);

        var rows = RunTreeView.Rows(run);
        Assert.Equal(RunTreeState.Skipped, rows.Single(r => r.Id == "stage-1").State);
        Assert.All(rows.Where(r => r.Kind == TreeRowKind.Step).TakeWhile(r => r.Id != stage2First), r => Assert.Equal(RunTreeState.Skipped, r.State));
        Assert.Equal(RunTreeState.Current, rows.Single(r => r.Id == stage2First).State);
    }

    [Fact]
    public void The_stamp_changes_whenever_a_colour_would()
    {
        var run = new TemplateRun(TemplateRunTests.StagedBuild());
        var stamps = new List<string> { RunTreeView.Stamp(run) };
        run.Start(); stamps.Add(RunTreeView.Stamp(run));
        run.Pause(PauseCause.Owner, null); stamps.Add(RunTreeView.Stamp(run));
        run.Resume(); stamps.Add(RunTreeView.Stamp(run));
        run.CompleteCurrent(); stamps.Add(RunTreeView.Stamp(run));

        Assert.Equal(stamps.Count, stamps.Distinct().Count() + 1); // pause then resume returns to the same place: the one repeat
        Assert.Equal(stamps[1], stamps[3]);
    }

    [Fact]
    public void The_box_carries_the_run_and_shows_again_when_a_step_moves()
    {
        var run = new TemplateRun(TemplateRunTests.StagedBuild());
        OrchestratorBoxView View() => new("t", "s", "n", []) { HasRun = true, Run = run, RunStamp = RunTreeView.Stamp(run) };
        var before = View();
        Assert.Equal(before, View());
        run.Start();
        Assert.NotEqual(before, View());
    }
}

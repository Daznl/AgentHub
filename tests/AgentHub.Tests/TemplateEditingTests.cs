using System.IO;
using AgentHub.Services.Orchestrator;
using Xunit;
using static AgentHub.Tests.TemplateRunTests;

namespace AgentHub.Tests;

/// <summary>
/// Editing an advanced template (stage 5): the changes the window makes to a draft, and what a run that has started
/// accepts of a changed template: steps not yet started only, none of them put before the step it is at.
/// </summary>
public class TemplateEditingTests : IDisposable
{
    // A folder with spaces, as the owner's are.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentHub Editing " + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private static string[] Ids(AdvancedTemplate t) => t.AllSteps.Select(s => s.Id).ToArray();
    private static TemplateStep Step(AdvancedTemplate t, string id) => t.AllSteps.Single(s => s.Id == id);

    private static PaneOrchestrator Attach(ScriptedWorker worker, ScriptedBrain brain, AdvancedTemplate? template = null) =>
        new(worker, brain, "Staged build", advanced: template ?? StagedBuild()) { TurnSettle = TimeSpan.Zero, StopDelay = TimeSpan.Zero, StartupDelay = TimeSpan.Zero, LoopInterval = TimeSpan.FromMilliseconds(20) };

    /// <summary>The staged build with a lint step put in after the build.</summary>
    private static AdvancedTemplate WithLint()
    {
        var t = StagedBuild();
        t.Stages[0].Steps.Insert(2, new TemplateStep { Id = "s1-lint", Kind = StepKind.Instruct, Title = "Lint", Text = "Run the linter and fix what it reports." });
        return t;
    }

    // ---- The draft's changes ----

    [Fact]
    public void A_copy_shares_nothing_with_the_template_it_came_from()
    {
        var template = StagedBuild();
        template.Id = "staged-build";
        var copy = template.Clone();
        copy.Stages[0].Steps[0].Branch = "feature/other";
        copy.Stages.RemoveAt(1);

        Assert.Equal("staged-build", copy.Id);
        Assert.Equal("feature/stage-1", template.Stages[0].Steps[0].Branch);
        Assert.Equal(2, template.Stages.Count);
    }

    [Fact]
    public void A_new_step_gets_an_id_no_step_has_had_and_goes_where_it_is_put()
    {
        var draft = StagedBuild();
        var stage = draft.Stages[0];
        // "stage-1-step-1" was a step of the saved template (removed in the draft since): never used again.
        var step = TemplateEditing.AddStep(draft, stage, 1, StepKind.Commit, taken: ["stage-1-step-1"]);

        Assert.Equal("stage-1-step-2", step.Id);
        Assert.Same(step, stage.Steps[1]);
        Assert.Equal(StepKind.Commit, step.Kind);
        var last = TemplateEditing.AddStep(draft, stage, 99, StepKind.Instruct);
        Assert.Same(last, stage.Steps[^1]);
        Assert.Equal("stage-1-step-1", last.Id); // only "taken" ids and the draft's own are avoided
        Assert.Equal(Ids(draft).Length, Ids(draft).Distinct().Count());
    }

    [Fact]
    public void A_step_moves_within_its_stage_and_over_the_edge_into_the_next()
    {
        var draft = StagedBuild();
        var close = Step(draft, "s1-close");
        Assert.True(TemplateEditing.MoveStep(draft, Step(draft, "s1-build"), -1));
        Assert.Equal(["s1-build", "s1-branch"], draft.Stages[0].Steps.Take(2).Select(s => s.Id));

        Assert.True(TemplateEditing.MoveStep(draft, close, +1)); // the last of stage 1 becomes the first of stage 2
        Assert.Equal("s1-close", draft.Stages[1].Steps[0].Id);
        Assert.True(TemplateEditing.MoveStep(draft, close, -1)); // and back
        Assert.Equal("s1-close", draft.Stages[0].Steps[^1].Id);

        Assert.False(TemplateEditing.CanMoveStep(draft, draft.Stages[0].Steps[0], -1));
        Assert.False(TemplateEditing.MoveStep(draft, draft.Stages[1].Steps[^1], +1));
        Assert.Equal(10, draft.AllSteps.Count());
    }

    [Fact]
    public void Stages_move_with_their_steps_and_a_removed_one_is_no_longer_waited_for()
    {
        var draft = StagedBuild();
        var added = TemplateEditing.AddStage(draft, -1);
        Assert.Equal("stage-3", added.Id);
        added.After = ["stage-1", "stage-2"];
        TemplateEditing.AddStep(draft, added, 0, StepKind.Handoff);

        Assert.True(TemplateEditing.MoveStage(draft, draft.Stages[1], -1));
        Assert.Equal(["stage-2", "stage-1", "stage-3"], draft.Stages.Select(s => s.Id));
        Assert.Equal("s2-session", draft.Stages[0].Steps[0].Id);
        Assert.False(TemplateEditing.CanMoveStage(draft, draft.Stages[0], -1));

        Assert.True(TemplateEditing.RemoveStage(draft, draft.Stages[0]));
        Assert.Equal(["stage-1"], added.After);
        Assert.Empty(draft.Validate());
    }

    [Fact]
    public async Task A_draft_may_be_unfinished_but_the_store_will_not_save_it()
    {
        var store = new AdvancedTemplateStore(_root);
        var template = StagedBuild();
        template.Id = "staged-build";
        await store.SaveAsync(template);

        var draft = store.Read("staged-build");
        TemplateEditing.AddStep(draft, draft.Stages[0], -1, StepKind.Instruct); // no text yet
        await store.SaveDraftAsync("staged-build", draft);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptDraftAsync("staged-build"));
        Assert.Contains("needs text", refused.Message);
        Assert.Equal(10, store.Read("staged-build").AllSteps.Count()); // untouched
        Assert.True(store.HasDraft("staged-build"));

        draft.Stages[0].Steps[^1].Text = "Tidy up.";
        await store.SaveDraftAsync("staged-build", draft);
        await store.AcceptDraftAsync("staged-build");
        Assert.Equal(11, store.Read("staged-build").AllSteps.Count());
        Assert.False(store.HasDraft("staged-build"));
    }

    // ---- What a run accepts of a changed template ----

    [Fact]
    public void A_run_not_yet_started_takes_any_sound_template()
    {
        var run = new TemplateRun(StagedBuild());
        var changed = StagedBuild();
        changed.Stages.Reverse();
        changed.Stages[0].After = []; // stage 2 first now, stage 1 after it
        changed.Stages[1].After = ["stage-2"];
        changed.Stages[1].Steps.RemoveAt(0);

        Assert.Empty(run.StartedStepIds);
        Assert.Empty(run.ProblemsWith(changed));
        var rebased = run.Rebase(changed);
        Assert.Equal(RunState.Ready, rebased.State);
        Assert.Equal("s2-session", rebased.Steps[0].Step.Id);
    }

    [Fact]
    public void A_paused_run_keeps_the_steps_it_has_reached_as_they_are()
    {
        var run = new TemplateRun(StagedBuild());
        run.Start();
        run.CompleteCurrent(); // s1-branch done, at s1-build
        run.Pause(PauseCause.Owner, "Paused by you.");
        Assert.Equal(["s1-branch", "s1-build"], run.StartedStepIds);

        var changedDone = StagedBuild();
        changedDone.Stages[0].Steps[0].Branch = "feature/other";
        Assert.Contains("was changed", Assert.Single(run.ProblemsWith(changedDone)));

        var removedCurrent = StagedBuild();
        removedCurrent.Stages[0].Steps.RemoveAt(1);
        Assert.Contains("was removed", Assert.Single(run.ProblemsWith(removedCurrent)));

        var before = StagedBuild();
        before.Stages[0].Steps.Insert(1, new TemplateStep { Id = "s1-read", Kind = StepKind.Instruct, Text = "Read the plan." });
        Assert.Contains("now comes before", Assert.Single(run.ProblemsWith(before)));

        var unsound = StagedBuild();
        unsound.Stages[1].Steps[1].Text = null;
        Assert.Contains("needs text", Assert.Single(run.ProblemsWith(unsound)));
        Assert.Throws<InvalidOperationException>(() => run.Rebase(unsound));
    }

    [Fact]
    public void Steps_not_yet_started_can_be_added_removed_changed_and_moved()
    {
        var run = new TemplateRun(StagedBuild());
        run.Start();
        run.CompleteCurrent();
        run.CompleteCurrent(); // at s1-watch
        run.Pause(PauseCause.Escalation, "Which database?");

        var changed = WithLint();
        changed.Stages[0].Steps.Insert(4, changed.Stages[0].Steps[2]); // "s1-lint" goes after the step the run is at
        changed.Stages[0].Steps.RemoveAt(2);
        Step(changed, "s2-build").Text = "Build stage 2, with tests.";
        changed.Stages[1].Steps.RemoveAt(2); // the question goes
        TemplateEditing.MoveStep(changed, Step(changed, "s1-close"), +1);

        Assert.Empty(run.ProblemsWith(changed));
        var rebased = run.Rebase(changed);

        Assert.Equal(RunState.Paused, rebased.State);
        Assert.Equal(PauseCause.Escalation, rebased.Cause); // the pause is kept as it was
        Assert.Equal("Which database?", rebased.PauseReason);
        Assert.Equal("s1-watch", rebased.Current!.Step.Id);
        Assert.Equal(StepState.Done, rebased.StateOf("s1-build"));
        Assert.Equal(StepState.Pending, rebased.StateOf("s1-lint"));
        Assert.Equal(["s1-branch", "s1-build", "s1-watch", "s1-lint", "s1-handoff", "s1-commit", "s1-close", "s2-session", "s2-build", "s2-pr"],
            rebased.Steps.Select(s => s.Step.Id));
        Assert.Equal("stage-2", rebased.Steps.Single(s => s.Step.Id == "s1-close").Stage.Id);
    }

    [Fact]
    public void A_finished_run_given_new_steps_stops_at_the_first_of_them()
    {
        var run = new TemplateRun(StagedBuild());
        run.Start();
        while (run.State == RunState.Running) run.CompleteCurrent();
        Assert.Equal(RunState.Finished, run.State);

        var changed = StagedBuild();
        changed.Stages[1].Steps.Add(new TemplateStep { Id = "s2-merge", Kind = StepKind.MergeBranch, Branch = "feature/stage-2", Target = "main" });
        var rebased = run.Rebase(changed);

        Assert.Equal(RunState.Paused, rebased.State);
        Assert.Equal("s2-merge", rebased.Current!.Step.Id);
        Assert.Equal(10, rebased.DoneCount);

        var renamed = StagedBuild();
        renamed.Name = "Staged build, renamed";
        Assert.Equal(RunState.Finished, run.Rebase(renamed).State);
    }

    [Fact]
    public void A_running_run_is_not_given_a_changed_template()
    {
        var run = new TemplateRun(StagedBuild());
        run.Start();
        Assert.Throws<InvalidOperationException>(() => run.Rebase(WithLint()));
    }

    // ---- Applying it to the run on a pane ----

    [Fact]
    public async Task A_paused_run_given_a_change_goes_on_with_it_and_the_orchestrator_is_told_once()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain();
        using var o = Attach(worker, brain);
        await o.GoAsync("s1-build");
        Assert.Equal(["do s1-build"], worker.Submitted);

        Assert.Throws<InvalidOperationException>(() => o.ApplyTemplate(WithLint())); // running: pause first
        o.PauseRun();
        worker.EndTurn("Stage 1 built, 14 tests pass."); // finished while paused: nobody is told
        await Task.Delay(150);

        var before = o.Run;
        o.ApplyTemplate(WithLint());
        Assert.NotSame(before, o.Run);
        Assert.Equal(11, o.Run!.Steps.Count);
        Assert.Equal("s1-build", o.Run.Current!.Step.Id);
        Assert.Equal(PauseCause.Owner, o.Run.Cause);
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.StartsWith("The template was changed"));

        await o.GoAsync();
        await Until(() => worker.Submitted.Contains("do s1-lint"));

        var resumed = brain.Messages[1];
        Assert.Contains("run_resumed", resumed);
        Assert.Contains("THE OWNER HAS CHANGED THE TEMPLATE", resumed);
        Assert.Contains("[instruct] Lint", resumed);
        Assert.Contains("[instruct] Build stage 1 <- CURRENT STEP", resumed);
        Assert.Contains("[create-branch] Create branch feature/stage-1 (skipped)", resumed);
        Assert.Contains("step_started", brain.Messages[2]);
        Assert.DoesNotContain("THE OWNER HAS CHANGED THE TEMPLATE", brain.Messages[2]); // told once
        Assert.Equal(StepState.Done, o.Run.StateOf("s1-build"));
    }

    [Fact]
    public async Task A_change_that_touches_a_step_the_run_has_reached_is_refused_and_nothing_changes()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain();
        using var o = Attach(worker, brain);
        await o.GoAsync("s1-build");
        o.PauseRun();
        var before = o.Run;

        var changed = StagedBuild();
        Step(changed, "s1-build").Text = "Build something else.";
        var refused = Assert.Throws<InvalidOperationException>(() => o.ApplyTemplate(changed));
        Assert.Contains("was changed", refused.Message);
        Assert.Same(before, o.Run);
    }

    [Fact]
    public async Task The_saved_run_follows_the_template_it_was_given()
    {
        var store = new RunStore(_root);
        var worker = new ScriptedWorker();
        using var o = Attach(worker, new ScriptedBrain());
        var now = DateTimeOffset.Now;
        var template = StagedBuild();
        var record = new RunRecord
        {
            Id = store.NewId(template.Name, now), Project = @"C:\My Project", TemplateId = "staged-build", TemplateName = template.Name,
            Template = template.Serialize(), StartedAt = now, Pane = "Terminal 1",
        };
        using var recorder = new RunRecorder(store, o, record);
        await o.GoAsync("s1-build");
        o.PauseRun();

        o.ApplyTemplate(WithLint());

        var saved = store.Find(record.Id)!;
        Assert.Equal(11, saved.ReadTemplate().AllSteps.Count());
        Assert.Equal(11, saved.StepCount);
        Assert.Equal(StepState.Pending, saved.Steps["s1-lint"]);
        // Picked up again later, it goes on with the changed template.
        var back = TemplateRun.Restore(saved.ReadTemplate(), saved.Snapshot(), "Picked up.");
        Assert.Equal("s1-build", back.Current!.Step.Id);
        Assert.Contains(back.Steps, s => s.Step.Id == "s1-lint");
    }
}

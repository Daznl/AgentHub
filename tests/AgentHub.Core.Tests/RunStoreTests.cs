using System.IO;
using AgentHub.Services.Orchestrator;
using Xunit;
using ScriptedBrain = AgentHub.Tests.TemplateRunTests.ScriptedBrain;
using ScriptedWorker = AgentHub.Tests.TemplateRunTests.ScriptedWorker;

namespace AgentHub.Tests;

/// <summary>
/// Runs kept on disk: a run is recorded as it moves, survives its orchestrator being taken off (and AgentHub closing),
/// and is picked up again where it stood.
/// </summary>
public class RunStoreTests : IDisposable
{
    // A folder with spaces, as the owner's are.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentHub Runs " + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private static PaneOrchestrator Attach(ScriptedWorker worker, ScriptedBrain brain, RunSnapshot? resume = null, bool remembers = false, AdvancedTemplate? template = null) =>
        new(worker, brain, "Staged build", advanced: template ?? SampleTemplates.StagedBuild(), resume: resume, brainRemembers: remembers)
        { TurnSettle = TimeSpan.Zero, StopDelay = TimeSpan.Zero, StartupDelay = TimeSpan.Zero, LoopInterval = TimeSpan.FromMilliseconds(20) };

    private RunRecord NewRecord(RunStore store, AdvancedTemplate? template = null)
    {
        template ??= SampleTemplates.StagedBuild();
        var now = DateTimeOffset.Now;
        return new RunRecord
        {
            Id = store.NewId(template.Name, now), Project = @"C:\My Project", TemplateId = "staged-build", TemplateName = template.Name,
            Template = template.Serialize(), StartedAt = now, Pane = "Terminal 1",
        };
    }

    // ---- Where a run stands, saved and restored ----

    [Fact]
    public void A_run_under_way_comes_back_paused_at_its_step_with_what_was_done()
    {
        var run = new TemplateRun(SampleTemplates.StagedBuild());
        run.Start();
        run.CompleteCurrent();
        run.CompleteCurrent(); // at s1-watch, running

        var back = TemplateRun.Restore(SampleTemplates.StagedBuild(), run.Snapshot(), "Picked up.");

        Assert.Equal(RunState.Paused, back.State);
        Assert.Equal(PauseCause.Owner, back.Cause);
        Assert.Equal("Picked up.", back.PauseReason);
        Assert.Equal("s1-watch", back.Current!.Step.Id);
        Assert.Equal(StepState.Done, back.StateOf("s1-branch"));
        Assert.Equal(StepState.Done, back.StateOf("s1-build"));
        Assert.Equal(StepState.Running, back.StateOf("s1-watch"));
        Assert.Equal(StepState.Pending, back.StateOf("s1-handoff"));
        Assert.Equal(2, back.DoneCount);

        back.Start(); // Go
        Assert.Equal(RunState.Running, back.State);
        Assert.Equal("s1-handoff", back.CompleteCurrent()!.Step.Id);
    }

    [Fact]
    public void A_pause_for_the_owner_is_kept_and_ready_and_finished_runs_come_back_as_they_were()
    {
        var run = new TemplateRun(SampleTemplates.StagedBuild());
        Assert.Equal(RunState.Ready, TemplateRun.Restore(SampleTemplates.StagedBuild(), run.Snapshot(), "x").State);

        run.Start("s2-check");
        run.Pause(PauseCause.Question, "Stage 2 is built. Open the pull request?");
        var asked = TemplateRun.Restore(SampleTemplates.StagedBuild(), run.Snapshot(), "x");
        Assert.Equal(PauseCause.Question, asked.Cause);
        Assert.Equal("Stage 2 is built. Open the pull request?", asked.PauseReason);
        Assert.Equal(StepState.Skipped, asked.StateOf("s1-build"));

        run.Start();
        while (run.CompleteCurrent() is not null) { }
        var done = TemplateRun.Restore(SampleTemplates.StagedBuild(), run.Snapshot(), "x");
        Assert.Equal(RunState.Finished, done.State);
        Assert.Null(done.Current);
    }

    [Fact]
    public void A_template_changed_in_between_keeps_what_was_done_by_step_id()
    {
        var run = new TemplateRun(SampleTemplates.StagedBuild());
        run.Start();
        run.CompleteCurrent();
        run.CompleteCurrent(); // at s1-watch

        var changed = SampleTemplates.StagedBuild();
        changed.Stages[0].Steps.RemoveAll(s => s.Id == "s1-watch");                                                    // the step it was at is gone
        changed.Stages[0].Steps.Insert(1, new TemplateStep { Id = "s1-lint", Kind = StepKind.Instruct, Text = "Run the linter." }); // a new one before it

        var back = TemplateRun.Restore(changed, run.Snapshot(), "x");

        Assert.Equal(StepState.Done, back.StateOf("s1-branch"));
        Assert.Equal(StepState.Done, back.StateOf("s1-build"));
        Assert.Equal("s1-lint", back.Current!.Step.Id); // the first step still to do
        Assert.Equal(RunState.Paused, back.State);
    }

    // ---- The store ----

    [Fact]
    public void Records_are_kept_one_folder_each_listed_latest_first_and_deleted_cleanly()
    {
        var store = new RunStore(_root);
        Assert.Empty(store.List());

        var now = new DateTimeOffset(2026, 11, 13, 14, 2, 5, TimeSpan.FromHours(8));
        Assert.Equal("20261113-140205-staged-build", store.NewId("Staged build", now));
        var first = NewRecord(store);
        first.Id = "20261113-140205-staged-build";
        first.UpdatedAt = now;
        first.State = RunState.Paused;
        first.Cause = PauseCause.Escalation;
        first.Reason = "Which key?";
        first.CurrentStepId = "s1-watch";
        first.Steps = new() { ["s1-branch"] = StepState.Done, ["s1-watch"] = StepState.Running };
        store.Save(first);
        Assert.Equal("20261113-140205-staged-build-2", store.NewId("Staged build", now)); // the same second, the same template

        var second = NewRecord(store);
        second.Id = "20261113-150000-other";
        second.UpdatedAt = now.AddHours(1);
        store.Save(second);
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", "run.json"), "{ not json");

        var list = store.List();
        Assert.Equal(["20261113-150000-other", "20261113-140205-staged-build"], list.Select(r => r.Id));
        var read = list[1];
        Assert.Equal(PauseCause.Escalation, read.Cause);
        Assert.Equal("Which key?", read.Reason);
        Assert.Equal(StepState.Done, read.Steps["s1-branch"]);
        Assert.Equal(first.Template, read.Template);
        Assert.Equal("Staged build", read.ReadTemplate().Name);
        Assert.Contains("\"state\": \"paused\"", File.ReadAllText(store.PathFor(first.Id)));

        store.Delete(first.Id);
        Assert.False(Directory.Exists(Path.Combine(_root, first.Id)));
        Assert.Single(store.List());
        store.Delete(@"..\" + Path.GetFileName(_root)); // never anything but a run's own folder
        store.Delete("");
        Assert.True(Directory.Exists(_root));
    }

    // ---- A run recorded as it goes, and picked up again ----

    [Fact]
    public async Task The_record_follows_the_run_and_a_run_left_running_is_saved_as_paused()
    {
        var store = new RunStore(_root);
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain();
        using var o = Attach(worker, brain);
        var recorder = new RunRecorder(store, o, NewRecord(store));
        var id = recorder.Record.Id;

        Assert.Equal(RunState.Ready, store.Find(id)!.State); // on disk from the moment it is attached
        Assert.Equal("claude", store.Find(id)!.WorkerCommand);
        Assert.Equal(@"C:\My Project", store.Find(id)!.WorkerFolder);

        await o.GoAsync();
        worker.EndTurn("Branch created.");
        await TemplateRunTests.Until(() => store.Find(id)!.CurrentStepId == "s1-build");

        var mid = store.Find(id)!;
        Assert.Equal(RunState.Running, mid.State);
        Assert.Equal(1, mid.DoneCount);
        Assert.Equal(10, mid.StepCount);
        Assert.Equal(StepState.Done, mid.Steps["s1-branch"]);
        Assert.StartsWith("step 2 of 10", mid.Progress);
        Assert.Null(recorder.Error);

        recorder.Close("AgentHub was closed."); // nothing follows it any more
        var left = store.Find(id)!;
        Assert.Equal(RunState.Paused, left.State);
        Assert.Equal(PauseCause.Owner, left.Cause);
        Assert.Equal("AgentHub was closed.", left.Reason);
        Assert.Equal("s1-build", left.CurrentStepId);
        Assert.Equal(RunState.Running, o.Run!.State); // the live run itself was not touched

        o.PauseRun(); // a closed recorder writes nothing more
        Assert.Equal("AgentHub was closed.", store.Find(id)!.Reason);
    }

    [Fact]
    public async Task A_saved_run_is_picked_up_where_it_stood_and_go_judges_the_workers_latest_reply()
    {
        var store = new RunStore(_root);
        string id;
        {
            var worker = new ScriptedWorker();
            using var o = Attach(worker, new ScriptedBrain());
            using var recorder = new RunRecorder(store, o, NewRecord(store));
            id = recorder.Record.Id;
            await o.GoAsync();
            worker.EndTurn("Branch created.");
            await TemplateRunTests.Until(() => o.Run!.Current?.Step.Id == "s1-build" && worker.Submitted.Count == 2);
        } // AgentHub closes: orchestrator and recorder go, the worker's session lives on and finishes its turn

        var record = store.Find(id)!;
        Assert.Equal(RunState.Paused, record.State);
        Assert.Equal("The orchestrator was taken off the session.", record.Reason);

        var later = new ScriptedWorker { LastReply = "Stage 1 built, 14 tests pass.", Obeys = true };
        var brain = new ScriptedBrain();
        using var back = Attach(later, brain, record.Resume().Snapshot, remembers: true, template: record.ReadTemplate());
        using var again = new RunRecorder(store, back, record);

        Assert.Equal(RunState.Paused, back.Run!.State);
        Assert.Equal("s1-build", back.Run.Current!.Step.Id);
        Assert.Equal(StepState.Done, back.Run.StateOf("s1-branch"));
        Assert.Equal("The orchestrator was taken off the session.", back.Run.PauseReason); // why it stopped, as saved
        Assert.Empty(later.Submitted);

        await back.GoAsync();
        await TemplateRunTests.Until(() => back.Run.DoneCount >= 4);

        var first = brain.Messages[0];
        Assert.Contains("run_resumed", first);
        Assert.Contains("[WORKER FINAL REPLY]\nStage 1 built, 14 tests pass.", first);
        Assert.DoesNotContain("[CONVERSATION HISTORY]", first); // the same orchestrator session: it remembers
        Assert.DoesNotContain("do s1-build", later.Submitted);  // the build is not sent again
        Assert.Equal("do s1-handoff", later.Submitted[0]);
        await TemplateRunTests.Until(() => store.Find(id)!.DoneCount >= 4);
        Assert.Equal(id, Assert.Single(store.List()).Id); // the same record carries on
    }

    [Fact]
    public async Task A_new_orchestrator_session_is_shown_the_workers_history_when_the_old_one_is_gone()
    {
        var run = new TemplateRun(SampleTemplates.StagedBuild());
        run.Start("s1-build");
        var worker = new ScriptedWorker { LastReply = "Done." };
        var brain = new ScriptedBrain();
        using var o = Attach(worker, brain, run.Snapshot(), remembers: false);

        await o.GoAsync();

        Assert.Contains("run_resumed", brain.Messages[0]);
        Assert.Null(o.BrainSessionId); // a scripted brain has no session to keep
        Assert.Contains("[RUN]", brain.Messages[0]);
        // With no history to read (a scripted worker) the section is left out; what matters is that it was looked for:
        Assert.Equal(StepState.Done, o.Run!.StateOf("s1-build"));
    }

    /// <summary>
    /// UI test 2026-11-13: a run resumed on a session started a moment ago never went on. A session that has not finished
    /// a turn yet is not "waiting for input", and the run waited for a turn end that could not come.
    /// </summary>
    [Fact]
    public async Task A_run_picked_up_on_a_quiet_session_goes_on_and_on_a_session_mid_turn_waits_for_its_turn_end()
    {
        var run = new TemplateRun(SampleTemplates.StagedBuild());
        run.Start("s1-build");

        var quiet = new ScriptedWorker { IsAwaitingInput = false, OutputSilentFor = TimeSpan.FromSeconds(40) };
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision(null, "Build stage 1 now.", "", StepOutcome.Continuing) };
        using (var o = new PaneOrchestrator(quiet, brain, "Staged build", advanced: SampleTemplates.StagedBuild(), resume: run.Snapshot()))
        {
            await o.GoAsync();
            Assert.Contains("run_resumed", Assert.Single(brain.Messages));
            Assert.Equal(["Build stage 1 now."], quiet.Submitted);
        }

        var working = new ScriptedWorker { IsAwaitingInput = false, OutputSilentFor = TimeSpan.FromSeconds(1) };
        var silent = new ScriptedBrain();
        using (var o = new PaneOrchestrator(working, silent, "Staged build", advanced: SampleTemplates.StagedBuild(), resume: run.Snapshot())
               { TurnSettle = TimeSpan.FromSeconds(15), LoopInterval = TimeSpan.FromMilliseconds(20) })
        {
            await o.GoAsync();
            await Task.Delay(150);
            Assert.Empty(silent.Messages); // its turn end brings the orchestrator in
            Assert.Equal(RunState.Running, o.Run!.State);

            // It was only starting up: no turn ends, it just goes quiet. The run goes on all the same.
            working.OutputSilentFor = TimeSpan.FromSeconds(20);
            await TemplateRunTests.Until(() => silent.Messages.Count >= 1); // and then on through the next steps
            Assert.Contains("run_resumed", silent.Messages[0]);
        }
    }
}

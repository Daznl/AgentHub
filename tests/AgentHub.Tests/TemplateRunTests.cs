using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// A run of an advanced template: AgentHub keeps the place and moves it, the orchestrator words the instructions and
/// judges each step from the worker's replies. Driven here with a scripted worker and a scripted brain.
/// </summary>
public class TemplateRunTests
{
    // ---- The owner's example (2026-11-13): branch, read, build, watch, hand off, commit, close; then stage 2 in a fresh session ----

    internal static AdvancedTemplate StagedBuild() => new()
    {
        Name = "Staged build",
        Stages =
        [
            new()
            {
                Id = "stage-1", Title = "Stage 1",
                Steps =
                [
                    new() { Id = "s1-branch", Kind = StepKind.CreateBranch, Branch = "feature/stage-1" },
                    new() { Id = "s1-build", Kind = StepKind.Instruct, Title = "Build stage 1", Text = "Build stage 1 of docs/PLAN.md." },
                    new() { Id = "s1-watch", Kind = StepKind.WaitForWorker, Escalate = true },
                    new() { Id = "s1-handoff", Kind = StepKind.Handoff },
                    new() { Id = "s1-commit", Kind = StepKind.Commit },
                    new() { Id = "s1-close", Kind = StepKind.CloseSession },
                ],
            },
            new()
            {
                Id = "stage-2", Title = "Stage 2",
                Steps =
                [
                    new() { Id = "s2-session", Kind = StepKind.NewSession },
                    new() { Id = "s2-build", Kind = StepKind.Instruct, Text = "Read .agenthub/handoff.md, then build stage 2." },
                    new() { Id = "s2-check", Kind = StepKind.AskOwner, Text = "Stage 2 is built. Open the pull request?" },
                    new() { Id = "s2-pr", Kind = StepKind.OpenPullRequest, Target = "main" },
                ],
            },
        ],
    };

    internal sealed class ScriptedWorker : IBuildWorker
    {
        public string Label => "Terminal 1";
        public bool IsRunning { get; set; } = true;
        public bool IsAwaitingInput { get; set; } = true;
        public TimeSpan OutputSilentFor { get; set; } = TimeSpan.FromMinutes(1);
        public string? TranscriptPath => null;
        public string? LastReply { get; set; }
        public List<string> Submitted { get; } = [];
        public List<string> Started { get; } = [];
        public int Stops { get; private set; }
        public ContextReading? LatestContextReading => null;
        public SessionLaunch? SessionLaunch { get; set; } = new SessionLaunch("claude", @"C:\My Project", "My Project", "My Project");
        public bool IsAtSecurityCheck { get; set; }
        /// <summary>Answer every instruction at once with "did: …", as a worker that never has a question.</summary>
        public bool Obeys { get; set; }
        public event Action? TurnEnded;
        public event Action? Exited;
#pragma warning disable CS0067 // a scripted worker's context never changes
        public event Action<ContextReading?>? ContextChanged;
#pragma warning restore CS0067
        public void Start(string commandLine, string workingDirectory, string title, string sessionName) { Started.Add($"{commandLine} in {workingDirectory}"); IsRunning = true; LastReply = null; }
        public void Stop() { Stops++; IsRunning = false; Exited?.Invoke(); }
        public void Die() { IsRunning = false; Exited?.Invoke(); }
        public Task SubmitAsync(string prompt)
        {
            Submitted.Add(prompt);
            if (Obeys) _ = Task.Run(async () => { await Task.Delay(10); EndTurn("did: " + prompt); });
            return Task.CompletedTask;
        }
        public string? ReadLastReply() => LastReply;
        public IReadOnlyList<HistoryEntry> ReadHistory() => [];
        public void EndTurn(string reply) { LastReply = reply; TurnEnded?.Invoke(); }
    }

    internal sealed class ScriptedBrain : IPaneBrain
    {
        private readonly object _lock = new();
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages { get { lock (_lock) return _messages.ToList(); } }
        public Func<string, PaneDecision> Answer { get; set; } = Follow;
        public Task<PaneDecision> DecideAsync(string message, CancellationToken cancellationToken)
        {
            lock (_lock) _messages.Add(message);
            return Task.FromResult(Answer(message));
        }

        /// <summary>The id of the current step a message's [RUN] section names.</summary>
        public static string Step(string message) => Regex.Match(message, @"CURRENT STEP: id (\S+),").Groups[1].Value;

        /// <summary>An orchestrator that follows the template to the letter: words each step's instruction, and calls a step done when the worker replied.</summary>
        public static PaneDecision Follow(string message)
        {
            if (message.Contains("[WORKER FINAL REPLY]") || message.Contains("the owner answered")) return new PaneDecision(null, null, "", StepOutcome.Done);
            if (message.Contains("step_started")) return new PaneDecision(null, "do " + Step(message), "", StepOutcome.Continuing);
            return new PaneDecision("Noted.", null, "");
        }
    }

    private static PaneOrchestrator Attach(ScriptedWorker worker, ScriptedBrain brain, AdvancedTemplate? template = null) =>
        new(worker, brain, "Staged build", advanced: template ?? StagedBuild()) { TurnSettle = TimeSpan.Zero, StopDelay = TimeSpan.Zero, StartupDelay = TimeSpan.Zero, LoopInterval = TimeSpan.FromMilliseconds(20) };

    internal static async Task Until(Func<bool> done, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(done());
    }

    // ---- The run's own state ----

    [Fact]
    public void A_run_steps_through_the_template_in_order_and_knows_where_it_is()
    {
        var run = new TemplateRun(StagedBuild());

        Assert.Equal(RunState.Ready, run.State);
        Assert.Null(run.Current);
        Assert.Equal(10, run.Steps.Count);
        Assert.Equal("10 steps, not started", run.Progress);
        Assert.Contains("has not started", run.Describe());

        run.Start();
        Assert.Equal(RunState.Running, run.State);
        Assert.Equal("s1-branch", run.Current!.Step.Id);
        Assert.Equal(StepState.Running, run.StateOf("s1-branch"));
        Assert.Equal(StepState.Pending, run.StateOf("s1-build"));
        Assert.Equal("step 1 of 10 · Stage 1 · Create branch feature/stage-1", run.Progress);

        Assert.Equal("s1-build", run.CompleteCurrent()!.Step.Id);
        Assert.Equal(StepState.Done, run.StateOf("s1-branch"));
        Assert.Equal(1, run.DoneCount);

        run.Pause(PauseCause.Owner, "Paused by you.");
        Assert.Equal(RunState.Paused, run.State);
        Assert.Equal(PauseCause.Owner, run.Cause);
        Assert.Equal("s1-build", run.CompleteCurrent()!.Step.Id); // a paused run moves nowhere
        Assert.Equal(1, run.DoneCount);
        run.Start(); // Go carries it on where it stands
        Assert.Equal(RunState.Running, run.State);
        Assert.Null(run.Cause);
        Assert.Equal("s1-build", run.Current!.Step.Id);

        while (run.CompleteCurrent() is not null) { }
        Assert.Equal(RunState.Finished, run.State);
        Assert.Null(run.Current);
        Assert.Equal(10, run.DoneCount);
        Assert.Equal("all 10 steps done", run.Progress);

        run.Start(); // Go on a finished run starts it over
        Assert.Equal("s1-branch", run.Current!.Step.Id);
        Assert.Equal(0, run.DoneCount);
    }

    [Fact]
    public void Going_on_from_a_step_skips_what_was_not_done_before_it_and_redoes_what_comes_after()
    {
        var run = new TemplateRun(StagedBuild());
        run.Start();
        run.CompleteCurrent(); // s1-branch done
        run.CompleteCurrent(); // s1-build done; at s1-watch

        run.Start("s1-commit");
        Assert.Equal("s1-commit", run.Current!.Step.Id);
        Assert.Equal(StepState.Done, run.StateOf("s1-branch"));
        Assert.Equal(StepState.Skipped, run.StateOf("s1-watch"));
        Assert.Equal(StepState.Skipped, run.StateOf("s1-handoff"));
        Assert.Equal(StepState.Running, run.StateOf("s1-commit"));

        run.Start("s1-build"); // back: the done step is to do again
        Assert.Equal(StepState.Done, run.StateOf("s1-branch"));
        Assert.Equal(StepState.Running, run.StateOf("s1-build"));
        Assert.Equal(StepState.Pending, run.StateOf("s1-commit"));

        Assert.Throws<ArgumentException>(() => run.Start("no-such-step"));
    }

    [Fact]
    public void The_orchestrator_is_told_the_current_step_what_it_means_and_what_comes_next()
    {
        var run = new TemplateRun(StagedBuild());
        run.Start("s1-watch");
        var text = run.Describe();

        Assert.Contains("Step 3 of 10, in stage \"Stage 1\" (1 of 2). The run is running.", text);
        Assert.Contains("CURRENT STEP: id s1-watch, kind wait-for-worker: Wait for the worker to finish", text);
        Assert.Contains("ESCALATION POINT", text);
        Assert.Contains("The step after it (not yet): handoff", text);

        run.Pause(PauseCause.Owner, null);
        Assert.Contains("PAUSED by the owner", run.Describe());

        run.Start("s2-pr");
        Assert.Contains("The worker is to be told: Push the current branch and open a pull request into main.", run.Describe());
        Assert.Contains("It is the last step.", run.Describe());
        Assert.DoesNotContain("ESCALATION POINT", run.Describe());
    }

    [Fact]
    public void Each_step_for_the_worker_has_an_instruction_of_its_own_and_the_others_have_none()
    {
        string? For(TemplateStep step) => TemplateRun.WorkerInstruction(step);

        Assert.Equal("Run the tests.", For(new() { Kind = StepKind.Instruct, Text = " Run the tests. " }));
        Assert.Equal("Create the branch feature/x from the current state and switch to it.", For(new() { Kind = StepKind.CreateBranch, Branch = "feature/x" }));
        Assert.Equal("Commit your work to the current branch with a clear message. Leave the generated files out.", For(new() { Kind = StepKind.Commit, Text = "Leave the generated files out." }));
        Assert.Contains("Merge the branch feature/x into develop.", For(new() { Kind = StepKind.MergeBranch, Branch = "feature/x", Target = "develop" }));
        Assert.Contains("No force", For(new() { Kind = StepKind.MergeBranch, Branch = "a", Target = "b" }));
        Assert.Contains("pull request into the trunk", For(new() { Kind = StepKind.OpenPullRequest }));
        Assert.Contains(".agenthub/handoff.md", For(new() { Kind = StepKind.Handoff }));
        Assert.Contains("onto the branch feature/x", For(new() { Kind = StepKind.MoveWorkToBranch, Branch = "feature/x" }));
        foreach (var kind in new[] { StepKind.WaitForWorker, StepKind.AskOwner, StepKind.NewSession, StepKind.CloseSession })
            Assert.Null(For(new() { Kind = kind, Text = "x" }));
    }

    [Fact]
    public void A_template_with_problems_cannot_be_run()
    {
        var broken = StagedBuild();
        broken.Stages[0].After = ["stage-2"];

        var ex = Assert.Throws<InvalidOperationException>(() => new TemplateRun(broken));
        Assert.Contains("cannot loop", ex.Message);
    }

    // ---- The orchestrator's reply and prompt ----

    [Fact]
    public void The_reply_of_a_run_carries_the_step_outcome()
    {
        PaneDecision Parse(string json) => PaneDecision.Parse(JsonDocument.Parse(json).RootElement);

        Assert.Equal(StepOutcome.Done, Parse("""{ "to_owner": null, "to_worker": null, "note": "", "step_outcome": "done" }""").Outcome);
        Assert.Equal(StepOutcome.Continuing, Parse("""{ "to_owner": null, "to_worker": "go", "note": "", "step_outcome": "Continuing" }""").Outcome);
        Assert.Equal(StepOutcome.Escalate, Parse("""{ "to_owner": "help", "to_worker": null, "note": "", "step_outcome": "escalate" }""").Outcome);
        Assert.Null(Parse("""{ "to_owner": null, "to_worker": null, "note": "", "step_outcome": null }""").Outcome);
        Assert.Null(Parse("""{ "to_owner": "hi", "to_worker": null, "note": "" }""").Outcome); // an orchestrator without a run
        Assert.Throws<FormatException>(() => Parse("""{ "to_owner": null, "to_worker": null, "note": "", "step_outcome": "skip" }"""));

        using var schema = JsonDocument.Parse(PaneDecision.RunSchema);
        Assert.Contains("step_outcome", schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("step_outcome", PaneDecision.Schema);
    }

    [Fact]
    public void Only_an_orchestrator_with_a_run_gets_the_runs_rules_and_the_templates_outline()
    {
        var prompt = PaneOrchestrator.SystemPrompt(@"C:\My Project", "Terminal 1", null, advanced: StagedBuild());

        Assert.Contains("RUN: YOU FOLLOW AN ADVANCED TEMPLATE", prompt);
        Assert.Contains("step_outcome", prompt);
        Assert.Contains("Rules you never break", prompt); // the fixed rules still stand
        Assert.Contains("Stage \"Stage 1\"", prompt);
        Assert.Contains("- [wait-for-worker] Wait for the worker to finish (escalation point)", prompt);
        Assert.Contains("- [open-pull-request] Open a pull request into main", prompt);
        Assert.DoesNotContain("step_outcome", PaneOrchestrator.SystemPrompt(@"C:\My Project", "Terminal 1", null));
    }

    // ---- Following a run ----

    [Fact]
    public async Task The_run_goes_through_every_step_in_order_and_stops_only_for_the_owners_question()
    {
        var worker = new ScriptedWorker { Obeys = true };
        var brain = new ScriptedBrain();
        using var o = Attach(worker, brain);

        Assert.Equal(RunState.Ready, o.Run!.State);
        Assert.Equal("RUN READY", OrchestratorBoxView.For(o).State);
        await o.SendAsync("The plan is docs/PLAN.md."); // before Go it is an orchestrator like any other
        Assert.Empty(worker.Submitted);
        Assert.Contains("[RUN] Advanced template \"Staged build\". The run has not started", brain.Messages[0]);

        await o.GoAsync();
        await Until(() => o.Run.State == RunState.Paused);

        // stage 1 in order, the session closed, a fresh one started, stage 2 up to the question
        Assert.Equal(["do s1-branch", "do s1-build", "do s1-handoff", "do s1-commit", "do s2-build"], worker.Submitted);
        Assert.Equal(1, worker.Stops);
        Assert.Equal([@"claude in C:\My Project"], worker.Started);
        Assert.Equal(2, o.Session);
        Assert.Equal(PauseCause.Question, o.Run.Cause);
        Assert.Equal("s2-check", o.Run.Current!.Step.Id);
        Assert.Equal("RUN WAITING FOR YOU", OrchestratorBoxView.For(o).State);
        Assert.StartsWith("step 9 of 10 · Stage 2 · Stage 2 is built.", OrchestratorBoxView.For(o).Note);
        Assert.Equal("Stage 2 is built. Open the pull request?", o.Conversation.Last(x => x.Kind == ExchangeKind.Question).Text);
        Assert.True(o.KeepsEndedSession);

        // the wait step was judged at once on the build's reply, with nothing sent for it
        var wait = brain.Messages.Single(m => m.Contains("step_started") && ScriptedBrain.Step(m) == "s1-watch");
        Assert.Contains("[WORKER FINAL REPLY]\ndid: do s1-build", wait);
        // the fresh session's first step says so
        var fresh = brain.Messages.Single(m => m.Contains("step_started") && ScriptedBrain.Step(m) == "s2-build");
        Assert.Contains("the worker session is fresh", fresh);
        Assert.Contains("session 2", fresh);

        await o.SendAsync("Yes, open it.");
        await Until(() => o.Run.State == RunState.Finished);

        Assert.Equal("do s2-pr", worker.Submitted[^1]);
        Assert.Equal(10, o.Run.DoneCount);
        Assert.All(o.Run.Steps, s => Assert.Equal(StepState.Done, o.Run.StateOf(s.Step.Id)));
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.StartsWith("Run finished: all 10 steps"));
        Assert.Equal("RUN FINISHED", OrchestratorBoxView.For(o).State);
        Assert.False(o.KeepsEndedSession);
    }

    [Fact]
    public async Task An_escalation_pauses_the_run_until_the_owner_has_answered()
    {
        var worker = new ScriptedWorker { LastReply = "Stage 1 needs a paid API key. Which one?" };
        var brain = new ScriptedBrain
        {
            Answer = m => m.Contains("[FROM OWNER]") ? new PaneDecision("Passed on.", "Use the sandbox key in .env.example.", "", StepOutcome.Continuing)
                : new PaneDecision("The worker needs an API key; which one?", "Wait.", "blocked on a key", StepOutcome.Escalate),
        };
        using var o = Attach(worker, brain);

        await o.GoAsync("s1-watch");

        Assert.Equal(RunState.Paused, o.Run!.State);
        Assert.Equal(PauseCause.Escalation, o.Run.Cause);
        Assert.Equal("The worker needs an API key; which one?", o.Run.PauseReason);
        Assert.Empty(worker.Submitted); // nothing goes to the worker with an escalation
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Reply && x.Text.StartsWith("The worker needs an API key"));

        worker.EndTurn("Still waiting for the key."); // a paused run leaves the worker alone
        await Task.Delay(150);
        Assert.Single(brain.Messages);

        await o.SendAsync("The sandbox one.");

        Assert.Contains("paused on your escalation", brain.Messages[1]);
        Assert.Equal(RunState.Running, o.Run.State);
        Assert.Equal("s1-watch", o.Run.Current!.Step.Id);
        Assert.Equal(["Use the sandbox key in .env.example."], worker.Submitted);
    }

    [Fact]
    public async Task The_orchestrator_cannot_start_a_fresh_session_of_its_own_during_a_run()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision(null, "Build stage 1 of docs/PLAN.md.", "", StepOutcome.Continuing, NewSession: "new work") };
        using var o = Attach(worker, brain);

        await o.GoAsync("s1-build");

        Assert.Equal(["Build stage 1 of docs/PLAN.md."], worker.Submitted); // the step's instruction still goes in
        Assert.Equal(0, worker.Stops);
        Assert.False(o.IsHandingOff);
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.Contains("an advanced-template run is in progress"));
        Assert.Contains("new_session", PaneDecision.RunSchema);
    }

    [Fact]
    public async Task Pause_stops_the_run_where_it_is_and_go_picks_it_up_from_the_workers_latest_reply()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain();
        using var o = Attach(worker, brain);
        await o.GoAsync("s1-build");
        Assert.Equal(["do s1-build"], worker.Submitted);

        o.PauseRun();
        Assert.Equal(PauseCause.Owner, o.Run!.Cause);
        Assert.Equal("RUN PAUSED", OrchestratorBoxView.For(o).State);

        worker.EndTurn("Stage 1 built, 14 tests pass."); // finished while paused: nobody is told, nothing moves
        await Task.Delay(150);
        Assert.Single(brain.Messages);
        Assert.Equal("s1-build", o.Run.Current!.Step.Id);

        brain.Answer = _ => new PaneDecision("Paused, as you asked.", "Carry on with stage 2.", "");
        await o.SendAsync("How far is it?"); // the orchestrator still answers, and cannot reach the worker
        Assert.Contains("PAUSED by the owner", brain.Messages[1]);
        Assert.Equal(["do s1-build"], worker.Submitted);
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.StartsWith("Not sent to the worker (the run is paused)"));

        brain.Answer = ScriptedBrain.Follow;
        worker.Obeys = true;
        await o.GoAsync();
        await Until(() => o.Run.Current?.Step.Id == "s1-close" || o.Run.DoneCount >= 5);

        var resumed = brain.Messages[2];
        Assert.Contains("run_resumed", resumed);
        Assert.Contains("[WORKER FINAL REPLY]\nStage 1 built, 14 tests pass.", resumed);
        Assert.Equal(StepState.Done, o.Run.StateOf("s1-build"));
        Assert.Equal("do s1-build", worker.Submitted[0]); // the build was not sent a second time
        Assert.DoesNotContain("do s1-build", worker.Submitted.Skip(1));
    }

    [Fact]
    public async Task A_step_the_orchestrator_words_nothing_for_still_reaches_the_worker_and_done_sends_nothing()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision(null, null, "", StepOutcome.Continuing) };
        using var o = Attach(worker, brain);

        await o.GoAsync("s1-commit");
        Assert.Equal(["Commit your work to the current branch with a clear message."], worker.Submitted);

        // "done" with an instruction attached: the step is done, the instruction is dropped, the next step asks for its own
        brain.Answer = m => m.Contains("worker_turn_ended") ? new PaneDecision(null, "Now open the pull request.", "", StepOutcome.Done) : new PaneDecision(null, null, "");
        worker.EndTurn("Committed as 1a2b3c4.");
        await Until(() => o.Run!.Current?.Step.Id == "s2-session" || o.Run.DoneCount >= 2);

        Assert.DoesNotContain("Now open the pull request.", worker.Submitted);
        Assert.Equal(StepState.Done, o.Run!.StateOf("s1-commit"));
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text == "✓ Commit to the branch");
    }

    [Fact]
    public async Task Without_a_worker_session_the_run_stops_and_says_so_and_the_orchestrator_stays_on()
    {
        var worker = new ScriptedWorker(); // it does not answer yet: an obeying worker raced the run past this step before it "died" (flaky, 2026-11-13)
        var brain = new ScriptedBrain();
        using var o = Attach(worker, brain);
        await o.GoAsync("s1-build");

        worker.Die(); // the session ends by itself mid-run
        Assert.Equal(RunState.Paused, o.Run!.State);
        Assert.Equal(PauseCause.Problem, o.Run.Cause);
        Assert.Contains("The worker session ended", o.Run.PauseReason);
        Assert.True(o.KeepsEndedSession);
        Assert.Equal("RUN STOPPED", OrchestratorBoxView.For(o).State);

        var asked = brain.Messages.Count;
        await o.GoAsync(); // still no session: it stops again, without asking the orchestrator
        Assert.Equal(PauseCause.Problem, o.Run.Cause);
        Assert.Contains("No worker session is running", o.Run.PauseReason);
        Assert.Equal(asked, brain.Messages.Count);

        worker.Obeys = true;
        await o.GoAsync("s2-session"); // a new-session step brings one back
        await Until(() => o.Run.Cause == PauseCause.Question);
        Assert.Single(worker.Started);
        Assert.Equal("do s2-build", worker.Submitted[^1]);
    }

    [Fact]
    public async Task A_failed_decision_pauses_the_run_at_its_step()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = _ => throw new OrchestratorBrainException("usage limit reached", usageLimit: true) };
        using var o = Attach(worker, brain);

        await o.GoAsync();

        Assert.Equal(RunState.Paused, o.Run!.State);
        Assert.Equal(PauseCause.Problem, o.Run.Cause);
        Assert.Equal("s1-branch", o.Run.Current!.Step.Id);
        Assert.Empty(worker.Submitted);
    }

    [Fact]
    public async Task An_orchestrator_without_a_template_has_no_run()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision(null, "go", "", StepOutcome.Done) };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator");

        Assert.Null(o.Run);
        Assert.False(o.KeepsEndedSession);
        await Assert.ThrowsAsync<InvalidOperationException>(() => o.GoAsync());
        o.PauseRun(); // nothing to pause
        await o.SendAsync("hello");
        Assert.DoesNotContain("[RUN]", brain.Messages[0]);
        Assert.Equal(["go"], worker.Submitted); // a stray step_outcome changes nothing
    }
}

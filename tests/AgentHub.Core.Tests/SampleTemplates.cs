using AgentHub.Services.Orchestrator;

namespace AgentHub.Tests;

/// <summary>Templates shared by the run, store, editing and tree tests (and, linked, by the Windows run tree panel tests).</summary>
internal static class SampleTemplates
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
}

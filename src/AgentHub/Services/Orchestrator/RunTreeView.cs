namespace AgentHub.Services.Orchestrator;

/// <summary>How far a run has got with a stage or step, as the run tree beside a pane colours it.</summary>
public enum RunTreeState { Pending, Current, Done, Skipped }

/// <summary>One row of the run tree beside a pane: a stage heading or a step, with how far the run has got with it.</summary>
/// <param name="Id">The stage or step id.</param>
/// <param name="Detail">A stage's size, or a step's full text when the title is not it; shown when the row is opened.</param>
/// <param name="Icon">A step's kind as one character; null for a stage.</param>
/// <param name="Label">A step's kind in words; null for a stage.</param>
public sealed record RunTreeRow(TreeRowKind Kind, string Id, string Title, string? Detail, string? Icon, string? Label, bool Escalate, RunTreeState State);

/// <summary>
/// The run tree beside a pane (owner request 2026-11-18): the tree of the advanced template a pane's orchestrator follows,
/// small, next to the session it drives, coloured by how far the run has got. The same rows as the Advanced templates window
/// (<see cref="AdvancedTemplateTree"/>), without its start and end caps. Kept apart from the control so it can be tested.
/// </summary>
public static class RunTreeView
{
    public static IReadOnlyList<RunTreeRow> Rows(TemplateRun run)
    {
        var stages = run.Template.Stages.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<RunTreeRow>();
        foreach (var row in AdvancedTemplateTree.Rows(run.Template))
        {
            if (row.Id is null) continue; // Start and Done
            var state = row.Kind == TreeRowKind.Stage
                ? StageState(stages.TryGetValue(row.Id, out var stage) ? stage.Steps.Select(s => StepState(run, s.Id)).ToList() : [])
                : StepState(run, row.Id);
            rows.Add(new RunTreeRow(row.Kind, row.Id, row.Title, row.Detail, row.Icon, row.Label, row.Escalate, state));
        }
        return rows;
    }

    /// <summary>Changes whenever any row's colour would: the run's state, its place and every step's state.</summary>
    public static string Stamp(TemplateRun run) =>
        $"{run.State}|{run.CurrentIndex}|" + string.Concat(run.Steps.Select(s => (int)run.StateOf(s.Step.Id)));

    /// <summary>"3 of 12 steps done".</summary>
    public static string Summary(TemplateRun run) => $"{run.DoneCount} of {run.Steps.Count} steps done";

    /// <summary>A step's state as the tree shows it: the step the run is at is current, paused there or not.</summary>
    private static RunTreeState StepState(TemplateRun run, string stepId) => run.StateOf(stepId) switch
    {
        Orchestrator.StepState.Done => RunTreeState.Done,
        Orchestrator.StepState.Skipped => RunTreeState.Skipped,
        Orchestrator.StepState.Running => RunTreeState.Current,
        _ => RunTreeState.Pending,
    };

    /// <summary>As the Advanced templates window: done once every step is done or skipped and one at least is done.</summary>
    private static RunTreeState StageState(IReadOnlyList<RunTreeState> steps)
    {
        if (steps.Contains(RunTreeState.Current)) return RunTreeState.Current;
        if (steps.Count > 0 && steps.All(s => s is RunTreeState.Done or RunTreeState.Skipped))
            return steps.Contains(RunTreeState.Done) ? RunTreeState.Done : RunTreeState.Skipped;
        return RunTreeState.Pending;
    }
}

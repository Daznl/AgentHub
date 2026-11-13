namespace AgentHub.Services.Orchestrator;

public enum TreeRowKind { Start, Stage, Step, End }

/// <summary>One row of an advanced template's tree, top to bottom: the start, a stage heading, a step, or the end.</summary>
/// <param name="Id">The stage or step id (a run ticks rows off by it); null for the start and the end.</param>
/// <param name="Detail">A second, quieter line: a stage's size and what it waits for, or a step's full text when the title is not it.</param>
/// <param name="Icon">A step's kind as one character.</param>
/// <param name="Label">A step's kind in words.</param>
/// <param name="Escalate">The step is an escalation point.</param>
public sealed record TreeRow(TreeRowKind Kind, string? Id, string Title, string? Detail = null, string? Icon = null, string? Label = null, bool Escalate = false);

/// <summary>
/// What the Advanced templates window draws for a template: the tree as rows, in the order a single worker follows. Kept
/// apart from the window so it can be tested without one.
/// </summary>
public static class AdvancedTemplateTree
{
    /// <summary>
    /// The rows, stage by stage. Stages come in <see cref="AdvancedTemplate.ExecutionOrder"/>; a template with problems
    /// (<see cref="AdvancedTemplate.Validate"/>) may have no such order and is shown in file order instead.
    /// </summary>
    public static IReadOnlyList<TreeRow> Rows(AdvancedTemplate template)
    {
        var sound = template.Validate().Count == 0;
        var stages = sound ? template.ExecutionOrder() : template.Stages;
        var titles = template.Stages.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().DisplayTitle);

        var rows = new List<TreeRow> { new(TreeRowKind.Start, null, "Start") };
        foreach (var stage in stages)
        {
            rows.Add(new(TreeRowKind.Stage, stage.Id, stage.DisplayTitle, StageDetail(stage, titles)));
            foreach (var step in stage.Steps)
            {
                var title = step.DisplayTitle;
                var text = step.Text?.Trim();
                rows.Add(new(TreeRowKind.Step, step.Id, title, string.IsNullOrEmpty(text) || text == title ? null : text,
                    Icon(step.Kind), Label(step.Kind), step.Escalate));
            }
        }
        rows.Add(new(TreeRowKind.End, null, "Done"));
        return rows;
    }

    /// <summary>"2 stages · 12 steps".</summary>
    public static string Summary(AdvancedTemplate template)
    {
        var stages = template.Stages.Count;
        var steps = template.AllSteps.Count();
        return $"{Count(stages, "stage")} · {Count(steps, "step")}";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>A stage's size, and what it waits for when the file says so (left out, it simply follows the stage above).</summary>
    private static string StageDetail(TemplateStage stage, Dictionary<string, string> titles)
    {
        var size = Count(stage.Steps.Count, "step");
        return stage.After switch
        {
            null => size,
            { Count: 0 } => size + " · starts at once",
            var after => size + " · after " + string.Join(" and ", after.Select(id => titles.GetValueOrDefault(id, id))),
        };
    }

    public static string Icon(StepKind kind) => kind switch
    {
        StepKind.Instruct => "💬",
        StepKind.WaitForWorker => "⏳",
        StepKind.AskOwner => "🙋",
        StepKind.NewSession => "▶",
        StepKind.CloseSession => "⏹",
        StepKind.Handoff => "📝",
        StepKind.CreateBranch => "🌿",
        StepKind.Commit => "💾",
        StepKind.MergeBranch => "🔀",
        StepKind.MoveWorkToBranch => "📦",
        StepKind.OpenPullRequest => "📬",
        _ => "•",
    };

    public static string Label(StepKind kind) => kind switch
    {
        StepKind.Instruct => "Instruct worker",
        StepKind.WaitForWorker => "Wait for worker",
        StepKind.AskOwner => "Ask you",
        StepKind.NewSession => "New session",
        StepKind.CloseSession => "Close session",
        StepKind.Handoff => "Handoff",
        StepKind.CreateBranch => "Create branch",
        StepKind.Commit => "Commit",
        StepKind.MergeBranch => "Merge",
        StepKind.MoveWorkToBranch => "Move work",
        StepKind.OpenPullRequest => "Pull request",
        _ => AdvancedTemplate.KindName(kind),
    };
}

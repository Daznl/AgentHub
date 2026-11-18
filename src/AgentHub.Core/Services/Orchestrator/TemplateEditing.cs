namespace AgentHub.Services.Orchestrator;

/// <summary>
/// The changes the Advanced templates window makes to a draft: add, remove and move stages and steps. Kept apart from the
/// window so they can be tested without one. A draft may be unsound in between (a stage with no steps yet, a step with no
/// text); <see cref="AdvancedTemplate.Validate"/> says so and <see cref="AdvancedTemplateStore.AcceptDraftAsync"/> refuses it.
/// </summary>
public static class TemplateEditing
{
    /// <summary>
    /// An id no step of <paramref name="draft"/> has, nor any of <paramref name="taken"/> (the ids of the saved template
    /// and of a run of it): a run's progress is recorded against step ids, so an id is never reused for another step.
    /// </summary>
    public static string NewStepId(AdvancedTemplate draft, TemplateStage stage, IEnumerable<string>? taken = null)
    {
        var used = new HashSet<string>(draft.AllSteps.Select(s => s.Id).Concat(taken ?? []), StringComparer.Ordinal);
        var prefix = OrchestratorMessages.Slug(stage.Id);
        for (var n = 1; ; n++)
        {
            var id = $"{prefix}-step-{n}";
            if (!used.Contains(id)) return id;
        }
    }

    /// <summary>A stage id no stage of <paramref name="draft"/> has, nor any of <paramref name="taken"/>.</summary>
    public static string NewStageId(AdvancedTemplate draft, IEnumerable<string>? taken = null)
    {
        var used = new HashSet<string>(draft.Stages.Select(s => s.Id).Concat(taken ?? []), StringComparer.Ordinal);
        for (var n = draft.Stages.Count + 1; ; n++)
        {
            var id = $"stage-{n}";
            if (!used.Contains(id)) return id;
        }
    }

    /// <summary>A new step of <paramref name="kind"/> at <paramref name="index"/> in <paramref name="stage"/> (at its end when out of range).</summary>
    public static TemplateStep AddStep(AdvancedTemplate draft, TemplateStage stage, int index, StepKind kind, IEnumerable<string>? taken = null)
    {
        var step = new TemplateStep { Id = NewStepId(draft, stage, taken), Kind = kind };
        stage.Steps.Insert(index < 0 || index > stage.Steps.Count ? stage.Steps.Count : index, step);
        return step;
    }

    /// <summary>A new, empty stage at <paramref name="index"/> (at the end when out of range).</summary>
    public static TemplateStage AddStage(AdvancedTemplate draft, int index, IEnumerable<string>? taken = null)
    {
        var id = NewStageId(draft, taken);
        var stage = new TemplateStage { Id = id, Title = "Stage " + (draft.Stages.Count + 1) };
        draft.Stages.Insert(index < 0 || index > draft.Stages.Count ? draft.Stages.Count : index, stage);
        return stage;
    }

    /// <summary>The stage that holds <paramref name="step"/>; null when none does.</summary>
    public static TemplateStage? StageOf(AdvancedTemplate draft, TemplateStep step) => draft.Stages.FirstOrDefault(s => s.Steps.Contains(step));

    public static bool RemoveStep(AdvancedTemplate draft, TemplateStep step) => StageOf(draft, step)?.Steps.Remove(step) ?? false;

    /// <summary>Removes the stage with its steps; stages that waited for it no longer do.</summary>
    public static bool RemoveStage(AdvancedTemplate draft, TemplateStage stage)
    {
        if (!draft.Stages.Remove(stage)) return false;
        foreach (var other in draft.Stages) other.After?.RemoveAll(id => id == stage.Id);
        return true;
    }

    /// <summary>Whether <see cref="MoveStep"/> by <paramref name="delta"/> can move it: not up from the very first step, nor down from the very last.</summary>
    public static bool CanMoveStep(AdvancedTemplate draft, TemplateStep step, int delta)
    {
        if (StageOf(draft, step) is not { } stage) return false;
        var si = draft.Stages.IndexOf(stage);
        var i = stage.Steps.IndexOf(step);
        return delta < 0 ? i > 0 || si > 0 : i < stage.Steps.Count - 1 || si < draft.Stages.Count - 1;
    }

    /// <summary>
    /// One place up (<paramref name="delta"/> -1) or down (+1). From the top of a stage it goes to the end of the stage
    /// above; from the bottom, to the start of the stage below. False when it cannot move that way.
    /// </summary>
    public static bool MoveStep(AdvancedTemplate draft, TemplateStep step, int delta)
    {
        if (delta == 0 || !CanMoveStep(draft, step, delta) || StageOf(draft, step) is not { } stage) return false;
        var si = draft.Stages.IndexOf(stage);
        var i = stage.Steps.IndexOf(step);
        var j = i + Math.Sign(delta);
        stage.Steps.RemoveAt(i);
        if (j >= 0 && j <= stage.Steps.Count && (delta < 0 || i < stage.Steps.Count))
        {
            // Within the stage: i < Count after the removal means the step was not its last.
            stage.Steps.Insert(j, step);
            return true;
        }
        if (delta < 0) draft.Stages[si - 1].Steps.Add(step);
        else draft.Stages[si + 1].Steps.Insert(0, step);
        return true;
    }

    /// <summary>Whether <see cref="MoveStage"/> by <paramref name="delta"/> can move it.</summary>
    public static bool CanMoveStage(AdvancedTemplate draft, TemplateStage stage, int delta)
    {
        var i = draft.Stages.IndexOf(stage);
        return i >= 0 && (delta < 0 ? i > 0 : i < draft.Stages.Count - 1);
    }

    /// <summary>The stage one place up (-1) or down (+1) in the file, with its steps.</summary>
    public static bool MoveStage(AdvancedTemplate draft, TemplateStage stage, int delta)
    {
        if (delta == 0 || !CanMoveStage(draft, stage, delta)) return false;
        var i = draft.Stages.IndexOf(stage);
        draft.Stages.RemoveAt(i);
        draft.Stages.Insert(i + Math.Sign(delta), stage);
        return true;
    }
}

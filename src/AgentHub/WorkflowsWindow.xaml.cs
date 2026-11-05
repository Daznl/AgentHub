using System.Windows;
using System.Windows.Controls;
using AgentHub.Services.Orchestrator;
using MessageBox = System.Windows.MessageBox;

namespace AgentHub;

/// <summary>
/// The workflow catalogue: built-in templates (read-only) and Daniel's own workflows. Duplicate a template, fill in its
/// inputs (a pipeline location, say) and instructions, save it, and pick it for an idea. Saved workflows live on the
/// agenthub-workflows branch of the AgentHub repository, so they are still there after a pause or on another machine.
/// </summary>
public partial class WorkflowsWindow : Window
{
    private readonly WorkflowCatalogue _catalogue;
    private readonly bool _canUse;
    private List<OrchestratorWorkflow> _workflows = [];
    private OrchestratorWorkflow? _current;
    private bool _loading;

    /// <summary>The workflow Daniel chose with "Use for this idea"; null when he just closed the window.</summary>
    public string? ChosenWorkflowId { get; private set; }

    public WorkflowsWindow(WorkflowCatalogue catalogue, string? selectedId, bool canUse)
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);
        _catalogue = catalogue;
        _canUse = canUse;
        UseBtn.Visibility = canUse ? Visibility.Visible : Visibility.Collapsed;
        StorageText.Text = $"Your workflows: {catalogue.OwnFolder}\nTemplates: {catalogue.TemplatesFolder}";
        Loaded += async (_, _) => await ReloadAsync(selectedId);
    }

    private async Task ReloadAsync(string? selectId)
    {
        _loading = true;
        try
        {
            _workflows = await _catalogue.ListAsync();
            WorkflowList.ItemsSource = _workflows;
            WorkflowList.SelectedItem = _workflows.FirstOrDefault(w => w.Id == selectId) ?? _workflows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read the catalogue: " + ex.Message;
        }
        finally { _loading = false; }
        Show(WorkflowList.SelectedItem as OrchestratorWorkflow);
    }

    private void WorkflowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (WorkflowList.SelectedItem is OrchestratorWorkflow w) Show(w);
    }

    private void Show(OrchestratorWorkflow? w)
    {
        _current = w;
        // Templates are editable too: Save stores the edited version as Daniel's own workflow and leaves the template as it is.
        var editable = w != null;
        NameBox.Text = w?.Name ?? "";
        DescriptionBox.Text = w?.Description ?? "";
        UseWhenBox.Text = w?.UseWhen ?? "";
        SelectModel(w?.Model ?? "sonnet");
        HandoffCombo.SelectedIndex = w?.Handoff == WorkflowHandoff.AfterEachStage ? 1 : 0;
        ThresholdBox.Text = w?.ContextThreshold is { } t ? $"{t * 100:0}%" : "";
        WorkerCommandBox.Text = w?.WorkerCommand ?? "";
        InputsBox.Text = w?.InputsText ?? "";
        InstructionsBox.Text = w?.Instructions ?? "";
        WorkerRulesBox.Text = w?.WorkerRules ?? "";

        foreach (var box in new[] { NameBox, DescriptionBox, UseWhenBox, ThresholdBox, WorkerCommandBox, InputsBox, InstructionsBox, WorkerRulesBox })
            box.IsReadOnly = !editable;
        ModelCombo.IsEnabled = HandoffCombo.IsEnabled = editable;
        SaveBtn.IsEnabled = editable;
        SaveBtn.Content = w is { IsTemplate: true } ? "💾 Save as my workflow" : "💾 Save";
        DeleteBtn.IsEnabled = w is { IsTemplate: false, Id.Length: > 0 };
        DuplicateBtn.IsEnabled = w != null;

        ModeText.Text = w switch
        {
            null => "No workflow selected.",
            { IsTemplate: true } => "📋 Template: change anything you like, then 💾 Save as my workflow. The template itself stays as it is." +
                (w.MissingInputs().Count > 0 ? $" Fill in: {string.Join(", ", w.MissingInputs())} (Inputs box)." : ""),
            { Id: "" } => "New workflow, not saved yet.",
            _ => w.MissingInputs() is { Count: > 0 } missing ? $"Fill in: {string.Join(", ", missing)}" : (w.BasedOn is { } b ? $"Based on {b}." : "")
        };
    }

    /// <summary>Selects the model in the list, adding it when a workflow file names one the list does not have.</summary>
    private void SelectModel(string model)
    {
        var item = ModelCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Content?.ToString(), model, StringComparison.OrdinalIgnoreCase));
        if (item == null) { item = new ComboBoxItem { Content = model }; ModelCombo.Items.Add(item); }
        ModelCombo.SelectedItem = item;
    }

    /// <summary>The editor's fields onto the workflow being edited.</summary>
    private void ReadFields(OrchestratorWorkflow w)
    {
        w.Name = NameBox.Text.Trim();
        w.Description = DescriptionBox.Text.Trim();
        w.UseWhen = UseWhenBox.Text.Trim();
        w.Model = (ModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() is { Length: > 0 } m ? m : "sonnet";
        w.Handoff = (HandoffCombo.SelectedItem as ComboBoxItem)?.Tag as string == "after-each-stage" ? WorkflowHandoff.AfterEachStage : WorkflowHandoff.OnContext;
        var t = ThresholdBox.Text.Replace("%", "").Trim();
        w.ContextThreshold = double.TryParse(t, out var v) ? Math.Clamp(v > 1 ? v / 100 : v, 0.40, 0.95) : null;
        w.WorkerCommand = string.IsNullOrWhiteSpace(WorkerCommandBox.Text) ? null : WorkerCommandBox.Text.Trim();
        w.InputsText = InputsBox.Text;
        w.Instructions = InstructionsBox.Text;
        w.WorkerRules = WorkerRulesBox.Text;
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var source = _current;
        if (!source.IsTemplate) { source = source.Duplicate(source.Name); ReadFields(source); }
        var copy = source.Duplicate(source.IsTemplate ? $"{source.Name} (my copy)" : $"{source.Name} (copy)");
        copy.BasedOn = _current.Name;
        WorkflowList.SelectedItem = null;
        Show(copy);
        NameBox.Focus();
        NameBox.SelectAll();
        StatusText.Text = "Rename it, fill in its inputs and instructions, then Save.";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        WorkflowList.SelectedItem = null;
        Show(new OrchestratorWorkflow
        {
            Name = "New workflow",
            Description = "What this workflow is for.",
            UseWhen = "When to pick it.",
            Instructions = "How the orchestrator should run this kind of job: phases, what to check, when it is done."
        });
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>The template with the editor's fields applied, as a new workflow of Daniel's (not saved yet).</summary>
    private OrchestratorWorkflow EditedCopyOf(OrchestratorWorkflow template)
    {
        var copy = template.Duplicate(template.Name);
        ReadFields(copy);
        if (copy.Name == template.Name || copy.Name.Length == 0) copy.Name = $"{template.Name} (my copy)";
        copy.BasedOn = template.Name;
        return copy;
    }

    /// <summary>True when the editor shows something other than the template as shipped.</summary>
    private bool TemplateEdited(OrchestratorWorkflow template)
    {
        var probe = template.Duplicate(template.Name);
        ReadFields(probe);
        probe.BasedOn = template.BasedOn;
        return probe.Serialize() != template.Duplicate(template.Name).Also(c => c.BasedOn = template.BasedOn).Serialize();
    }

    private async Task<bool> SaveAsync()
    {
        if (_current == null) return false;
        var w = _current.IsTemplate ? EditedCopyOf(_current) : _current;
        if (!_current.IsTemplate) ReadFields(w);
        _current = w;
        SaveBtn.IsEnabled = false;
        try
        {
            StatusText.Text = await _catalogue.SaveAsync(w);
            await ReloadAsync(w.Id);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save workflow", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { SaveBtn.IsEnabled = _current != null; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { IsTemplate: false, Id.Length: > 0 } w) return;
        if (MessageBox.Show(this, $"Delete the workflow \"{w.Name}\"? Ideas using it fall back to Builder. Its past runs keep their copy.",
                "Delete workflow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            StatusText.Text = await _catalogue.DeleteAsync(w);
            await ReloadAsync(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete workflow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (!_canUse || _current == null) return;
        // Edits are saved first (a changed template becomes Daniel's own workflow), so the idea points at what is on screen.
        if ((!_current.IsTemplate || TemplateEdited(_current)) && !await SaveAsync()) return;
        var chosen = _current;
        if (chosen.MissingInputs() is { Count: > 0 } missing)
        {
            MessageBox.Show(this,
                chosen.IsTemplate
                    ? $"\"{chosen.Name}\" needs {string.Join(", ", missing)} filled in: replace the <…> in the Inputs box with your value, then Use for this idea again."
                    : $"Fill in {string.Join(", ", missing)} first.",
                "Workflow inputs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ChosenWorkflowId = chosen.Id;
        DialogResult = true;
    }
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}

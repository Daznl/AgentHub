using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentHub.Services.Orchestrator;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AgentHub;

/// <summary>
/// 🗺 Advanced templates: a project's advanced templates (<c>orchestration/*.json</c> in its repository), each drawn as the
/// tree an orchestrator follows: stages, and under each its steps, top to bottom in the order a single worker takes them.
/// Read-only so far: the files are changed by hand or by a session, and re-read when the window is come back to.
/// </summary>
public partial class AdvancedTemplatesWindow : Window
{
    private readonly AdvancedTemplateStore _store;
    private string _shown = "";
    private bool _loading;

    /// <summary>The project folder whose templates are shown.</summary>
    public string Folder { get; }

    public AdvancedTemplatesWindow(string projectFolder)
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);
        Folder = projectFolder;
        _store = new AdvancedTemplateStore(projectFolder);
        HeadingText.Text = "🗺 Advanced templates · " + Path.GetFileName(projectFolder.TrimEnd('\\', '/'));
        SubheadingText.Text = "The sequence an orchestrator follows, step by step. Kept with the project in " + _store.Folder;
        Loaded += (_, _) => Reload(null, force: true);
        Activated += (_, _) => Reload((TemplateList.SelectedItem as ListItem)?.File.Id, force: false);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    /// <summary>One line of the list on the left.</summary>
    private sealed record ListItem(AdvancedTemplateFile File)
    {
        public string Title => File.Template?.Name is { Length: > 0 } name ? name : File.Id;
        public string Subtitle => File.Template is { } t ? AdvancedTemplateTree.Summary(t) + (t.Validate().Count > 0 ? " · has problems" : "") : "Cannot be read";
        public Brush SubtitleBrush => File.Template is { } t && t.Validate().Count == 0 ? Muted : Amber;
        public override string ToString() => Title; // what a screen reader and UI Automation call the item
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload((TemplateList.SelectedItem as ListItem)?.File.Id, force: true);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Directory.Exists(_store.Folder) ? _store.Folder : Folder;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText.Text = "Could not open " + folder + ": " + ex.Message; }
    }

    private async void AddExample_Click(object sender, RoutedEventArgs e)
    {
        AddExampleBtn.IsEnabled = false;
        try
        {
            var example = AdvancedTemplate.StagedBuildExample();
            await _store.SaveAsync(example);
            Reload(example.Id, force: true);
            StatusText.Text = "Wrote " + _store.PathFor(example.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { StatusText.Text = "Could not write the example: " + ex.Message; }
        finally { AddExampleBtn.IsEnabled = true; }
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) Show((TemplateList.SelectedItem as ListItem)?.File);
    }

    /// <summary>Reads the folder again. Unless forced, nothing is redrawn when no file changed, so coming back to the window keeps its place.</summary>
    private void Reload(string? selectId, bool force)
    {
        IReadOnlyList<AdvancedTemplateFile> files;
        string stamp;
        try
        {
            files = _store.List();
            stamp = string.Join("|", files.Select(f => $"{f.Path}:{File.GetLastWriteTimeUtc(f.Path).Ticks}:{f.HasDraft}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not read " + _store.Folder + ": " + ex.Message;
            return;
        }
        if (!force && stamp == _shown) return;
        _shown = stamp;

        var items = files.Select(f => new ListItem(f)).ToList();
        _loading = true;
        TemplateList.ItemsSource = items;
        TemplateList.SelectedItem = items.FirstOrDefault(i => i.File.Id == selectId) ?? items.FirstOrDefault();
        _loading = false;
        Show((TemplateList.SelectedItem as ListItem)?.File);
        StatusText.Text = files.Count == 0 ? "" : $"{(files.Count == 1 ? "1 template" : files.Count + " templates")} · read {DateTime.Now:HH:mm:ss}";
    }

    private void Show(AdvancedTemplateFile? file)
    {
        RowsPanel.Children.Clear();
        ProblemsBox.Visibility = Visibility.Collapsed;
        NoteText.Visibility = Visibility.Collapsed;
        if (file is null)
        {
            TreePanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyText.Text = "This project has no advanced templates yet.\n\nAn advanced template is a file in " + _store.Folder +
                             " that lists the stages and steps an orchestrator follows one by one.";
            return;
        }
        EmptyPanel.Visibility = Visibility.Collapsed;
        TreePanel.Visibility = Visibility.Visible;

        if (file.Template is not { } template)
        {
            TemplateName.Text = file.Id;
            TemplateSummary.Text = "";
            TemplateDescription.Text = file.Path;
            ShowProblems("This file cannot be read", [file.Error ?? "Unknown error."]);
            return;
        }

        TemplateName.Text = template.Name.Length > 0 ? template.Name : file.Id;
        TemplateSummary.Text = AdvancedTemplateTree.Summary(template);
        TemplateDescription.Text = template.Description ?? "";
        TemplateDescription.Visibility = string.IsNullOrWhiteSpace(template.Description) ? Visibility.Collapsed : Visibility.Visible;

        var problems = template.Validate();
        if (problems.Count > 0) ShowProblems(problems.Count == 1 ? "1 problem stops this template being run" : $"{problems.Count} problems stop this template being run", problems);

        var notes = new List<string>();
        if (problems.Count == 0 && template.HasParallelStages) notes.Add("Some stages do not depend on each other and could run side by side. This is the order a single worker follows.");
        if (file.HasDraft) notes.Add($"A change is waiting as a draft ({Path.GetFileName(_store.DraftPathFor(file.Id))}); it is not shown here.");
        if (notes.Count > 0)
        {
            NoteText.Text = string.Join("\n", notes);
            NoteText.Visibility = Visibility.Visible;
        }

        foreach (var row in AdvancedTemplateTree.Rows(template)) RowsPanel.Children.Add(BuildRow(row));
        Scroller.ScrollToTop();
    }

    private void ShowProblems(string title, IReadOnlyList<string> problems)
    {
        ProblemsTitle.Text = "⚠ " + title;
        ProblemsText.Text = problems.Count == 1 ? problems[0] : string.Join("\n", problems.Select(p => "• " + p));
        ProblemsBox.Visibility = Visibility.Visible;
    }

    // ---- The tree ----

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush Line = Frozen(0x2A, 0x3B, 0x55);
    private static readonly Brush Muted = Frozen(0x7E, 0x8C, 0xA6);
    private static readonly Brush Faint = Frozen(0x5A, 0x6B, 0x85);
    private static readonly Brush Amber = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush StageDot = Frozen(0xA7, 0x8B, 0xFA); // the orchestrator's purple
    private static readonly Brush StepCard = Frozen(0x0F, 0x17, 0x24);
    private static readonly Brush StepCardBorder = Frozen(0x1B, 0x27, 0x38);
    private static readonly Brush PanelFill = Frozen(0x0C, 0x12, 0x1C);
    private static readonly Brush Text = Frozen(0xE8, 0xF1, 0xFB);

    private const double GutterWidth = 30;

    /// <summary>One row: the trunk with this row's node on the left, what the row says on the right.</summary>
    private static Grid BuildRow(TreeRow row)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        // The trunk runs through every row; it starts at the Start node and stops at the Done node.
        var trunk = new Rectangle { Width = 2, Fill = Line, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true };
        Grid.SetRow(trunk, row.Kind == TreeRowKind.Start ? 1 : 0);
        Grid.SetRowSpan(trunk, row.Kind is TreeRowKind.Start or TreeRowKind.End ? 1 : 2);
        grid.Children.Add(trunk);

        (Ellipse node, FrameworkElement content) = row.Kind switch
        {
            TreeRowKind.Stage => (Node(14, StageDot, StageDot, top: 21), (FrameworkElement)StageContent(row)),
            TreeRowKind.Step => (Node(8, PanelFill, Muted, top: 17), StepContent(row)),
            _ => (Node(12, row.Kind == TreeRowKind.Start ? Muted : PanelFill, Muted, top: null), CapContent(row)),
        };
        Grid.SetRowSpan(node, 2);
        grid.Children.Add(node);
        Grid.SetColumn(content, 1);
        Grid.SetRowSpan(content, 2);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>A node on the trunk: centred in a one-line row, else level with the row's first line.</summary>
    private static Ellipse Node(double size, Brush fill, Brush stroke, double? top) => new()
    {
        Width = size,
        Height = size,
        Fill = fill,
        Stroke = stroke,
        StrokeThickness = 2,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = top is null ? VerticalAlignment.Center : VerticalAlignment.Top,
        Margin = new Thickness(0, top is { } t ? t - size / 2 : 0, 0, 0),
    };

    private static TextBlock CapContent(TreeRow row) => new()
    {
        Text = row.Title.ToUpperInvariant(),
        Foreground = Faint,
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(8, 6, 0, 6),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel StageContent(TreeRow row)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 10, 0, 4) };
        panel.Children.Add(new TextBlock { Text = row.Title, Foreground = Text, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (row.Detail is { } detail) panel.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static Border StepContent(TreeRow row)
    {
        var head = new DockPanel { LastChildFill = true };
        // Right to left: the kind, then the escalation flag before it.
        var kind = new TextBlock { Text = (row.Label ?? "").ToUpperInvariant(), Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0) };
        DockPanel.SetDock(kind, Dock.Right);
        head.Children.Add(kind);
        if (row.Escalate)
        {
            var flag = new Border
            {
                Background = Frozen(0x2B, 0x21, 0x10),
                BorderBrush = Frozen(0x7A, 0x5A, 0x12),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "An escalation point: anything here the orchestrator cannot answer from your instructions pauses the run and messages you.",
                Child = new TextBlock { Text = "⚑ pauses for you", Foreground = Amber, FontSize = 10, FontWeight = FontWeights.SemiBold },
            };
            DockPanel.SetDock(flag, Dock.Right);
            head.Children.Add(flag);
        }
        var icon = new TextBlock { Text = row.Icon, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"), FontSize = 12, Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = row.Title, Foreground = Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });

        var body = new StackPanel();
        body.Children.Add(head);
        if (row.Detail is { } detail)
            body.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 3, 0, 0) });

        return new Border
        {
            Background = StepCard,
            BorderBrush = StepCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 8),
            Margin = new Thickness(8, 3, 0, 3),
            Child = body,
        };
    }
}

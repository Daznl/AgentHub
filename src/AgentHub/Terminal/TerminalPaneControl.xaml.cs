using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentHub.Hosting;
using AgentHub.Models;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Instructions;
using AgentHub.Services.Orchestrator;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Terminal;

public partial class TerminalPaneControl : UserControl, IDisposable
{
    private static readonly Color AttentionColor = Color.FromRgb(0xF5, 0x9E, 0x0B);       // amber border
    private static readonly Color AttentionHeaderColor = Color.FromRgb(0x3B, 0x2A, 0x12); // amber-tinted header
    private static readonly Color HeaderBaseColor = Color.FromRgb(0x0E, 0x16, 0x22);
    private static readonly Color WorkingColor = Color.FromRgb(0x3A, 0x7B, 0xD5);        // soft blue border while the agent works
    private static readonly Color WorkingHeaderColor = Color.FromRgb(0x11, 0x24, 0x3E);  // blue-tinted header

    // Status dot colours, mirrored from the embedded terminal's session state.
    private static readonly SolidColorBrush IdleBrush = new(Color.FromRgb(0x5A, 0x6B, 0x85));
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(113, 113, 122));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(239, 68, 68));

    // Context badge bar colours: comfortable, getting full, near auto-compact.
    private static readonly SolidColorBrush ContextOkBrush = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush ContextWarnBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush ContextFullBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private const double ContextBarWidth = 36;

    private SolidColorBrush? _workingBorderBrush;
    private SolidColorBrush? _workingHeaderBrush;
    private readonly ContextWindowTracker _context = new(Controls.DispatcherTickTimer.Create);
    private long _claudeContextWindowTokens = 1_000_000;
    private const double PromptCollapsedMaxHeight = 18;
    private const double PromptExpandedMaxHeight = 160;
    private LastPrompt? _lastPrompt;
    private bool _promptExpanded;
    private string? _lastTranscript;
    private string? _archiveRoot;
    private int _resumeGeneration;

    public event Action<TerminalPaneControl>? CloseRequested;
    public event Action<TerminalPaneControl, int>? MoveRequested;
    /// <summary>Raised when the user asks to browse for a folder to launch in. The host shows the picker and calls <see cref="SetCustomFolder"/>.</summary>
    public event Action<TerminalPaneControl>? FolderPickRequested;
    /// <summary>Raised when the user wants to add or remove SSH connections (also when SSH is pressed with none saved).</summary>
    public event Action? SshManageRequested;
    /// <summary>Raised when the user wants this pane's session history. See <see cref="HistoryFolder"/> and <see cref="TranscriptPath"/>.</summary>
    public event Action<TerminalPaneControl>? HistoryRequested;
    /// <summary>Raised by the header's Repo context button. See <see cref="ContextFolder"/> and <see cref="ContextCli"/>.</summary>
    public event Action<TerminalPaneControl>? RepoContextRequested;
    /// <summary>Raised by the header's 🌳 Timeline button. See <see cref="HistoryFolder"/>.</summary>
    public event Action<TerminalPaneControl>? TimelineRequested;
    private List<SshConnection> _sshConnections = [];
    private List<RepositoryDefinition> _repositories = [];
    private LaunchLocation? _customLocation;
    private IReadOnlyList<SessionFolder> _foundFolders = [];
    private bool _showAllFound;
    private const int FoundShownByDefault = 8;
    /// <summary>Raised once when the coding CLI in this pane stops working and waits for the user.</summary>
    public event Action<TerminalPaneControl>? AttentionRequested;
    private int _paneIndex = 1;

    public EmbeddedTerminalControl TerminalControl => Terminal;
    public int PaneIndex => _paneIndex;
    public bool IsRunning => Terminal.IsRunning;
    public event Action? SessionStateChanged;

    public TerminalPaneControl()
    {
        InitializeComponent();
        RunTree.CloseRequested += () => SetRunTreeOpen(false);
        RefreshContextFolder();
        ApplyHeaderMode(working: false);
        Terminal.SessionStarted += () =>
        {
            _lastTranscript = Terminal.HostedRecord?.TranscriptPath;
            // A reattached session's transcript was created when its process started, not now.
            _context.Start(Terminal.CommandLine, Terminal.WorkingDirectory, Terminal.StartedAt, _claudeContextWindowTokens);
            BeginSession();
            SessionStateChanged?.Invoke();
        };
        Terminal.SessionExited += () =>
        {
            _context.Stop();
            RefreshContextFolder();
            SessionStateChanged?.Invoke();
        };
        Terminal.ReattachFailed += OfferResume;
        _context.ReadingChanged += ShowContext;
        _context.PromptChanged += ShowPrompt;
        Terminal.StatusChanged += OnTerminalStatusChanged;
        Terminal.SubtitleChanged += subtitle => SubtitleText.Text = subtitle;
        Terminal.AttentionRequested += () =>
        {
            ShowAttention();
            AttentionRequested?.Invoke(this);
        };
        Terminal.AttentionCleared += ClearAttention;
        Terminal.WorkingStateChanged += working =>
        {
            if (working) StartWorkingPulse();
            else StopWorkingPulse();
        };
    }

    // Gentle blue breathing on the border and header while the agent is producing output.
    private void StartWorkingPulse()
    {
        if (_workingBorderBrush is not null) return;

        var baseBorder = ((SolidColorBrush)FindResource("BorderBrush")).Color;
        _workingBorderBrush = new SolidColorBrush(baseBorder);
        _workingHeaderBrush = new SolidColorBrush(HeaderBaseColor);
        PaneBorder.BorderBrush = _workingBorderBrush;
        PaneHeader.Background = _workingHeaderBrush;

        _workingBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildBreath(baseBorder, WorkingColor));
        _workingHeaderBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildBreath(HeaderBaseColor, WorkingHeaderColor));
    }

    private void StopWorkingPulse()
    {
        if (_workingBorderBrush is null) return;

        _workingBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _workingHeaderBrush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _workingBorderBrush = null;
        _workingHeaderBrush = null;
        PaneBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        PaneHeader.Background = new SolidColorBrush(HeaderBaseColor);
    }

    private static ColorAnimation BuildBreath(Color from, Color to) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromSeconds(1.6),
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
    };

    /// <summary>Applies the user's attention settings (flash on/off and idle threshold in seconds).</summary>
    public void ConfigureAttention(bool enabled, double idleSeconds)
    {
        Terminal.AttentionEnabled = enabled;
        Terminal.AttentionIdleSeconds = idleSeconds;
    }

    /// <summary>Window size assumed for Claude Code sessions, which do not record it. Applies from the next launch.</summary>
    public void ConfigureContext(long claudeContextWindowTokens) => _claudeContextWindowTokens = claudeContextWindowTokens;

    // ---- The orchestrator attached to this pane ----

    /// <summary>What the orchestrator box shows; null hides it. Only a pane with an orchestrator attached gets one.</summary>
    public OrchestratorBoxView? OrchestratorView { get; private set; }

    /// <summary>🧭 Orchestrate pressed in the header: the owner wants an orchestrator put on this running session.</summary>
    public event Action<TerminalPaneControl, Button>? OrchestratorAttachRequested;
    /// <summary>✕ Detach pressed in an attached orchestrator's box.</summary>
    public event Action<TerminalPaneControl>? OrchestratorDetachRequested;
    /// <summary>The owner sent a message to the attached orchestrator.</summary>
    public event Action<TerminalPaneControl, string>? OrchestratorMessageSent;
    /// <summary>The owner set the attached orchestrator's handoff limit (a fraction of the context window) in its box.</summary>
    public event Action<TerminalPaneControl, double>? OrchestratorThresholdChanged;
    /// <summary>🗺 Run pressed in the box of an orchestrator that follows an advanced template: the owner wants its tree.</summary>
    public event Action<TerminalPaneControl>? OrchestratorRunRequested;

    private void OrchestratorRun_Click(object sender, RoutedEventArgs e) => OrchestratorRunRequested?.Invoke(this);

    /// <summary>The owner wants the run tree beside the session; kept for the pane's next run while AgentHub runs. Hidden at first.</summary>
    private bool _runTreeOpen;

    private void OrchestratorTree_Click(object sender, RoutedEventArgs e) => SetRunTreeOpen(!_runTreeOpen);

    private void SetRunTreeOpen(bool open)
    {
        _runTreeOpen = open;
        ShowRunTree(OrchestratorView);
    }

    // ◨ Tree in the box: the box's own button colours while the tree is hidden, lit while it shows.
    private static readonly SolidColorBrush TreeBtnOff = Frozen(0x22, 0x1A, 0x3D);
    private static readonly SolidColorBrush TreeBtnOffText = Frozen(0xC4, 0xB5, 0xFD);
    private static readonly SolidColorBrush TreeBtnOn = Frozen(0x4C, 0x3A, 0x8C);
    private static readonly SolidColorBrush TreeBtnOnText = Frozen(0xED, 0xE9, 0xFE);

    /// <summary>The run tree beside the session: only for an orchestrator that follows an advanced template, and only when asked for.</summary>
    private void ShowRunTree(OrchestratorBoxView? view)
    {
        var run = view?.Run;
        OrchestratorTreeBtn.Visibility = run is null ? Visibility.Collapsed : Visibility.Visible;
        OrchestratorTreeBtn.Background = _runTreeOpen ? TreeBtnOn : TreeBtnOff;
        OrchestratorTreeBtn.Foreground = _runTreeOpen ? TreeBtnOnText : TreeBtnOffText;
        System.Windows.Automation.AutomationProperties.SetName(OrchestratorTreeBtn, _runTreeOpen ? "Hide the run tree" : "Show the run tree");
        if (run is null || !_runTreeOpen)
        {
            RunTree.Visibility = Visibility.Collapsed;
            return;
        }
        RunTree.Show(run);
        RunTree.Visibility = Visibility.Visible;
    }

    public void ShowOrchestrator(OrchestratorBoxView? view)
    {
        var wasAttached = OrchestratorView is not null;
        OrchestratorView = view;
        ShowRunTree(view);
        if (view is null)
        {
            OrchestratorBox.Visibility = Visibility.Collapsed;
            RefreshOrchestrateButton();
            return;
        }
        OrchestratorStateText.Text = view.State;
        OrchestratorTitle.Text = view.Title;
        OrchestratorRunBtn.Visibility = view.HasRun ? Visibility.Visible : Visibility.Collapsed;
        OrchestratorProgress.Text = view.Note;
        OrchestratorProgress.ToolTip = string.IsNullOrEmpty(view.Note) ? null : view.Note;
        OrchestratorProgress.Visibility = string.IsNullOrEmpty(view.Note) ? Visibility.Collapsed : Visibility.Visible;
        ShowConversation(view.Conversation);
        ShowOrchestratorContext(view.OrchestratorContext);
        OrchestratorContextUsed.Text = view.ContextUsed is { } used ? "context " + used + " ·" : "";
        // Not while the owner is typing a new value into it.
        if (!OrchestratorThresholdBox.IsKeyboardFocused) OrchestratorThresholdBox.Text = $"{view.HandoffThreshold * 100:0}";
        OrchestratorBox.Visibility = Visibility.Visible;
        RefreshOrchestrateButton();
        // Just attached: the owner's next keystrokes are meant for the orchestrator.
        if (!wasAttached) Dispatcher.BeginInvoke(() => OrchestratorInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OrchestratorThreshold_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; ApplyOrchestratorThreshold(); OrchestratorInput.Focus(); }
        else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; ShowOrchestratorThreshold(); OrchestratorInput.Focus(); }
    }

    private void OrchestratorThreshold_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => ApplyOrchestratorThreshold();

    /// <summary>Reads the field as a per cent (or a fraction), clamps it to the orchestrator's range and raises it; anything else restores the shown value.</summary>
    private void ApplyOrchestratorThreshold()
    {
        if (OrchestratorView is not { HandoffThreshold: var current }) return;
        var text = OrchestratorThresholdBox.Text.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            ShowOrchestratorThreshold();
            return;
        }
        var fraction = Math.Clamp(value > 1 ? value / 100 : value, PaneOrchestrator.MinContextThreshold, PaneOrchestrator.MaxContextThreshold);
        OrchestratorThresholdBox.Text = $"{fraction * 100:0}";
        if (Math.Abs(fraction - current) < 0.0005) return;
        OrchestratorThresholdChanged?.Invoke(this, fraction);
    }

    private void ShowOrchestratorThreshold()
    {
        if (OrchestratorView is { HandoffThreshold: var t }) OrchestratorThresholdBox.Text = $"{t * 100:0}";
    }

    /// <summary>The orchestrator's own context bar in its box, styled as the pane's badge; hidden until it has decided once.</summary>
    private void ShowOrchestratorContext(ContextReading? reading)
    {
        if (reading is null)
        {
            OrchestratorContextBadge.Visibility = Visibility.Collapsed;
            return;
        }
        var fraction = reading.UsedFraction;
        OrchestratorContextFill.Width = ContextBarWidth * fraction;
        OrchestratorContextFill.Background = fraction switch
        {
            >= 0.85 => ContextFullBrush,
            >= 0.6 => ContextWarnBrush,
            _ => ContextOkBrush
        };
        OrchestratorContextText.Text = $"{FormatTokens(reading.UsedTokens)} / {FormatTokens(reading.WindowTokens)} · {fraction:P0}";
        OrchestratorContextBadge.ToolTip =
            $"The orchestrator's own context window: {reading.UsedTokens:N0} tokens in its last decision\n" +
            $"Window {reading.WindowTokens:N0} tokens is assumed (claudeContextWindowTokens in settings; Claude Code does not record it).\n" +
            (reading.Model is null ? "" : $"Model: {reading.Model}\n") +
            $"As of {reading.RecordedAt.ToLocalTime():HH:mm:ss}\n{reading.FilePath}";
        OrchestratorContextBadge.Visibility = Visibility.Visible;
    }

    private bool _sessionRunning;

    /// <summary>🧭 Orchestrate is offered on a running session that has no orchestrator yet.</summary>
    private void RefreshOrchestrateButton() =>
        OrchestrateBtn.Visibility = _sessionRunning && OrchestratorView is null ? Visibility.Visible : Visibility.Collapsed;

    private void Orchestrate_Click(object sender, RoutedEventArgs e) => OrchestratorAttachRequested?.Invoke(this, OrchestrateBtn);
    private void OrchestratorDetach_Click(object sender, RoutedEventArgs e) => OrchestratorDetachRequested?.Invoke(this);
    private void OrchestratorSend_Click(object sender, RoutedEventArgs e) => SendOrchestratorInput();

    private void OrchestratorInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        SendOrchestratorInput();
    }

    private void SendOrchestratorInput()
    {
        var text = OrchestratorInput.Text.Trim();
        if (text.Length == 0) return;
        OrchestratorInput.Clear();
        OrchestratorMessageSent?.Invoke(this, text);
        OrchestratorInput.Focus();
    }

    private readonly ObservableCollection<OrchestratorExchange> _conversation = [];

    /// <summary>Shows the run's conversation; follows the newest message unless the owner scrolled up.</summary>
    private void ShowConversation(IReadOnlyList<OrchestratorExchange> next)
    {
        if (OrchestratorConversation.ItemsSource is null) OrchestratorConversation.ItemsSource = _conversation;
        var scroll = OrchestratorConversationScroll;
        var following = scroll.VerticalOffset >= scroll.ScrollableHeight - 8
            || _conversation.Count > 0 && (next.Count == 0 || !ReferenceEquals(_conversation[0], next[0])); // a new session starts at its end
        var changed = OrchestratorBoxView.Sync(_conversation, next);

        OrchestratorNothingYet.Text = OrchestratorBoxView.NothingYet;
        OrchestratorNothingYet.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changed && following)
            Dispatcher.BeginInvoke(scroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public ContextReading? LatestContextReading { get; private set; }
    public event Action<ContextReading?>? ContextReadingChanged;

    /// <summary>Sends text input directly into this pane's running session.</summary>
    public void SendInput(string data) => Terminal.SendInput(data);

    /// <summary>Types a prompt into this pane's agent CLI and presses Enter separately so it submits.</summary>
    public Task SubmitPromptAsync(string prompt) => Terminal.SubmitPromptAsync(prompt);

    private void ShowContext(ContextReading? reading)
    {
        LatestContextReading = reading;
        ContextReadingChanged?.Invoke(reading);
        if (reading is null)
        {
            ContextBadge.Visibility = Visibility.Collapsed;
            return;
        }

        _lastTranscript = reading.FilePath;
        Terminal.NoteTranscript(reading.FilePath);
        var fraction = reading.UsedFraction;
        ContextBarFill.Width = ContextBarWidth * fraction;
        ContextBarFill.Background = fraction switch
        {
            >= 0.85 => ContextFullBrush,
            >= 0.6 => ContextWarnBrush,
            _ => ContextOkBrush
        };
        ContextText.Text = reading.CompactedSinceLastTurn
            ? "compacted"
            : $"{FormatTokens(reading.UsedTokens)} / {FormatTokens(reading.WindowTokens)} · {fraction:P0}";

        var window = reading.WindowIsAssumed
            ? $"Window {reading.WindowTokens:N0} tokens is assumed (the CLI does not record it). Change it with claudeContextWindowTokens in settings."
            : $"Window {reading.WindowTokens:N0} tokens, as recorded by the CLI.";
        var compacted = reading.CompactedSinceLastTurn
            ? "\nConversation compacted; size updates after the next reply."
            : "";
        ContextBadge.ToolTip =
            $"Context window: {reading.UsedTokens:N0} tokens in the last request{compacted}\n" +
            $"{window}\n" +
            (reading.Model is null ? "" : $"Model: {reading.Model}\n") +
            $"As of {reading.RecordedAt.ToLocalTime():HH:mm:ss}\n" +
            $"Source: {reading.Source}\n{reading.FilePath}";
        ContextBadge.Visibility = Visibility.Visible;
    }

    private void ShowPrompt(LastPrompt? prompt)
    {
        if (prompt is null)
        {
            _lastPrompt = null;
            PromptStrip.Visibility = Visibility.Collapsed;
            return;
        }

        if (_lastPrompt?.Text != prompt.Text) _promptExpanded = false;
        _lastPrompt = prompt;
        PromptLabel.ToolTip = prompt.SentAt is { } sent ? $"Sent {sent.ToLocalTime():HH:mm:ss}" : null;
        ApplyPromptLayout();
        PromptStrip.Visibility = Visibility.Visible;
    }

    // Collapsed: one line, line breaks shown as ↵. Expanded: full text, wrapped, scrolling past a few lines.
    private void ApplyPromptLayout()
    {
        if (_lastPrompt is null) return;
        var text = _lastPrompt.Text;
        if (_promptExpanded)
        {
            PromptText.Text = text;
            PromptText.TextWrapping = TextWrapping.Wrap;
            PromptText.TextTrimming = TextTrimming.None;
            PromptScroller.MaxHeight = PromptExpandedMaxHeight;
            PromptScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            PromptScroller.ToolTip = "Click to collapse";
        }
        else
        {
            PromptText.Text = string.Join(" ↵ ", text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            PromptText.TextWrapping = TextWrapping.NoWrap;
            PromptText.TextTrimming = TextTrimming.CharacterEllipsis;
            PromptScroller.MaxHeight = PromptCollapsedMaxHeight;
            PromptScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            PromptScroller.ScrollToTop();
            PromptScroller.ToolTip = "Click to show the full prompt";
        }
    }

    private void PromptText_Click(object sender, MouseButtonEventArgs e)
    {
        _promptExpanded = !_promptExpanded;
        ApplyPromptLayout();
    }

    private void PromptCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPrompt is null) return;
        try
        {
            System.Windows.Clipboard.SetText(_lastPrompt.Text);
            PromptCopyBtn.Content = "Copied";
            var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += (_, _) => { reset.Stop(); PromptCopyBtn.Content = "Copy"; };
            reset.Start();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard held open by another process; the user can press Copy again.
        }
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
        >= 1_000 => $"{tokens / 1_000d:0}k",
        _ => tokens.ToString()
    };

    // Three amber pulses on the pane border and header, then a persistent badge until the user types.
    private void ShowAttention()
    {
        StopWorkingPulse();
        AttentionBadge.Visibility = Visibility.Visible;

        var baseBorder = ((SolidColorBrush)FindResource("BorderBrush")).Color;
        var borderBrush = new SolidColorBrush(baseBorder);
        var headerBrush = new SolidColorBrush(HeaderBaseColor);
        PaneBorder.BorderBrush = borderBrush;
        PaneHeader.Background = headerBrush;

        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildPulse(baseBorder, AttentionColor, restore: () =>
        {
            PaneBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        }));
        headerBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildPulse(HeaderBaseColor, AttentionHeaderColor, restore: null));
    }

    private static ColorAnimationUsingKeyFrames BuildPulse(Color from, Color to, Action? restore)
    {
        const int pulses = 3;
        const double pulseSeconds = 0.8;
        var anim = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(pulses * pulseSeconds),
            FillBehavior = FillBehavior.Stop
        };
        for (var i = 0; i < pulses; i++)
        {
            anim.KeyFrames.Add(new EasingColorKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * pulseSeconds + pulseSeconds / 2))));
            anim.KeyFrames.Add(new EasingColorKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds((i + 1) * pulseSeconds))));
        }
        if (restore is not null)
        {
            anim.Completed += (_, _) => restore();
        }
        return anim;
    }

    private void ClearAttention()
    {
        AttentionBadge.Visibility = Visibility.Collapsed;
    }

    public void SetIndex(int index, SolidColorBrush accent)
    {
        // The "Terminal N" label was removed from the header; only the internal index is tracked now.
        _paneIndex = index;
        _ = accent;
    }

    /// <summary>Folder a session starts in when no repository or folder has been chosen.</summary>
    public static string BaseDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Launcher state: the chosen location and CLI, the open step (1 repository, 2 CLI, 3 session),
    // whether the launcher covers the terminal, and whether the terminal holds an earlier session's output.
    private LaunchLocation? _location;
    private ShellOption? _shell;
    private List<ShellOption> _shells = [];
    private int _step = 1;
    private bool _launcherOpen = true;
    private bool _hadSession;
    private const int LocationFilterThreshold = 8;

    private static readonly SolidColorBrush StepActiveBorder = Frozen(0x0E, 0x74, 0x90);
    private static readonly SolidColorBrush StepIdleBorder = Frozen(0x1E, 0x2C, 0x40);
    private static readonly SolidColorBrush StepBadgeActive = Frozen(0x0E, 0x74, 0x90);
    private static readonly SolidColorBrush StepBadgeDone = Frozen(0x14, 0x53, 0x2D);
    private static readonly SolidColorBrush StepBadgeIdle = Frozen(0x16, 0x20, 0x2F);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>The folder a new session starts in.</summary>
    public string SelectedFolder => _location?.Path ?? BaseDirectory;

    public void UpdateRepositories(List<RepositoryDefinition> repos)
    {
        _repositories = repos;
        if (_location?.Repository is { } chosen)
        {
            var still = repos.FirstOrDefault(r => r.Id == chosen.Id);
            _location = still is null ? null : ToLocation(still);
            if (_location is null && !Terminal.IsRunning) _step = 1;
        }
        RefreshLauncher();
    }

    private static LaunchLocation ToLocation(RepositoryDefinition repo) => new() { Name = repo.Name, Path = repo.LocalPath, Repository = repo };

    /// <summary>Adds (or replaces) the browsed-folder entry and chooses it as the launcher's location.</summary>
    public void SetCustomFolder(string folder)
    {
        _customLocation = CustomLocation(folder);
        ChooseLocation(_customLocation);
    }

    private static LaunchLocation CustomLocation(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new LaunchLocation { Name = "📁 " + (string.IsNullOrWhiteSpace(name) ? folder : name), Path = folder };
    }

    // Order: home, the browsed folder if any, then registered repositories by name.
    private List<LaunchLocation> Locations()
    {
        var items = new List<LaunchLocation> { new() { Name = "🏠 Home", Path = BaseDirectory } };
        if (_customLocation is not null) items.Add(_customLocation);
        items.AddRange(_repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(ToLocation));
        return items;
    }

    /// <summary>
    /// Folders with past Claude Code or Codex sessions on this computer (see <see cref="SessionFolderDiscovery"/>), newest
    /// first, offered under the repositories so their sessions can be continued without registering the folder first.
    /// </summary>
    public void UpdateFoundFolders(IReadOnlyList<SessionFolder> folders)
    {
        _foundFolders = folders;
        if (_launcherOpen && _step == 1) RefreshLauncher();
    }

    // Found folders not already listed above, that still exist (a session cannot start in a deleted folder).
    private List<LaunchLocation> FoundLocations(List<LaunchLocation> listed) =>
        _foundFolders
            .Where(f => f.Exists && !listed.Any(l => SamePath(l.Path, f.Path)))
            .Select(LaunchLocation.FromFound)
            .ToList();

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsHome(LaunchLocation? location) => location is { Repository: null } && SamePath(location.Path, BaseDirectory);

    /// <summary>Function-type row that opens the SSH connection manager.</summary>
    private static readonly object ManageSshItem = new();

    private static bool HasSessions(ShellOption shell) => SessionContextReader.DetectSource(shell.Command) != ContextSource.None;

    /// <summary>Folder whose instruction files Repo context shows: where the running session is, else the chosen location.</summary>
    public string ContextFolder => Terminal.IsRunning && !string.IsNullOrEmpty(Terminal.WorkingDirectory) ? Terminal.WorkingDirectory : SelectedFolder;

    /// <summary>CLI whose file the context windows open on: the running session's, else the chosen one's.</summary>
    public InstructionCli? ContextCli =>
        (Terminal.IsRunning ? InstructionFiles.DetectCli(Terminal.CommandLine) : null)
        ?? InstructionFiles.DetectCli(_shell?.Command);

    private void RefreshContextFolder()
    {
        var folder = ContextFolder;
        RepoContextBtn.ToolTip = $"View and edit the instruction files in {folder}: CLAUDE.md, AGENTS.md and GEMINI.md";
    }

    private void RepoContext_Click(object sender, RoutedEventArgs e) => RepoContextRequested?.Invoke(this);

    public void UpdateShellOptions(List<ShellOption> shells)
    {
        _shells = shells;
        if (_shell is not null)
        {
            _shell = shells.FirstOrDefault(s => s.DisplayName == _shell.DisplayName);
            if (_shell is null && _step == 3) _step = 2;
        }
        RefreshLauncher();
    }

    // Reflects the embedded terminal's session state onto the shared header (dot colour + Stop/Restart buttons).
    private void OnTerminalStatusChanged(TerminalStatus status)
    {
        switch (status)
        {
            case TerminalStatus.Running:
                StatusDot.Fill = RunningBrush;
                RestartBtn.Visibility = Visibility.Collapsed;
                StopBtn.Visibility = Visibility.Visible;
                break;
            case TerminalStatus.Stopped:
                StatusDot.Fill = StoppedBrush;
                RestartBtn.Visibility = Visibility.Visible;
                StopBtn.Visibility = Visibility.Collapsed;
                break;
            case TerminalStatus.Failed:
                StatusDot.Fill = FailedBrush;
                RestartBtn.Visibility = Visibility.Visible;
                StopBtn.Visibility = Visibility.Collapsed;
                break;
            default:
                StatusDot.Fill = IdleBrush;
                RestartBtn.Visibility = Visibility.Collapsed;
                StopBtn.Visibility = Visibility.Collapsed;
                break;
        }
        ApplyHeaderMode(status == TerminalStatus.Running);
        _sessionRunning = status == TerminalStatus.Running;
        RefreshOrchestrateButton();
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e) => Terminal.StopSession();

    private void RestartBtn_Click(object sender, RoutedEventArgs e)
    {
        CloseLauncher();
        Terminal.RestartSession();
    }

    /// <summary>AgentHub's transcript archive, so sessions the CLI has deleted can still be continued. Null when archiving is off.</summary>
    public void ConfigureHistory(string? archiveRoot) => _archiveRoot = archiveRoot;

    private const int ResumeListLimit = 30;

    // ---- Launcher: repository, then CLI, then session ----

    /// <summary>
    /// Shows the launcher over the terminal while it is open and nothing runs. The terminal is hidden, not collapsed,
    /// so it keeps its size and a CLI started from the launcher reads the final terminal size.
    /// </summary>
    private void RefreshLauncher()
    {
        var show = _launcherOpen && !Terminal.IsRunning;
        LaunchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Terminal.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        LaunchBtn.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        BackToOutputBtn.Visibility = _hadSession ? Visibility.Visible : Visibility.Collapsed;

        var all = Locations();
        var found = FoundLocations(all);
        var filter = LocationFilter.Text.Trim();
        bool Matches(LaunchLocation l) => filter.Length == 0 ||
            l.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || l.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var shown = all.Where(Matches).ToList();
        var foundShown = found.Where(Matches).ToList();
        LocationFilter.Visibility = all.Count + found.Count > LocationFilterThreshold ? Visibility.Visible : Visibility.Collapsed;
        LocationList.ItemsSource = shown.Select(l => new PickRow(l, l.Name, l.Path, SamePath(l.Path, _location?.Path))).ToList();
        LocationEmpty.Visibility = shown.Count + foundShown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Past-session folders: the newest few unless asked for all; a filter searches them all.
        var foundLimited = filter.Length == 0 && !_showAllFound && foundShown.Count > FoundShownByDefault;
        FoundSection.Visibility = foundShown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FoundList.ItemsSource = (foundLimited ? foundShown.Take(FoundShownByDefault) : foundShown)
            .Select(l => new PickRow(l, l.Name, l.Detail, SamePath(l.Path, _location?.Path))).ToList();
        FoundMoreBtn.Visibility = foundLimited ? Visibility.Visible : Visibility.Collapsed;
        FoundMoreText.Text = $"Show all {foundShown.Count} folders…";
        var functions = _shells.Select(s => new PickRow(s, s.DisplayName, s.Command, s.DisplayName == _shell?.DisplayName)).ToList();
        // SSH runs from the home folder, so it is offered only there.
        if (IsHome(_location))
        {
            functions.AddRange(_sshConnections.Select(c => new PickRow(c, $"SSH · {c.DisplayName}", $"ssh {c.Target}", false)));
            functions.Add(new PickRow(ManageSshItem, "⚙ Manage SSH connections…",
                _sshConnections.Count == 0 ? "Add a connection to SSH from here" : "Add or remove saved connections", false));
        }
        ShellList.ItemsSource = functions;

        ApplyStep(1, Step1Card, Step1Badge, Step1Number, Step1Value, Step1Change, Step1Body, _location?.Name);
        ApplyStep(2, Step2Card, Step2Badge, Step2Number, Step2Value, Step2Change, ShellList, _shell?.DisplayName);
        ApplyStep(3, Step3Card, Step3Badge, null, null, null, Step3Body, null);

        RefreshTitle();
        RefreshContextFolder();
    }

    // Active: list open, accent border. Done: tick, the choice, and Change. Pending: dimmed.
    private void ApplyStep(int step, Border card, Border badge, TextBlock? number, TextBlock? value, Button? change, UIElement body, string? chosen)
    {
        var active = _step == step;
        var done = _step > step;
        body.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        card.Opacity = _step < step ? 0.5 : 1;
        card.BorderBrush = active ? StepActiveBorder : StepIdleBorder;
        badge.Background = active ? StepBadgeActive : done ? StepBadgeDone : StepBadgeIdle;
        if (number is not null) number.Text = done ? "✓" : step.ToString();
        if (value is not null) value.Text = done ? chosen ?? "" : "";
        if (change is not null) change.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GoToStep(int step)
    {
        _step = step;
        RefreshLauncher();
        if (step == 3) _ = LoadSessionsAsync();
    }

    // The step after a location: straight to sessions when a CLI with sessions is already chosen, else pick a CLI.
    private void ChooseLocation(LaunchLocation location)
    {
        if (!Directory.Exists(location.Path))
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), $"The folder no longer exists:\n{location.Path}", "AgentHub",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _location = location;
        LocationFilter.Text = "";
        GoToStep(_shell is not null && HasSessions(_shell) ? 3 : 2);
    }

    // CLIs that record sessions (Claude Code, Codex) go on to pick one. Anything else starts straight away.
    private void ChooseShell(ShellOption shell)
    {
        _shell = shell;
        if (HasSessions(shell)) GoToStep(3);
        else LaunchCommand(shell.Command, shell.DisplayName, shell.IsAgent);
    }

    private void PickRow_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag)
        {
            // A found folder becomes this pane's browsed folder, so it stays chosen and the saved layout brings it back.
            case PickRow { Item: LaunchLocation { Found: not null } found }:
                SetCustomFolder(found.Path);
                break;
            case PickRow { Item: LaunchLocation location }:
                ChooseLocation(location);
                break;
            case PickRow { Item: ShellOption shell }:
                ChooseShell(shell);
                break;
            case PickRow { Item: SshConnection connection }:
                ConnectSsh(connection);
                break;
            case PickRow { Item: var item } when ReferenceEquals(item, ManageSshItem):
                SshManageRequested?.Invoke();
                break;
        }
    }

    private void LocationFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshLauncher();

    private void FoundMore_Click(object sender, RoutedEventArgs e)
    {
        _showAllFound = true;
        RefreshLauncher();
    }

    private void Step1Change_Click(object sender, RoutedEventArgs e)
    {
        GoToStep(1);
        if (LocationFilter.IsVisible) LocationFilter.Focus();
    }

    private void Step2Change_Click(object sender, RoutedEventArgs e) => GoToStep(2);

    private void BackToOutput_Click(object sender, RoutedEventArgs e) => CloseLauncher();

    private void CloseLauncher()
    {
        _launcherOpen = false;
        RefreshLauncher();
    }

    // Reopens the launcher at the first step still to choose; a CLI with sessions reopens on its session list.
    private void OpenLauncher()
    {
        _launcherOpen = true;
        GoToStep(_location is null ? 1 : _shell is null || !HasSessions(_shell) ? 2 : 3);
    }

    private async Task LoadSessionsAsync()
    {
        if (_shell is null) return;
        var source = SessionContextReader.DetectSource(_shell.Command);
        var dir = SelectedFolder;
        var generation = ++_resumeGeneration;
        ResumeList.ItemsSource = null;
        ResumeStatus.Text = "Looking for earlier sessions…";
        NewSessionBtn.Focus();

        var archiveRoot = _archiveRoot;
        var sessions = await Task.Run(() => SessionHistoryReader.ListSessions(dir, archiveRoot: archiveRoot)
            .Where(s => s.Agent == source)
            .ToList());
        if (generation != _resumeGeneration || _step != 3) return;

        ResumeList.ItemsSource = sessions.Take(ResumeListLimit).Select(s => new SessionRow(s)).ToList();
        var lastHere = _resumeHint is { } hint
            ? sessions.FindIndex(s => string.Equals(s.FilePath, hint, StringComparison.OrdinalIgnoreCase))
            : -1;
        ResumeStatus.Text = sessions.Count switch
        {
            0 => "No earlier sessions in this folder.",
            _ when lastHere >= 0 => $"This pane's last session is {Ordinal(lastHere + 1)} in the list. Continue it, or another:",
            > ResumeListLimit => $"Or continue an earlier one (newest {ResumeListLimit} of {sessions.Count}; all are in 🕘 History):",
            _ => "Or continue an earlier one:"
        };
    }

    private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", _ => $"number {n}" };

    // Transcript of the session this pane had before AgentHub last closed, pointed out in the resume list.
    private string? _resumeHint;

    // Running: Relaunch. Otherwise the header button reopens the launcher over the last session's output.
    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Terminal.IsRunning) Relaunch();
        else OpenLauncher();
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_shell is { } shell) LaunchCommand(shell.Command, shell.DisplayName, shell.IsAgent);
    }

    private void ResumeItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionRow row || _shell is not { } shell) return;

        if (!SessionResume.RestoreFromArchive(row.Summary, _archiveRoot))
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this),
                $"Could not restore this session from the archive so {shell.DisplayName} can resume it:\n{row.FilePath}",
                "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LaunchCommand(SessionResume.BuildCommand(shell.Command, row.Summary), $"{shell.DisplayName} · resumed", shell.IsAgent,
            $"continue “{row.Title}”", new FreshLaunch(shell.Command, $"Terminal {_paneIndex} · {shell.DisplayName}", shell.IsAgent));
    }

    private void LaunchCommand(string command, string title, bool isAgent, string? what = null, FreshLaunch? fresh = null)
    {
        var dir = SelectedFolder;
        var repoName = _location?.Repository?.Name ?? _location?.Name ?? "Home";
        if (!Directory.Exists(dir))
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), $"The folder no longer exists:\n{dir}", "AgentHub",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Terminal.IsRunning)
        {
            var replace = System.Windows.MessageBox.Show(Window.GetWindow(this),
                $"This terminal has a running session. Replace it and {what ?? $"start {title}"} in {dir}?",
                "Replace session", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return;
        }

        var fullTitle = $"Terminal {_paneIndex} · {title}";
        _fresh = fresh ?? new FreshLaunch(command, fullTitle, isAgent);
        BeginSession();
        Terminal.StartSession(command, dir, fullTitle, repoName, isAgent);
    }

    // Uncovers the terminal before the process starts, so nothing on screen changes once the CLI draws.
    private void BeginSession()
    {
        _hadSession = true;
        CloseLauncher();
    }

    /// <summary>What Relaunch starts: the CLI's plain command (never a resume of the old session), its title, and whether it is an agent.</summary>
    private sealed record FreshLaunch(string Command, string Title, bool IsAgent);
    private FreshLaunch? _fresh;

    // Title row: the running session's repository or folder, else the location chosen in the launcher.
    private void RefreshTitle()
    {
        if (Terminal.IsRunning)
        {
            SessionTitle.Text = Terminal.SessionName;
            SessionTitle.ToolTip = Terminal.WorkingDirectory;
        }
        else
        {
            SessionTitle.Text = _location?.Repository?.Name ?? _location?.Name ?? "New terminal";
            SessionTitle.ToolTip = _location?.Path;
        }
    }

    // Running session: start a new session of the same CLI in the same folder, after confirming.
    private void Relaunch()
    {
        var dir = Terminal.WorkingDirectory;
        var fresh = _fresh ?? new FreshLaunch(Terminal.CommandLine, $"Terminal {_paneIndex}", Terminal.IsAgentSession);
        var name = string.IsNullOrEmpty(Terminal.SessionName) ? dir : Terminal.SessionName;
        var replace = System.Windows.MessageBox.Show(Window.GetWindow(this),
            $"End the running session and start a new one in {name}?\n{dir}",
            "Relaunch session", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (replace != MessageBoxResult.Yes) return;
        Terminal.StartSession(fresh.Command, dir, fresh.Title, Terminal.SessionName, fresh.IsAgent);
    }

    /// <summary>
    /// No session: the launcher fills the pane; the header keeps Repo context, history, move and close
    /// (plus New session over an ended session's output). Session running: the repository name, context badge,
    /// 📂 to open its folder, Relaunch, Repo context, move and close.
    /// </summary>
    private void ApplyHeaderMode(bool working)
    {
        var setup = working ? Visibility.Collapsed : Visibility.Visible;
        BrowseBtn.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        HistoryBtn.Visibility = setup;
        if (working)
        {
            StopBtn.Visibility = Visibility.Collapsed;
            RestartBtn.Visibility = Visibility.Collapsed;
        }

        LaunchBtn.Content = working ? "Relaunch" : "New session";
        LaunchBtn.ToolTip = working
            ? "End this session and start a new one of the same CLI in the same folder"
            : "Choose a repository, CLI and session to start";
        BrowseBtn.ToolTip = $"Open {Terminal.WorkingDirectory} in File Explorer";
        RefreshLauncher();
    }

    /// <summary>Saved SSH connections, offered as function types when the launcher's location is Home.</summary>
    public void UpdateSshConnections(List<SshConnection> connections)
    {
        _sshConnections = connections;
        RefreshLauncher();
    }

    private void ConnectSsh(SshConnection connection)
    {
        if (Terminal.IsRunning)
        {
            var replace = System.Windows.MessageBox.Show(Window.GetWindow(this),
                $"This terminal has a running session. Replace it with SSH to {connection.DisplayName}?",
                "Replace session", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return;
        }

        _shell = null; // reopening the launcher lands on function type again
        _fresh = new FreshLaunch(connection.BuildCommandLine(), $"Terminal {_paneIndex} · SSH", false);
        BeginSession();
        Terminal.StartSession(_fresh.Command, BaseDirectory, _fresh.Title, $"SSH · {connection.DisplayName}");
    }

    // Launcher: pick any folder to launch in. Session running: show its folder in File Explorer.
    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Terminal.IsRunning && Directory.Exists(Terminal.WorkingDirectory))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Terminal.WorkingDirectory}\""));
        else
            FolderPickRequested?.Invoke(this);
    }

    private void History_Click(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke(this);
    private void Timeline_Click(object sender, RoutedEventArgs e) => TimelineRequested?.Invoke(this);

    /// <summary>Folder whose sessions the history button shows: where the current or last session ran, else the chosen location.</summary>
    public string HistoryFolder => string.IsNullOrEmpty(Terminal.WorkingDirectory) ? SelectedFolder : Terminal.WorkingDirectory;

    /// <summary>Transcript of this pane's current or most recent Claude Code / Codex session, if one has been found.</summary>
    public string? TranscriptPath => _context.BoundPath ?? _lastTranscript;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this);
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, -1);

    private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, 1);

    /// <summary>Starts a session chosen outside the launcher (toolbar folder launch, repository Launch); the launcher follows it.</summary>
    public void StartSession(string commandLine, string workingDirectory, string title, string repoName, bool isAgentSession = false)
    {
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            var match = Locations().FirstOrDefault(l => SamePath(l.Path, workingDirectory));
            if (match is not null) _location = match;
            else if (Directory.Exists(workingDirectory)) _location = _customLocation = CustomLocation(workingDirectory);
        }
        _shell = _shells.FirstOrDefault(s => s.Command == commandLine) ?? _shell;
        _step = _location is null ? 1 : 2;
        _fresh = new FreshLaunch(commandLine, title, isAgentSession);
        BeginSession();
        Terminal.StartSession(commandLine, workingDirectory, title, repoName, isAgentSession);
    }

    // ---- Sessions that outlive AgentHub ----

    /// <summary>Sessions start in host processes through this service; null keeps them inside AgentHub.</summary>
    public void ConfigureHost(SessionHostService? hosts) => Terminal.Host = hosts;

    /// <summary>Picks up a session still running in its host from before AgentHub last closed; the launcher follows it.</summary>
    public void AttachSession(SessionRecord record)
    {
        PlaceLauncher(record.WorkingDirectory, record.CommandLine);
        _fresh = new FreshLaunch(_shell?.Command ?? PlainCommand(record.CommandLine), record.Title, record.IsAgent);
        BeginSession();
        Terminal.AttachSession(record);
    }

    /// <summary>Brings back an idle pane's launcher choices (folder and CLI) from a saved layout.</summary>
    public void RestoreLayout(PaneLayoutEntry entry)
    {
        PlaceLauncher(entry.Folder, entry.Command);
        _resumeHint = entry.TranscriptPath;
        _launcherOpen = true;
        GoToStep(_location is null ? 1 : _shell is null ? 2 : HasSessions(_shell) ? 3 : 2);
    }

    // The saved session is gone (ended while AgentHub was closed, or its host with it): open the launcher on its folder
    // and CLI with the session list up, pointing at the one this pane had. Its last screen stays under "Back to output".
    private void OfferResume(SessionRecord record, string how)
    {
        _resumeHint = record.TranscriptPath ?? _lastTranscript;
        _hadSession = true;
        if (_location is null || _shell is null) PlaceLauncher(record.WorkingDirectory, record.CommandLine);
        _launcherOpen = true;
        GoToStep(_location is null ? 1 : _shell is null ? 2 : HasSessions(_shell) ? 3 : 2);
    }

    // Launcher state for a folder and a command: the matching location (or a browsed one) and the function type whose
    // command the session's command line starts with (a resume command starts with the CLI's plain command).
    private void PlaceLauncher(string? folder, string? command)
    {
        if (!string.IsNullOrEmpty(folder))
        {
            var match = Locations().FirstOrDefault(l => SamePath(l.Path, folder));
            if (match is not null) _location = match;
            else if (Directory.Exists(folder)) _location = _customLocation = CustomLocation(folder);
        }
        if (!string.IsNullOrEmpty(command))
        {
            _shell = _shells.FirstOrDefault(s => s.Command == command)
                ?? _shells.Where(s => command.StartsWith(s.Command + " ", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Command.Length).FirstOrDefault()
                ?? _shell;
        }
        _step = _location is null ? 1 : 2;
    }

    private string PlainCommand(string commandLine) =>
        _shells.Where(s => commandLine.StartsWith(s.Command + " ", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Command.Length).Select(s => s.Command).FirstOrDefault() ?? commandLine;

    /// <summary>This pane as the layout saves it: its hosted session while one runs, and its folder and CLI either way.</summary>
    public PaneLayoutEntry LayoutEntry() => new()
    {
        SessionId = Terminal.IsRunning ? Terminal.HostedRecord?.Id : null,
        Folder = Terminal.IsRunning && !string.IsNullOrEmpty(Terminal.WorkingDirectory) ? Terminal.WorkingDirectory : _location?.Path,
        Command = _fresh?.Command ?? _shell?.Command,
        ShellName = _shell?.DisplayName,
        IsAgent = Terminal.IsRunning ? Terminal.IsAgentSession : _shell?.IsAgent ?? false,
        RepoName = Terminal.IsRunning ? Terminal.SessionName : _location?.Repository?.Name ?? _location?.Name,
        TranscriptPath = TranscriptPath
    };

    /// <summary>True while this pane shows a session in a host, the kind a close lets go of rather than ends.</summary>
    public bool IsHosted => Terminal.IsHosted;

    /// <summary>Saves the terminal as shown for the next attach to put back; see <see cref="EmbeddedTerminalControl.SaveScreenAsync"/>.</summary>
    public Task SaveScreenAsync(TimeSpan timeout) => Terminal.SaveScreenAsync(timeout);

    /// <summary>Lets go of a hosted session without ending it (AgentHub is closing); a session inside AgentHub ends.</summary>
    public void Detach()
    {
        _context.Dispose();
        Terminal.Detach();
    }

    /// <summary>Ends the session and releases the pane (the pane was closed).</summary>
    public void Dispose()
    {
        _context.Dispose();
        Terminal.Dispose();
    }
}

/// <summary>
/// An entry in a pane's location list or the History window: the home folder, a browsed folder, a registered repository,
/// or a folder found from its past sessions (<see cref="Found"/>).
/// </summary>
public sealed class LaunchLocation
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public RepositoryDefinition? Repository { get; init; }

    /// <summary>Set for a folder found from the sessions recorded on this computer rather than registered or browsed.</summary>
    public SessionFolder? Found { get; init; }

    /// <summary>The line under the name: the path, after the session count and last activity for a found folder.</summary>
    public string Detail => Found is null ? Path : $"{Describe(Found)} · {Path}";

    /// <summary>The History window's section for this entry.</summary>
    public string Group => Found is null ? "Repositories and folders" : "Found on this computer";

    public static LaunchLocation FromFound(SessionFolder folder)
    {
        var name = System.IO.Path.GetFileName(folder.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return new LaunchLocation { Name = "📁 " + (string.IsNullOrWhiteSpace(name) ? folder.Path : name), Path = folder.Path, Found = folder };
    }

    /// <summary>"12 sessions · today", "1 session · 3 Nov", plus "folder deleted" when it is gone.</summary>
    public static string Describe(SessionFolder folder)
    {
        var count = folder.Sessions == 1 ? "1 session" : $"{folder.Sessions} sessions";
        var local = folder.LastActivity.ToLocalTime();
        var today = DateTime.Today;
        var when = local.Date == today ? "today"
            : local.Date == today.AddDays(-1) ? "yesterday"
            : local.Year == today.Year ? local.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
        return folder.Exists ? $"{count} · {when}" : $"{count} · {when} · folder deleted";
    }

    // Screen readers and UI Automation name combo items by ToString, not DisplayMemberPath.
    public override string ToString() => Name;
}

/// <summary>A repository or CLI row in the pane launcher; <see cref="IsCurrent"/> outlines the current choice.</summary>
public sealed class PickRow(object item, string title, string meta, bool isCurrent)
{
    public object Item { get; } = item;
    public string Title { get; } = title;
    public string Meta { get; } = meta;
    public bool IsCurrent { get; } = isCurrent;

    // Screen readers and UI Automation name items by ToString.
    public override string ToString() => Title;
}

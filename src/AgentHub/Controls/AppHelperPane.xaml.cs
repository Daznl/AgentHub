using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AgentHub.Services;
using AgentHub.Services.Help;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Controls;

/// <summary>
/// The AgentHub helper pane, opened by the crab button in the main window's header and shown to the right of the
/// terminals: a short conversation about how to use AgentHub. Answers come from <see cref="AppHelp"/>. The Timeline window
/// shows the same pane as its Timeline helper, with <see cref="Actions"/> (the two reviews) above the conversation and
/// its own <see cref="Title"/>, <see cref="Intro"/> and <see cref="Examples"/>.
/// </summary>
public partial class AppHelperPane : UserControl
{
    private const string KeysHint = "Enter asks · Shift+Enter new line";
    private readonly ObservableCollection<HelperMessage> _log = [];
    private readonly ObservableCollection<string> _followUps = [];
    private readonly ObservableCollection<string> _examples =
    [
        "How do I start Claude Code in one of my repositories?",
        "What does the context badge on a pane mean?",
        "How do I put an orchestrator on a running session?",
        "How do I add a rule to my CLAUDE.md?",
    ];
    private readonly List<HelpTurn> _history = [];
    private CancellationTokenSource? _running;

    public AppHelperPane()
    {
        InitializeComponent();
        Log.ItemsSource = _log;
        FollowUps.ItemsSource = _followUps;
        ExampleChips.ItemsSource = _examples;
    }

    /// <summary>The Claude Code command to run (from Settings → Agents), read at each question.</summary>
    public Func<string> ClaudeCommand { get; set; } = () => "claude";

    /// <summary>A short summary of what is on screen (view, panes), sent with each question; null for none.</summary>
    public Func<string?>? AppState { get; set; }

    /// <summary>Raised by the pane's ✕.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised with true when a question is sent and false when its answer is in (or it stopped or failed).</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>
    /// Answers a question in place of <see cref="AppHelp"/>: the conversation so far, the question, the app state, a
    /// token. The Advanced templates window's Template helper builds and changes templates through it. Throws
    /// <see cref="OperationCanceledException"/> when stopped, <see cref="InvalidOperationException"/> with a message for the owner on failure.
    /// </summary>
    public Func<IReadOnlyList<HelpTurn>, string, string?, CancellationToken, Task<HelpAnswer>>? Answerer { get; set; }

    /// <summary>The crab's line while it works, before the seconds ("Looking that up…").</summary>
    public string WorkingText { get; set; } = "Looking that up…";

    /// <summary>The Ask button's tooltip: what asking does and what it can change.</summary>
    public string AskToolTip { get => AskBtn.ToolTip as string ?? ""; set => AskBtn.ToolTip = value; }

    /// <summary>A question is being answered.</summary>
    public bool IsAsking => _running is not null;

    /// <summary>Stops the question being answered, if any (the owner window is closing).</summary>
    public void Stop() => _running?.Cancel();

    /// <summary>Asks <paramref name="question"/> as if typed and sent (an action button's request). Ignored while another is being answered.</summary>
    public void Ask(string question)
    {
        if (_running is null) _ = AskAsync(question);
    }

    /// <summary>The pane's name in its header ("AgentHub helper", "Timeline helper").</summary>
    public string Title { get => TitleText.Text; set => TitleText.Text = value; }

    /// <summary>The line under the name.</summary>
    public string Subtitle { get => SubtitleText.Text; set => SubtitleText.Text = value; }

    /// <summary>The crab's first words, shown until the first question.</summary>
    public string Intro { get => IntroText.Text; set => IntroText.Text = value; }

    /// <summary>The grey text in the empty question box.</summary>
    public string Placeholder { get => InputHint.Text; set => InputHint.Text = value; }

    /// <summary>Example questions, each asked by a click; shown until the first question.</summary>
    public IList<string> Examples => _examples;

    /// <summary>
    /// Things the crab can do for the window that owns the pane, shown above the conversation and staying while it goes
    /// on: the Timeline's two reviews. Null for none.
    /// </summary>
    public object? Actions { get => ActionsHost.Content; set => ActionsHost.Content = value; }

    /// <summary>A line from the crab that is not an answer (a review started, a report arrived); not part of the conversation sent to Claude.</summary>
    public void Say(string text)
    {
        _log.Add(HelperMessage.Crab(text));
        NewChatBtn.IsEnabled = true;
        ScrollToEnd();
    }

    public void FocusInput() => Dispatcher.BeginInvoke(() => Input.Focus(), DispatcherPriority.Input);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _running?.Cancel();
        _log.Clear();
        _followUps.Clear();
        _history.Clear();
        Welcome.Visibility = Visibility.Visible;
        NewChatBtn.IsEnabled = false;
        FocusInput();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        InputHint.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAsk();
    }

    // Enter asks, as in a chat; Shift+Enter is a new line.
    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_running is null) _ = AskAsync(Input.Text);
    }

    private void AskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_running is { } running) running.Cancel();
        else _ = AskAsync(Input.Text);
    }

    // Example and follow-up chips ask their question straight away.
    private void Ask_FromChip(object sender, RoutedEventArgs e)
    {
        if (_running is null && (sender as Button)?.Content is string question) _ = AskAsync(question);
    }

    private void UpdateAsk()
    {
        AskBtn.Content = _running is null ? "Ask" : "■ Stop";
        AskBtn.IsEnabled = _running is not null || Input.Text.Trim().Length > 0;
        Hint.Text = _running is null ? KeysHint : "Thinking… you can type your next question.";
    }

    private async Task AskAsync(string text)
    {
        var question = text.Trim();
        if (question.Length == 0 || _running is not null) return;
        if (ReferenceEquals(text, Input.Text)) Input.Text = "";

        Welcome.Visibility = Visibility.Collapsed;
        _followUps.Clear();
        NewChatBtn.IsEnabled = true;
        _log.Add(HelperMessage.User(question));
        var reply = HelperMessage.Crab("");
        _log.Add(reply);
        ScrollToEnd();

        var history = _history.ToList();
        string? state = null;
        try { state = AppState?.Invoke(); }
        catch (InvalidOperationException) { /* a pane mid-change: answer without the state */ }

        var cts = new CancellationTokenSource();
        _running = cts;
        UpdateAsk();
        var started = DateTime.Now;
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Tick() => reply.Text = $"{WorkingText} {(int)(DateTime.Now - started).TotalSeconds} s";
        ticker.Tick += (_, _) => Tick();
        Tick();
        ticker.Start();
        BusyChanged?.Invoke(true);
        try
        {
            var answer = Answerer is { } answerer
                ? await answerer(history, question, state, cts.Token)
                : await AppHelp.RunAsync(new ProcessRunner(), ClaudeCommand(), history, question, state, cts.Token);
            reply.Text = answer.Answer.Length > 0 ? answer.Answer : "I have no answer for that. Try asking it another way.";
            _history.Add(new HelpTurn(true, question));
            _history.Add(new HelpTurn(false, reply.Text));
            foreach (var s in answer.Suggestions) _followUps.Add(s);
        }
        catch (OperationCanceledException)
        {
            if (_log.Contains(reply)) reply.Text = "Stopped.";
        }
        catch (InvalidOperationException ex)
        {
            reply.Text = ex.Message;
            reply.IsError = true;
        }
        finally
        {
            ticker.Stop();
            _running = null;
            cts.Dispose();
            UpdateAsk();
            ScrollToEnd();
            BusyChanged?.Invoke(false);
        }
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(Scroll.ScrollToEnd, DispatcherPriority.Background);
}

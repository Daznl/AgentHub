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
/// terminals: a short conversation about how to use AgentHub. Answers come from <see cref="AppHelp"/>.
/// </summary>
public partial class AppHelperPane : UserControl
{
    private const string KeysHint = "Enter asks · Shift+Enter new line";
    private readonly ObservableCollection<HelperMessage> _log = [];
    private readonly ObservableCollection<string> _followUps = [];
    private readonly List<HelpTurn> _history = [];
    private CancellationTokenSource? _running;

    public AppHelperPane()
    {
        InitializeComponent();
        Log.ItemsSource = _log;
        FollowUps.ItemsSource = _followUps;
    }

    /// <summary>The Claude Code command to run (from Settings → Agents), read at each question.</summary>
    public Func<string> ClaudeCommand { get; set; } = () => "claude";

    /// <summary>A short summary of what is on screen (view, panes), sent with each question; null for none.</summary>
    public Func<string?>? AppState { get; set; }

    /// <summary>Raised by the pane's ✕.</summary>
    public event Action? CloseRequested;

    public void FocusInput() => Dispatcher.BeginInvoke(() => Input.Focus(), DispatcherPriority.Input);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _running?.Cancel();
        _log.Clear();
        _followUps.Clear();
        _history.Clear();
        Examples.Visibility = Visibility.Visible;
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

        Examples.Visibility = Visibility.Collapsed;
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
        void Tick() => reply.Text = $"Looking that up… {(int)(DateTime.Now - started).TotalSeconds} s";
        ticker.Tick += (_, _) => Tick();
        Tick();
        ticker.Start();
        try
        {
            var answer = await AppHelp.RunAsync(new ProcessRunner(), ClaudeCommand(), history, question, state, cts.Token);
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
        }
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(Scroll.ScrollToEnd, DispatcherPriority.Background);
}

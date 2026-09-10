using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using AgentHub.Services;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AgentHub;

/// <summary>
/// Drives <c>gh auth login --web</c> from inside the app. The GitHub CLI performs the real device-flow
/// verification against GitHub; this window only shows the one-time code, opens the browser and waits.
/// </summary>
public partial class GitHubLoginWindow : Window
{
    private readonly GitHubService _github;
    private readonly CancellationTokenSource _cts = new();
    private string? _code;
    private string? _url;
    private bool _browserOpened;
    private bool _running;

    /// <summary>Login of the account that was signed in, or null if the flow did not complete.</summary>
    public string? SignedInLogin { get; private set; }

    public string Host { get; private set; } = "github.com";

    public GitHubLoginWindow(GitHubService github)
    {
        InitializeComponent();
        _github = github;
        Loaded += (_, _) => { HostBox.Focus(); HostBox.SelectAll(); };
        Closing += (_, _) => { if (_running) _cts.Cancel(); };
    }

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_running) Start_Click(sender, e);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        Host = string.IsNullOrWhiteSpace(HostBox.Text) ? "github.com" : HostBox.Text.Trim();

        _running = true;
        HostBox.IsEnabled = false;
        StartButton.IsEnabled = false;
        HostPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        CodeBadge.Visibility = Visibility.Collapsed;
        CodeCaption.Text = $"Waiting for GitHub CLI to issue a one-time code for {Host}…";
        StatusText.Text = $"Contacting {Host} via GitHub CLI…";

        var result = await _github.LoginWithBrowserAsync(
            Host,
            progress => Dispatcher.BeginInvoke(() => OnProgress(progress)),
            _cts.Token);

        _running = false;

        if (result.Succeeded)
        {
            SignedInLogin = result.Login;
            StatusText.Text = string.IsNullOrWhiteSpace(result.Login)
                ? "Signed in. GitHub CLI has stored the credentials."
                : $"Signed in as @{result.Login}.";
            CodeCaption.Text = "✓ Authentication complete";
            CopyButton.IsEnabled = false;
            OpenBrowserButton.IsEnabled = false;
            DialogResult = true;
            return;
        }

        if (_cts.IsCancellationRequested)
        {
            // Window is closing or the user pressed Cancel; nothing more to show.
            return;
        }

        StatusText.Text = "Sign-in failed: " + (result.Error ?? "unknown error");
        CodeCaption.Text = "Sign-in did not complete. Check the host and try again.";
        CodeBadge.Visibility = Visibility.Collapsed;
        UrlText.Text = string.Empty;
        CopyButton.IsEnabled = false;
        OpenBrowserButton.IsEnabled = false;
        _code = null;
        _url = null;
        _browserOpened = false;
        HostPanel.Visibility = Visibility.Visible;
        HostBox.IsEnabled = true;
        StartButton.IsEnabled = true;
        StartButton.Content = "⚡ Try again";
    }

    private void OnProgress(GitHubLoginProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.OneTimeCode) && progress.OneTimeCode != _code)
        {
            _code = progress.OneTimeCode;
            CodeBox.Text = _code;
            CodeBadge.Visibility = Visibility.Visible;
            CodeCaption.Text = $"Enter this one-time code on {Host}, then approve access:";
            CopyButton.IsEnabled = true;
            TryCopyCode();
        }

        if (!string.IsNullOrWhiteSpace(progress.VerificationUrl) && progress.VerificationUrl != _url)
        {
            _url = progress.VerificationUrl;
            UrlText.Text = _url;
            OpenBrowserButton.IsEnabled = true;
        }

        if (_code is not null && _url is not null && !_browserOpened)
        {
            _browserOpened = true;
            OpenBrowser();
            StatusText.Text = "Code copied to clipboard and browser opened. Waiting for you to approve on GitHub…";
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (TryCopyCode()) StatusText.Text = "Code copied to clipboard.";
    }

    private bool TryCopyCode()
    {
        if (string.IsNullOrWhiteSpace(_code)) return false;
        try
        {
            Clipboard.SetText(_code);
            return true;
        }
        catch
        {
            // Clipboard can be locked by another process; the code is still visible on screen.
            return false;
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e) => OpenBrowser();

    private void OpenBrowser()
    {
        if (string.IsNullOrWhiteSpace(_url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open browser: " + ex.Message + " — open the URL above manually.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts.Cancel();
            StatusText.Text = "Cancelling sign-in…";
        }
        DialogResult = false;
    }
}

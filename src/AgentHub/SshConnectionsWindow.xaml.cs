using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentHub.Models;

namespace AgentHub;

/// <summary>Adds and removes saved SSH connections. Edits the list in place; check <see cref="Changed"/> afterwards and save settings.</summary>
public partial class SshConnectionsWindow : Window
{
    private readonly List<SshConnection> _connections;

    public bool Changed { get; private set; }

    public SshConnectionsWindow(List<SshConnection> connections)
    {
        InitializeComponent();
        _connections = connections;
        RefreshList();
        Loaded += (_, _) => TargetBox.Focus();
    }

    private void RefreshList()
    {
        ConnectionList.ItemsSource = null;
        ConnectionList.ItemsSource = _connections;
        EmptyText.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => TryAdd();

    private void Input_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryAdd();
            e.Handled = true;
        }
    }

    private void TryAdd()
    {
        var target = TargetBox.Text.Trim();
        var error = SshConnection.Validate(target);
        if (error is null && _connections.Any(c => string.Equals(c.Target, target, StringComparison.OrdinalIgnoreCase)))
            error = $"{target} is already saved.";

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            TargetBox.Focus();
            return;
        }

        var name = NameBox.Text.Trim();
        _connections.Add(new SshConnection { Name = string.IsNullOrEmpty(name) ? null : name, Target = target });
        Changed = true;

        NameBox.Clear();
        TargetBox.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        RefreshList();
        TargetBox.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not SshConnection connection) return;
        _connections.Remove(connection);
        Changed = true;
        RefreshList();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

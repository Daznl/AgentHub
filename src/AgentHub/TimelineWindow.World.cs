using System.Windows;

namespace AgentHub;

/// <summary>🌐 3D: opens the one 3D world of every project (the main window's 🌐 World), which includes this repository.</summary>
public partial class TimelineWindow
{
    /// <summary>Raised by 🌐 3D; the main window opens the world.</summary>
    public event Action? WorldRequested;

    private void World_Click(object sender, RoutedEventArgs e) => WorldRequested?.Invoke();
}

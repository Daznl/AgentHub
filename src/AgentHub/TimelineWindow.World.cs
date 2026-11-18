using System.Diagnostics;
using System.Windows;
using AgentHub.Services.Timeline;
using AgentHub.Services.World;

namespace AgentHub;

/// <summary>
/// 🌐 3D: the Timeline's model, plus worktrees and live panes, written as <c>world.json</c> on every refresh while Godot
/// draws it. AgentHub reads; Godot only draws (see <see cref="WorldSnapshot"/>).
/// </summary>
public partial class TimelineWindow
{
    private Process? _world;
    private string? _worldPath;

    /// <summary>The Godot command from settings (<see cref="Models.AppSettings.GodotCommand"/>).</summary>
    public string GodotCommand { get; set; } = "godot";

    private bool WorldIsOpen => _world is { HasExited: false };

    private async void World_Click(object sender, RoutedEventArgs e)
    {
        if (WorldIsOpen)
        {
            StatusText.Text = "The 3D world is already open; it follows this window's refreshes.";
            return;
        }
        var godot = GodotLauncher.Resolve(GodotCommand);
        if (godot is null)
        {
            System.Windows.MessageBox.Show(this,
                $"Godot was not found (setting GodotCommand: \"{GodotCommand}\"). Install Godot 4, put it on PATH or set the full path in settings.json.",
                "3D world", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_model is not { } model)
        {
            StatusText.Text = "The timeline is still being read; press 🌐 3D again in a moment.";
            return;
        }
        WorldBtn.IsEnabled = false;
        try
        {
            await ExportWorldAsync(model);
            _world = GodotLauncher.Launch(godot, _worldPath!);
            StatusText.Text = $"3D world opened in Godot. It redraws from {_worldPath} whenever this window refreshes.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not open the 3D world: " + ex.Message;
        }
        finally
        {
            WorldBtn.IsEnabled = true;
        }
    }

    private async Task ExportWorldAsync(TimelineModel model)
    {
        try
        {
            var worktrees = await WorldSource.ReadWorktreesAsync(_runner, _scope);
            var snapshot = await Task.Run(() =>
                WorldSnapshotBuilder.Build(_scope, model, _pullRequests, worktrees, WorldSource.ReadPanes(_scope), DateTimeOffset.UtcNow));
            _worldPath ??= WorldFile.PathFor(_scope);
            await Task.Run(() => WorldFile.Write(_worldPath, snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not write the 3D world file: " + ex.Message;
        }
    }
}

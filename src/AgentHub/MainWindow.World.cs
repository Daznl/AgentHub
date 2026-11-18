using System.Diagnostics;
using System.Windows;
using AgentHub.Services.Orchestrator;
using AgentHub.Services.World;

namespace AgentHub;

/// <summary>
/// 🌐 World: one 3D world of every project, drawn by Godot from the files <see cref="WorldService"/> keeps up to date.
/// The service runs while Godot is open and stops when it closes (or with AgentHub).
/// </summary>
public partial class MainWindow
{
    private WorldService? _world;
    private Process? _worldProcess;

    private bool WorldIsOpen => _worldProcess is { HasExited: false };

    private void OpenWorld_Click(object sender, RoutedEventArgs e) => OpenWorld();

    private void OpenWorld()
    {
        if (WorldIsOpen)
        {
            StatusText.Text = "The 3D world is already open.";
            return;
        }
        var godot = GodotLauncher.Resolve(_settings.GodotCommand);
        if (godot is null)
        {
            System.Windows.MessageBox.Show(this,
                $"Godot was not found (setting GodotCommand: \"{_settings.GodotCommand}\"). Install Godot 4, put it on PATH or set the full path in settings.json.",
                "3D world", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _world ??= new WorldService(_runner, ReadWorldUiAsync, HistoryArchiveRoot);
            _world.Start();
            _worldProcess = GodotLauncher.Launch(godot, _world.WorldPath);
            _worldProcess.Exited += (_, _) => Dispatcher.InvokeAsync(StopWorld);
            StatusText.Text = $"3D world opened. AgentHub keeps {_world.WorldPath} and {_world.EventsFile} up to date while it is open.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not open the 3D world: " + ex.Message;
            StopWorld();
        }
    }

    private void StopWorld()
    {
        _world?.Dispose();
        _world = null;
        _worldProcess = null;
    }

    /// <summary>The panes, their transcripts and the orchestrators, read on the UI thread for the world's loop.</summary>
    private Task<WorldUiState> ReadWorldUiAsync() => Dispatcher.InvokeAsync(() =>
    {
        var roots = _settings.Repositories.Where(r => r.ExistsOnDisk).Select(r => r.LocalPath).ToList();
        var panes = _panes.Where(p => p.IsRunning)
            .Select(p => new WorldPaneInfo($"Terminal {p.PaneIndex}", p.TranscriptPath, p.ContextFolder ?? "")).ToList();
        var orchestrators = _paneOrchestrators.Select(kv => new WorldOrchestratorInfo(
            $"Terminal {kv.Key.PaneIndex}", kv.Value.Title, kv.Key.TranscriptPath, OrchestratorState(kv.Value),
            kv.Value.Run?.Current?.Step.DisplayTitle)).ToList();
        return new WorldUiState(roots, panes, orchestrators);
    }).Task;

    private static string OrchestratorState(PaneOrchestrator o) =>
        o.IsWaitingForOwner ? "waiting for you" : o.IsHandingOff ? "handing off" : o.IsThinking ? "thinking" : o.Run is not null ? "running a template" : "watching";

    /// <summary>Called whenever an orchestrator changes: its new instructions and notes go to the world as events.</summary>
    private void NoteOrchestratorsForWorld()
    {
        if (_world is not { IsRunning: true } world) return;
        foreach (var (pane, orchestrator) in _paneOrchestrators)
            world.NoteOrchestrator($"Terminal {pane.PaneIndex}", pane.TranscriptPath,
                orchestrator.Conversation.Select(x => (x.Kind.ToString(), x.Text)).ToList());
    }
}

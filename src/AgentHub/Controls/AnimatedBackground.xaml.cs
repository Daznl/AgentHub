using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Controls;

/// <summary>
/// Animated deep-space backdrop placed behind the main window content.
/// Motion is opt-out through <c>AppSettings.AnimatedBackground</c>; when disabled the
/// static layers (gradient, grid, glow orbs, vignette) still render so the window never
/// falls back to a flat colour.
/// </summary>
public partial class AnimatedBackground : UserControl
{
    private Storyboard? _drift;
    private bool _animated = true;

    public AnimatedBackground()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyState();
        Unloaded += (_, _) => StopDrift();
    }

    /// <summary>Turns the drifting/scanning motion on or off. Static layers are unaffected.</summary>
    public void SetAnimated(bool animated)
    {
        _animated = animated;
        if (IsLoaded)
        {
            ApplyState();
        }
    }

    private void ApplyState()
    {
        if (_animated)
        {
            StartDrift();
        }
        else
        {
            StopDrift();
        }
    }

    private void StartDrift()
    {
        if (_drift is not null)
        {
            return;
        }

        _drift = (Storyboard)FindResource("DriftStoryboard");
        _drift.Begin(this, isControllable: true);
    }

    private void StopDrift()
    {
        if (_drift is null)
        {
            return;
        }

        _drift.Stop(this);
        _drift = null;
    }
}

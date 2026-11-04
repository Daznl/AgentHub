using System.Windows;
using AgentHub.Controls;

namespace AgentHub.Tests;

public class WindowPlacementTests
{
    // A laptop at 150%: 1920 × 1080 physical, taskbar at the bottom, is 1280 × 672 logical pixels of work area.
    private static readonly Rect Laptop = new(0, 0, 1280, 672);

    [Fact]
    public void A_window_that_fits_is_left_alone()
    {
        var window = new Rect(100, 50, 800, 500);
        Assert.Equal(window, WindowPlacement.Fit(window, Laptop));
    }

    [Fact]
    public void A_window_centred_above_the_screen_top_is_shrunk_and_moved_down()
    {
        // The Timeline window: 1240 × 840 centred on a maximised owner starts at -84.
        var fitted = WindowPlacement.Fit(new Rect(20, -84, 1240, 840), Laptop);
        Assert.Equal(new Rect(20, 0, 1240, 672), fitted);
    }

    [Fact]
    public void A_window_wider_and_taller_than_the_screen_becomes_the_work_area()
    {
        var fitted = WindowPlacement.Fit(new Rect(-30, -70, 1380, 860), Laptop);
        Assert.Equal(Laptop, fitted);
    }

    [Fact]
    public void A_window_hanging_over_the_right_and_bottom_edges_is_moved_back_in()
    {
        var fitted = WindowPlacement.Fit(new Rect(900, 400, 520, 500), Laptop);
        Assert.Equal(new Rect(760, 172, 520, 500), fitted);
    }

    [Fact]
    public void A_second_screen_to_the_left_keeps_its_own_origin()
    {
        var left = new Rect(-1920, 0, 1920, 1040);
        // Hangs 240 over that screen's right edge (x = 0) and 60 over its top: moved left and down, not resized.
        var fitted = WindowPlacement.Fit(new Rect(-1000, -60, 1240, 840), left);
        Assert.Equal(new Rect(-1240, 0, 1240, 840), fitted);
    }
}

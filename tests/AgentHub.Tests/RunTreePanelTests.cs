using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentHub.Controls;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// The run tree beside a pane, drawn off screen: done steps green, the step the run is at blue. With
/// <c>AGENTHUB_RENDER_DIR</c> set, the drawing is also saved there as <c>run-tree-render.png</c> to look at.
/// </summary>
public class RunTreePanelTests
{
    private static readonly Color Green = Color.FromRgb(0x34, 0xE5, 0xA8);
    private static readonly Color Blue = Color.FromRgb(0x2F, 0xD4, 0xF5);

    [Fact]
    public void It_draws_done_steps_green_and_the_step_the_run_is_at_blue() => OnSta(() =>
    {
        var run = new TemplateRun(SampleTemplates.StagedBuild());
        var before = Render(run);
        Assert.Equal(0, Count(before, Green));
        Assert.Equal(0, Count(before, Blue));

        run.Start();
        run.CompleteCurrent();
        run.CompleteCurrent();
        var pixels = Render(run, save: "run-tree-render.png");
        Assert.True(Count(pixels, Green) > 40, "done steps have green nodes and ticks");
        Assert.True(Count(pixels, Blue) > 40, "the step the run is at, and its stage, are blue");
    });

    private static byte[] Render(TemplateRun run, string? save = null)
    {
        var panel = new RunTreePanel { Height = 520 };
        panel.Show(run);
        var size = new System.Windows.Size(panel.Width, panel.Height);
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        if (save is not null && Environment.GetEnvironmentVariable("AGENTHUB_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(dir, save));
            png.Save(file);
        }
        var pixels = new byte[(int)size.Width * (int)size.Height * 4];
        bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
        return pixels;
    }

    /// <summary>Pixels of that colour, give or take a step of rounding (Pbgra32: blue, green, red, alpha).</summary>
    private static int Count(byte[] pixels, Color colour)
    {
        var n = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - colour.B) <= 2 && Math.Abs(pixels[i + 1] - colour.G) <= 2 && Math.Abs(pixels[i + 2] - colour.R) <= 2 && pixels[i + 3] == 255) n++;
        return n;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

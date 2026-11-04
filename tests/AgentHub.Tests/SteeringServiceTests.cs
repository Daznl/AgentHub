using System.IO;
using AgentHub.Models;
using AgentHub.Services.Ideas;

namespace AgentHub.Tests;

public class SteeringServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agenthub steering " + Guid.NewGuid().ToString("n"));

    public SteeringServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Notes_stay_queued_until_delivered_and_only_queued_ones_can_be_removed()
    {
        var svc = new SteeringService(Path.Combine(_dir, "steering.json"));
        var a = await svc.AddAsync("idea1", "  focus on the backtest report  ");
        var b = await svc.AddAsync("idea1", "use pytest");
        await svc.AddAsync("idea2", "other idea");

        Assert.Equal("focus on the backtest report", a.Text);
        Assert.Equal(2, (await svc.GetPendingAsync("idea1")).Count);

        await svc.MarkDeliveredAsync([a]);
        var pending = await svc.GetPendingAsync("idea1");
        Assert.Equal(b.Id, Assert.Single(pending).Id);
        Assert.True((await svc.GetForIdeaAsync("idea1")).Single(n => n.Id == a.Id).IsDelivered);

        Assert.False(await svc.RemovePendingAsync(a.Id));   // delivered: kept as a record
        Assert.True(await svc.RemovePendingAsync(b.Id));
        Assert.Empty(await svc.GetPendingAsync("idea1"));
        Assert.Single(await svc.GetPendingAsync("idea2"));
    }

    [Fact]
    public async Task Notes_survive_a_new_service_instance()
    {
        var path = Path.Combine(_dir, "steering.json");
        await new SteeringService(path).AddAsync("idea1", "keep it");
        Assert.Equal("keep it", Assert.Single(await new SteeringService(path).GetPendingAsync("idea1")).Text);
    }

    [Fact]
    public void Store_sits_beside_ideas_json() =>
        Assert.Equal(
            Path.Combine(_dir, "steering.json"),
            typeof(SteeringService).GetField("_storagePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(SteeringService.BesideIdeas(new IdeaService(Path.Combine(_dir, "ideas.json")))));

    [Fact]
    public void Handoff_gets_a_steering_section_at_the_end_when_missing()
    {
        var note = new SteeringNote { Text = "focus on\nthe report", CreatedAt = new DateTimeOffset(2026, 11, 4, 22, 50, 0, TimeSpan.Zero) };

        var result = SteeringService.AddToHandoff("# AI Stock Bot\n\n## Next tasks\n- [ ] train\n\n\n", note);

        Assert.StartsWith("# AI Stock Bot\n\n## Next tasks\n- [ ] train\n\n## Steering notes\n", result);
        Assert.Contains("- 2026-11-04 22:50: focus on the report", result);
    }

    [Fact]
    public void Later_notes_append_inside_the_existing_section_before_the_next_heading()
    {
        var handoff = "# Bot\n\n## Steering notes\n\nIntro.\n\n- 2026-11-04 22:50: first\n\n## Next tasks\n- [ ] train\n";
        var note = new SteeringNote { Text = "second", CreatedAt = new DateTimeOffset(2026, 11, 4, 23, 0, 0, TimeSpan.Zero) };

        var result = SteeringService.AddToHandoff(handoff, note);

        Assert.Equal("# Bot\n\n## Steering notes\n\nIntro.\n\n- 2026-11-04 22:50: first\n- 2026-11-04 23:00: second\n\n## Next tasks\n- [ ] train\n", result);
    }

    [Fact]
    public void Delivery_message_names_every_note_and_sends_the_agent_back_to_work()
    {
        var one = SteeringService.FormatDelivery([new SteeringNote { Text = "use pytest" }]);
        Assert.StartsWith("[DIRECTION FROM THE USER]: use pytest", one);
        Assert.Contains("carry on building", one);

        var two = SteeringService.FormatDelivery([new SteeringNote { Text = "a" }, new SteeringNote { Text = "b" }]);
        Assert.Contains("(1) a (2) b", two);
    }
}

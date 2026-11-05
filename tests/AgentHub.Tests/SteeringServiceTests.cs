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
}

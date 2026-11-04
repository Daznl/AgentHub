using System.IO;
using AgentHub.Models;
using AgentHub.Services.Ideas;
using Xunit;

namespace AgentHub.Tests;

public class IdeaServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _storagePath;

    public IdeaServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AgentHubTests_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_testDir);
        _storagePath = Path.Combine(_testDir, "test-ideas.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadAsync_WithNoFile_SeedsDefaultsAndCreatesFile()
    {
        var service = new IdeaService(_storagePath);
        var ideas = await service.LoadAsync();

        Assert.NotEmpty(ideas);
        Assert.True(File.Exists(_storagePath));
    }

    [Fact]
    public async Task AddIdeaAsync_AppendsAndPersists()
    {
        var service = new IdeaService(_storagePath);
        var idea = new ProjectIdea
        {
            Title = "Drone Flight Simulator",
            Description = "A 3D flight simulation engine",
            TargetDirectory = @"C:\Apps\DroneSim",
            AgentCommand = "claude"
        };

        await service.AddIdeaAsync(idea);
        var loaded = await service.LoadAsync();

        Assert.Contains(loaded, i => i.Title == "Drone Flight Simulator" && i.TargetDirectory == @"C:\Apps\DroneSim");
    }

    [Fact]
    public async Task UpdateIdeaAsync_ModifiesExistingItem()
    {
        var service = new IdeaService(_storagePath);
        var idea = new ProjectIdea
        {
            Title = "Initial Name",
            Description = "Initial spec"
        };

        await service.AddIdeaAsync(idea);
        idea.Title = "Updated Name";
        idea.Status = IdeaStatus.Completed;

        await service.UpdateIdeaAsync(idea);
        var loaded = await service.LoadAsync();
        var found = loaded.FirstOrDefault(i => i.Id == idea.Id);

        Assert.NotNull(found);
        Assert.Equal("Updated Name", found.Title);
        Assert.Equal(IdeaStatus.Completed, found.Status);
    }

    [Fact]
    public async Task DeleteIdeaAsync_RemovesItem()
    {
        var service = new IdeaService(_storagePath);
        var idea1 = new ProjectIdea { Title = "Idea 1" };
        var idea2 = new ProjectIdea { Title = "Idea 2" };

        await service.AddIdeaAsync(idea1);
        await service.AddIdeaAsync(idea2);

        await service.DeleteIdeaAsync(idea1.Id);
        var loaded = await service.LoadAsync();

        Assert.DoesNotContain(loaded, i => i.Id == idea1.Id);
        Assert.Contains(loaded, i => i.Id == idea2.Id);
    }

    [Fact]
    public async Task ReorderAsync_ChangesItemPosition()
    {
        var service = new IdeaService(_storagePath);
        var ideaA = new ProjectIdea { Title = "Alpha" };
        var ideaB = new ProjectIdea { Title = "Beta" };
        var ideaC = new ProjectIdea { Title = "Gamma" };

        await service.SaveAsync([ideaA, ideaB, ideaC]);

        // Move Gamma to top (index 0)
        await service.ReorderAsync(ideaC.Id, 0);
        var loaded = await service.LoadAsync();

        Assert.Equal(ideaC.Id, loaded[0].Id);
        Assert.Equal(ideaA.Id, loaded[1].Id);
        Assert.Equal(ideaB.Id, loaded[2].Id);
    }

    [Fact]
    public async Task GetNextQueuedAsync_PrioritizesBuildingThenQueued()
    {
        var service = new IdeaService(_storagePath);
        var completed = new ProjectIdea { Title = "Done", Status = IdeaStatus.Completed };
        var queued1 = new ProjectIdea { Title = "First Queued", Status = IdeaStatus.Queued };
        var queued2 = new ProjectIdea { Title = "Second Queued", Status = IdeaStatus.Queued };

        await service.SaveAsync([completed, queued1, queued2]);

        var next = await service.GetNextQueuedAsync();
        Assert.NotNull(next);
        Assert.Equal("First Queued", next.Title);

        // If one is Building, it takes precedence
        queued2.Status = IdeaStatus.Building;
        await service.UpdateIdeaAsync(queued2);

        next = await service.GetNextQueuedAsync();
        Assert.NotNull(next);
        Assert.Equal("Second Queued", next.Title);
    }
}

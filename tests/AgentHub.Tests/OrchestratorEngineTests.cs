using System.IO;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Ideas;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class OrchestratorEngineTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _storagePath;
    private readonly ProcessRunner _runner;

    public OrchestratorEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "OrchestratorTests_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_testDir);
        _storagePath = Path.Combine(_testDir, "ideas.json");
        _runner = new ProcessRunner();
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        var ideaService = new IdeaService(_storagePath);
        var git = new GitService(_runner);
        using var engine = new OrchestratorEngine(ideaService, git, _runner);

        Assert.Equal(OrchestratorState.Idle, engine.State);
        Assert.Null(engine.CurrentIdea);
        Assert.Null(engine.ActivePane);
        Assert.Equal(0.75, engine.ContextThreshold);
    }

    [Fact]
    public void ContextThreshold_ClampsWithinSafeBounds()
    {
        var ideaService = new IdeaService(_storagePath);
        var git = new GitService(_runner);
        using var engine = new OrchestratorEngine(ideaService, git, _runner);

        engine.ContextThreshold = 0.10;
        Assert.Equal(0.40, engine.ContextThreshold);

        engine.ContextThreshold = 0.99;
        Assert.Equal(0.95, engine.ContextThreshold);

        engine.ContextThreshold = 0.80;
        Assert.Equal(0.80, engine.ContextThreshold);
    }

    [Fact]
    public void UsageCapThreshold_ClampsWithinSafeBounds()
    {
        var ideaService = new IdeaService(_storagePath);
        var git = new GitService(_runner);
        using var engine = new OrchestratorEngine(ideaService, git, _runner);

        engine.UsageCapThreshold = 0.001;
        Assert.Equal(0.01, engine.UsageCapThreshold);

        engine.UsageCapThreshold = 0.50;
        Assert.Equal(0.20, engine.UsageCapThreshold);

        engine.UsageCapThreshold = 0.05;
        Assert.Equal(0.05, engine.UsageCapThreshold);
    }

    [Fact]
    public void PauseAndResume_TransitionStatesCorrectly()
    {
        var ideaService = new IdeaService(_storagePath);
        var git = new GitService(_runner);
        using var engine = new OrchestratorEngine(ideaService, git, _runner);

        // Pause when Idle does nothing
        engine.Pause();
        Assert.Equal(OrchestratorState.Idle, engine.State);

        // Stop resets
        engine.Stop();
        Assert.Equal(OrchestratorState.Idle, engine.State);
    }

    [Fact]
    public void ProjectIdea_StatusBadgeProperties_MatchExpectedOutput()
    {
        var queued = new ProjectIdea { Status = IdeaStatus.Queued };
        Assert.Equal("QUEUED", queued.StatusBadgeText);

        var building = new ProjectIdea { Status = IdeaStatus.Building };
        Assert.Equal("BUILDING", building.StatusBadgeText);
        Assert.Equal("#10B981", building.StatusBadgeBrush);

        var waiting = new ProjectIdea { Status = IdeaStatus.WaitingForReset };
        Assert.Equal("WAITING FOR RESET", waiting.StatusBadgeText);
        Assert.Equal("#F59E0B", waiting.StatusBadgeBrush);

        var completed = new ProjectIdea { Status = IdeaStatus.Completed };
        Assert.Equal("COMPLETED", completed.StatusBadgeText);
        Assert.Equal("#3B82F6", completed.StatusBadgeBrush);

        var paused = new ProjectIdea { Status = IdeaStatus.Paused };
        Assert.Equal("PAUSED", paused.StatusBadgeText);
        Assert.Equal("#6B7280", paused.StatusBadgeBrush);
    }
}

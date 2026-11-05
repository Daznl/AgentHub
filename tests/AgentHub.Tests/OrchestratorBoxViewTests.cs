using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class OrchestratorBoxViewTests
{
    [Fact]
    public void Nothing_is_shown_when_no_build_is_being_orchestrated()
    {
        Assert.Null(OrchestratorBoxView.For(OrchestratorState.Idle, false, "Tally", 1, "x", "y"));
        Assert.Null(OrchestratorBoxView.For(OrchestratorState.Building, false, null, 1, "x", "y"));
    }

    [Theory]
    [InlineData(OrchestratorState.Building, false, "DRIVING", false)]
    [InlineData(OrchestratorState.Building, true, "THINKING…", false)]
    [InlineData(OrchestratorState.HandoffPending, false, "HANDING OFF", false)]
    [InlineData(OrchestratorState.WaitingForReset, false, "WAITING FOR USAGE RESET", false)]
    [InlineData(OrchestratorState.Paused, true, "PAUSED", true)]
    public void The_state_label_follows_the_engine(OrchestratorState state, bool thinking, string label, bool paused)
    {
        var v = OrchestratorBoxView.For(state, thinking, "Tally", 2, "Slice 3 of 4.\n Tests pass.", "Build ONLY\r\n Slice 3.")!;
        Assert.Equal(label, v.State);
        Assert.Equal(paused, v.Paused);
        Assert.Equal("Tally · worker #2", v.Title);
        Assert.Equal("Slice 3 of 4. Tests pass.", v.Progress);
        Assert.Equal("Build ONLY Slice 3.", v.LastSent);
    }
}

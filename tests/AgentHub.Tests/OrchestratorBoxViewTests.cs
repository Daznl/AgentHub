using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class OrchestratorBoxViewTests
{
    [Fact]
    public void Nothing_is_shown_when_no_build_is_being_orchestrated()
    {
        Assert.Null(OrchestratorBoxView.For(OrchestratorState.Idle, false, "Tally", 1, "x", "r", "y"));
        Assert.Null(OrchestratorBoxView.For(OrchestratorState.Building, false, null, 1, "x", "r", "y"));
    }

    [Theory]
    [InlineData(OrchestratorState.Building, false, "DRIVING", false)]
    [InlineData(OrchestratorState.Building, true, "THINKING…", false)]
    [InlineData(OrchestratorState.HandoffPending, false, "HANDING OFF", false)]
    [InlineData(OrchestratorState.WaitingForReset, false, "WAITING FOR USAGE RESET", false)]
    [InlineData(OrchestratorState.Paused, true, "PAUSED", true)]
    public void The_state_label_follows_the_engine(OrchestratorState state, bool thinking, string label, bool paused)
    {
        var v = OrchestratorBoxView.For(state, thinking, "Tally", 2, "Slice 3 of 4.\n Tests pass.", "Slice 2 done:\n 14 tests pass.", "Build ONLY\r\n Slice 3.")!;
        Assert.Equal(label, v.State);
        Assert.Equal(paused, v.Paused);
        Assert.Equal("Tally · worker #2", v.Title);
        Assert.Equal("Slice 3 of 4. Tests pass.", v.Progress);
        Assert.Equal("Slice 2 done: 14 tests pass.", v.LastRead);
        Assert.Equal("Build ONLY Slice 3.", v.LastSent);
        Assert.Equal("Last sent: ", v.SentLabel);
    }

    [Fact]
    public void A_new_worker_has_read_nothing_and_a_wait_marks_the_last_message_as_earlier()
    {
        Assert.Equal(OrchestratorBoxView.NothingReadYet, OrchestratorBoxView.For(OrchestratorState.Building, false, "Tally", 1, "", null, "Start.")!.LastRead);
        Assert.Equal("Last sent (earlier; it chose to wait): ", OrchestratorBoxView.For(OrchestratorState.Building, false, "Tally", 1, "", "Still running tests.", "Build slice 2.", sentAnswersRead: false)!.SentLabel);
        Assert.Equal("Last sent (before this reply; deciding the next one): ", OrchestratorBoxView.For(OrchestratorState.Building, true, "Tally", 1, "", "Slice 1 done.", "Build slice 1.", sentAnswersRead: false)!.SentLabel);
    }
}

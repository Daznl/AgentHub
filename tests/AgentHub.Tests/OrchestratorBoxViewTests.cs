using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class OrchestratorBoxViewTests
{
    private static readonly IReadOnlyList<OrchestratorExchange> Talk =
    [
        new(DateTimeOffset.Now, ExchangeKind.Sent, "Build slice 1."),
        new(DateTimeOffset.Now, ExchangeKind.Read, "Slice 1 done:\n 14 tests pass."),
    ];

    [Fact]
    public void Nothing_is_shown_when_no_build_is_being_orchestrated()
    {
        Assert.Null(OrchestratorBoxView.For(OrchestratorState.Idle, false, "Tally", 1, "x", Talk));
        Assert.Null(OrchestratorBoxView.For(OrchestratorState.Building, false, null, 1, "x", Talk));
    }

    [Theory]
    [InlineData(OrchestratorState.Building, false, "DRIVING", false)]
    [InlineData(OrchestratorState.Building, true, "THINKING…", false)]
    [InlineData(OrchestratorState.HandoffPending, false, "HANDING OFF", false)]
    [InlineData(OrchestratorState.WaitingForReset, false, "WAITING FOR USAGE RESET", false)]
    [InlineData(OrchestratorState.Paused, true, "PAUSED", true)]
    public void The_state_label_follows_the_engine(OrchestratorState state, bool thinking, string label, bool paused)
    {
        var v = OrchestratorBoxView.For(state, thinking, "Tally", 2, "Slice 3 of 4.\n Tests pass.", Talk)!;
        Assert.Equal(label, v.State);
        Assert.Equal(paused, v.Paused);
        Assert.Equal("Tally · worker #2", v.Title);
        Assert.Equal("Slice 3 of 4. Tests pass.", v.Progress);
    }

    [Fact]
    public void The_whole_conversation_is_shown_with_each_message_in_full()
    {
        var v = OrchestratorBoxView.For(OrchestratorState.Building, false, "Tally", 1, "", Talk)!;
        Assert.Same(Talk, v.Conversation);
        Assert.Equal("Slice 1 done:\n 14 tests pass.", v.Conversation[1].Text);
        Assert.Empty(OrchestratorBoxView.For(OrchestratorState.Building, false, "Tally", 1, "", null)!.Conversation);
    }

    [Theory]
    [InlineData(ExchangeKind.Read, "Worker → orchestrator")]
    [InlineData(ExchangeKind.Sent, "Orchestrator → worker")]
    [InlineData(ExchangeKind.Wait, "Orchestrator waits")]
    [InlineData(ExchangeKind.Steer, "Your steer → orchestrator")]
    [InlineData(ExchangeKind.Question, "Orchestrator asks you")]
    [InlineData(ExchangeKind.AgentHub, "AgentHub → worker")]
    public void Each_message_says_who_it_went_between(ExchangeKind kind, string label) =>
        Assert.Equal(label, new OrchestratorExchange(DateTimeOffset.Now, kind, "x").Label);
}

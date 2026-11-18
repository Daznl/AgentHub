using System.Collections.ObjectModel;
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
    public void A_shown_list_follows_the_conversation_and_starts_over_when_it_does_not_continue()
    {
        var shown = new ObservableCollection<OrchestratorExchange>();
        Assert.True(OrchestratorBoxView.Sync(shown, Talk));
        Assert.Equal(2, shown.Count);
        Assert.False(OrchestratorBoxView.Sync(shown, Talk)); // nothing new

        var longer = Talk.Append(new OrchestratorExchange(DateTimeOffset.Now, ExchangeKind.Reply, "Done.")).ToList();
        Assert.True(OrchestratorBoxView.Sync(shown, longer));
        Assert.Equal(3, shown.Count);
        Assert.Same(Talk[0], shown[0]);

        var fresh = new List<OrchestratorExchange> { new(DateTimeOffset.Now, ExchangeKind.Note, "Session #2") }; // a new session: a different list
        Assert.True(OrchestratorBoxView.Sync(shown, fresh));
        Assert.Single(shown);
        Assert.Equal("Session #2", shown[0].Text);
    }

    [Theory]
    [InlineData(ExchangeKind.Read, "Worker → orchestrator")]
    [InlineData(ExchangeKind.Sent, "Orchestrator → worker")]
    [InlineData(ExchangeKind.Wait, "Orchestrator waits")]
    [InlineData(ExchangeKind.Steer, "You → orchestrator")]
    [InlineData(ExchangeKind.Question, "Orchestrator asks you")]
    [InlineData(ExchangeKind.Reply, "Orchestrator → you")]
    [InlineData(ExchangeKind.Note, "AgentHub")]
    [InlineData(ExchangeKind.AgentHub, "AgentHub → worker")]
    public void Each_message_says_who_it_went_between(ExchangeKind kind, string label) =>
        Assert.Equal(label, new OrchestratorExchange(DateTimeOffset.Now, kind, "x").Label);
}

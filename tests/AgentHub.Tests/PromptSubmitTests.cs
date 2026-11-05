using AgentHub.Terminal;

namespace AgentHub.Tests;

public class PromptSubmitTests
{
    [Fact]
    public void Flattened_prompt_has_no_line_breaks_so_only_the_separate_enter_submits()
    {
        var prompt = "\n\n[ORCHESTRATOR NOTICE]: line one\r\nline two\n1. first\n2. second\r\n";

        var flat = EmbeddedTerminalControl.FlattenPrompt(prompt);

        Assert.Equal("[ORCHESTRATOR NOTICE]: line one line two 1. first 2. second", flat);
        Assert.DoesNotContain('\n', flat);
        Assert.DoesNotContain('\r', flat);
    }

    [Theory]
    [InlineData("[COMPLETED]\n# AI Stock Bot\n", true)]
    [InlineData("# [COMPLETED] AI Stock Bot\n", true)]
    [InlineData("\n\n**[completed]** all done\n", true)]
    [InlineData("# AI Stock Bot\n\nNotes: put [COMPLETED] at the top when finished.\n", false)]
    [InlineData("# AI Stock Bot\n- [ ] Implement core functionality\n", false)]
    [InlineData("", false)]
    public void Only_a_completed_marker_at_the_top_finishes_an_idea(string handoff, bool expected) =>
        Assert.Equal(expected, AgentHub.Services.Orchestrator.OrchestratorEngine.IsMarkedCompleted(handoff));

    [Fact]
    public void Worker_rules_tell_the_agent_how_to_finish_and_what_the_orchestrator_sees()
    {
        Assert.Contains("[COMPLETED]", AgentHub.Services.Orchestrator.OrchestratorMessages.AutonomyRules);
        Assert.Contains("summary", AgentHub.Services.Orchestrator.OrchestratorMessages.AutonomyRules);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void Blank_prompt_flattens_to_empty(string prompt) =>
        Assert.Equal("", EmbeddedTerminalControl.FlattenPrompt(prompt));
}

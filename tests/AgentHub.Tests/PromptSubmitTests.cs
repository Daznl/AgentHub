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
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void Blank_prompt_flattens_to_empty(string prompt) =>
        Assert.Equal("", EmbeddedTerminalControl.FlattenPrompt(prompt));
}

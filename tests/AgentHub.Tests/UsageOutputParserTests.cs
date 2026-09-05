using AgentHub.Models;
using AgentHub.Services.Usage;
using Xunit;

namespace AgentHub.Tests;

public class UsageOutputParserTests
{
    [Fact]
    public void Parse_AntigravityTabOutput_ParsesGeminiAndClaudeGPTLimits()
    {
        var raw = "Gemini Models\tWeekly Limit Remaining\t91%\t2026-09-08T18:00:00Z\r\n" +
                  "Claude and GPT models\tWeekly Limit Remaining\t100%\t2026-09-12T17:40:00Z\r\n";

        var provider = new UsageProviderDefinition
        {
            Id = "gemini",
            Name = "Antigravity",
            Command = "agy",
            Arguments = ["-p", "\"/usage\""]
        };

        var limits = UsageOutputParser.Parse(raw, provider);

        Assert.Equal(2, limits.Count);

        var gemini = limits.FirstOrDefault(l => l.Name.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gemini);
        Assert.True(Math.Abs(gemini.RemainingFraction - 0.91) < 0.001);
        Assert.NotNull(gemini.ResetsAt);

        var claudeGpt = limits.FirstOrDefault(l => l.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(claudeGpt);
        Assert.True(Math.Abs(claudeGpt.RemainingFraction - 1.0) < 0.001);
        Assert.NotNull(claudeGpt.ResetsAt);
    }

    [Fact]
    public void Parse_ClaudeStatusOutput_ParsesSessionAndWeeklyUsed()
    {
        var raw = """
            Session
            Total cost:            $0.0000
            Total duration (API):  0s
            Total duration (wall): 42s
            Total code changes:    0 lines added, 0 lines removed
            Usage:                 0 input, 0 output, 0 cache read, 0 cache write

            Current session
            ██████████████████████████████████████████████████ 100% used
            Resets 7:50pm (Australia/Perth)

            Current week (all models)
            ███████████                                        22% used
            Resets Sep 8, 2am (Australia/Perth)
            """;

        var provider = new UsageProviderDefinition
        {
            Id = "claude",
            Name = "Claude Code",
            Command = "claude",
            Arguments = ["/status"]
        };

        var limits = UsageOutputParser.Parse(raw, provider);

        Assert.Equal(2, limits.Count);

        var session = limits.FirstOrDefault(l => l.Name.Equals("Session", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(session);
        // 100% used => 0% remaining
        Assert.True(Math.Abs(session.RemainingFraction - 0.0) < 0.001);
        Assert.Contains("7:50pm", session.ResetText);

        var weekly = limits.FirstOrDefault(l => l.Name.Equals("Weekly", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(weekly);
        // 22% used => 78% remaining
        Assert.True(Math.Abs(weekly.RemainingFraction - 0.78) < 0.001);
        Assert.Contains("Sep 8, 2am", weekly.ResetText);
    }

    [Fact]
    public void Parse_CodexStatusOutput_Parses5HourAndWeeklyLeft()
    {
        var raw = """
            OpenAI Codex
            5h limit:       [====                ] 20% left (resets in 2h 15m)
            Weekly limit:   [===============     ] 75% left (resets Sat 12:00)
            """;

        var provider = new UsageProviderDefinition
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Arguments = ["/status"],
            UnqualifiedPercentagesAreRemaining = true
        };

        var limits = UsageOutputParser.Parse(raw, provider);

        Assert.Equal(2, limits.Count);

        var fiveHour = limits.FirstOrDefault(l => l.Name.Contains("5-Hour", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fiveHour);
        Assert.True(Math.Abs(fiveHour.RemainingFraction - 0.20) < 0.001);
        Assert.Contains("2h 15m", fiveHour.ResetText);

        var weekly = limits.FirstOrDefault(l => l.Name.Contains("Weekly", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(weekly);
        Assert.True(Math.Abs(weekly.RemainingFraction - 0.75) < 0.001);
        Assert.Contains("Sat 12:00", weekly.ResetText);
    }

    [Fact]
    public void Parse_UnsupportedOutput_ReturnsEmptyList()
    {
        var raw = "Error: stdin is not a terminal\r\nExited with code 1";
        var provider = new UsageProviderDefinition
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex"
        };

        var limits = UsageOutputParser.Parse(raw, provider);
        Assert.Empty(limits);
    }

    [Fact]
    public void StripAnsi_RemovesTerminalControlAndOrphanEscapeCodes()
    {
        var raw = "[?9001h[?1004h[?2004h[?25l[2J[m[H]0;C:\\WINDOWS\\SYSTEM32\\cmd.exe[?25h[?2026h[0 q[?2026l[?25l[2m╭───────────────────────────────────────╮[22m[K[2m\r\n" +
                  "│ >_ [22m[1mOpenAI Codex\r\n" +
                  "5h limit:       [====                ] 20% left (resets in 2h 15m)\r\n" +
                  "Weekly limit:   [===============     ] 75% left (resets Sat 12:00)";

        var cleaned = UsageOutputParser.StripAnsi(raw);

        Assert.DoesNotContain("[?9001h", cleaned);
        Assert.DoesNotContain("[2J", cleaned);
        Assert.DoesNotContain("]0;", cleaned);
        Assert.Contains("OpenAI Codex", cleaned);
        Assert.Contains("5h limit:", cleaned);
        Assert.Contains("Weekly limit:", cleaned);
    }

    [Fact]
    public void StripAnsi_PreservesSpacesFromCursorMovement()
    {
        var raw = "Run\x1b[C/init\x1b[1Cto\x1b[3Ccreate\x1b[Ca\x1b[CCLAUDE.md";
        var cleaned = UsageOutputParser.StripAnsi(raw);

        Assert.Contains("Run /init to", cleaned);
        Assert.Contains("create a CLAUDE.md", cleaned);
        Assert.DoesNotContain("Run/init", cleaned);
    }

    [Fact]
    public void ExtractRelevantScreen_ClaudeLoadedOutput_ExtractsSessionTableOnly()
    {
        var raw = """
            ╭─── Claude Code v2.1.163 ────────────────────────────────────────╮
            │ Tips for getting started                                        │
            │ Welcome back Daniel! Run /init to create a CLAUDE.md file       │
            │ What's new                                                      │
            │ Added requiredMinimumVersion and requiredMaximumVersion         │
            ╰─────────────────────────────────────────────────────────────────╯
            > /status

            Session

            Total cost:            $0.0000
            Total duration (API):  0s
            Total duration (wall): 42s
            Total code changes:    0 lines added, 0 lines removed
            Usage:                 0 input, 0 output, 0 cache read, 0 cache write

            Current session
            ██████████████████████████████████████████████████ 100% used
            Resets 7:50pm (Australia/Perth)

            Current week (all models)
            ███████████                                        22% used
            Resets Sep 8, 2am (Australia/Perth)
            """;

        var extracted = UsageOutputParser.ExtractRelevantScreen(raw, "claude");

        Assert.DoesNotContain("Tips for getting started", extracted);
        Assert.DoesNotContain("Welcome back Daniel", extracted);
        Assert.DoesNotContain("What's new", extracted);
        Assert.Contains("Session", extracted);
        Assert.Contains("Current session", extracted);
        Assert.Contains("100% used", extracted);
        Assert.Contains("Current week (all models)", extracted);
        Assert.Contains("22% used", extracted);
    }

    [Fact]
    public void ExtractRelevantScreen_ClaudeLoadingOutput_ExtractsLoadingNotice()
    {
        var raw = """
            ╭─── Claude Code v2.1.163 ────────────────────────────────────────╮
            │ Welcome back Daniel!                                            │
            ╰─────────────────────────────────────────────────────────────────╯
            > /status

            ✶ Loading your Claude Code stats…
            """;

        var extracted = UsageOutputParser.ExtractRelevantScreen(raw, "claude");

        Assert.DoesNotContain("Welcome back Daniel", extracted);
        Assert.Contains("Loading your Claude Code stats", extracted);
    }

    [Fact]
    public void ExtractRelevantScreen_CodexOutput_ExtractsLimitsOnly()
    {
        var raw = """
            [?9001h[?1004h[?2004h[?25l[2J[m[H]0;C:\WINDOWS\SYSTEM32\cmd.exe[?25h
            ╭───────────────────────────────────────╮
            │ >_ OpenAI Codex                       │
            ╰───────────────────────────────────────╯
            Model: gpt-5
            5h limit:       [====                ] 20% left (resets in 2h 15m)
            Weekly limit:   [===============     ] 75% left (resets Sat 12:00)
            """;

        var extracted = UsageOutputParser.ExtractRelevantScreen(raw, "codex");

        Assert.DoesNotContain("cmd.exe", extracted);
        Assert.Contains("Model: gpt-5", extracted);
        Assert.Contains("5h limit:", extracted);
        Assert.Contains("Weekly limit:", extracted);
    }
}
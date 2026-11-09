using System;
using System.IO;
using System.Linq;
using AgentHub.Services.Instructions;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class OrchestratorRulesTests : IDisposable
{
    // Spaces in the path on purpose: repositories often live under "OneDrive - Company".
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub rules " + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");
    private string Repo => Path.Combine(_root, "My Repo");

    public OrchestratorRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(Home, ".claude"));
        Directory.CreateDirectory(Path.Combine(Home, ".codex"));
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        Directory.CreateDirectory(Path.Combine(Repo, "src", "app"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private RulesFile[] Gather(string folder, InstructionCli? cli) =>
        [.. OrchestratorRules.Gather(folder, cli, Path.Combine(Home, ".claude"), Path.Combine(Home, ".codex"), Path.Combine(Home, ".gemini"))];

    [Fact]
    public void Gathers_the_user_wide_file_then_the_project_files_from_the_repository_root_down()
    {
        File.WriteAllText(Path.Combine(Home, ".claude", "CLAUDE.md"), "Never force push.");
        File.WriteAllText(Path.Combine(Repo, "AGENTS.md"), "Treat destructive Git operations as explicit user actions.");
        File.WriteAllText(Path.Combine(Repo, "src", "CLAUDE.md"), "Source rules.");
        File.WriteAllText(Path.Combine(Repo, "CLAUDE.md"), "   "); // empty: left out

        var files = Gather(Path.Combine(Repo, "src", "app"), InstructionCli.ClaudeCode);

        Assert.Equal(["Never force push.", "Treat destructive Git operations as explicit user actions.", "Source rules."], files.Select(f => f.Text));
        Assert.Equal("user-wide Claude Code instructions", files[0].Label);
        Assert.Equal("project AGENTS.md", files[1].Label);
        Assert.Equal($"project src{Path.DirectorySeparatorChar}CLAUDE.md", files[2].Label);
    }

    [Fact]
    public void Uses_the_worker_clis_user_wide_file_and_passes_identical_texts_once()
    {
        File.WriteAllText(Path.Combine(Home, ".claude", "CLAUDE.md"), "Claude rules.");
        File.WriteAllText(Path.Combine(Home, ".codex", "AGENTS.md"), "Codex rules.");
        File.WriteAllText(Path.Combine(Repo, "CLAUDE.md"), "Same.");
        File.WriteAllText(Path.Combine(Repo, "AGENTS.md"), "Same.");

        Assert.Equal(["Codex rules.", "Same."], Gather(Repo, InstructionCli.Codex).Select(f => f.Text));
        Assert.Equal(["Claude rules.", "Same."], Gather(Repo, null).Select(f => f.Text)); // unknown worker: Claude Code
    }

    [Fact]
    public void System_prompt_always_forbids_destructive_git_and_carries_the_files()
    {
        var bare = PaneOrchestrator.SystemPrompt(Repo, "Terminal 1", null);
        Assert.Contains("force push", bare);
        Assert.Contains("--force", bare);
        Assert.Contains("the owner asked for exactly that", bare);
        Assert.DoesNotContain("THE OWNER'S INSTRUCTION FILES", bare);

        File.WriteAllText(Path.Combine(Repo, "CLAUDE.md"), "Pull requests always target main.");
        var prompt = PaneOrchestrator.SystemPrompt(Repo, "Terminal 1", null, Gather(Repo, InstructionCli.ClaudeCode));
        Assert.Contains("THE OWNER'S INSTRUCTION FILES", prompt);
        Assert.Contains("Pull requests always target main.", prompt);
        Assert.Contains(Path.Combine(Repo, "CLAUDE.md"), prompt);
    }
}

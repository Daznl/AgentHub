using System;
using System.IO;
using System.Linq;
using AgentHub.Services.Instructions;
using Xunit;

namespace AgentHub.Tests;

public class InstructionFilesTests : IDisposable
{
    // Spaces in the path on purpose: repositories often live under "OneDrive - Company".
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub instructions " + Guid.NewGuid().ToString("N"));

    public InstructionFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("claude", InstructionCli.ClaudeCode)]
    [InlineData("claude --model opus", InstructionCli.ClaudeCode)]
    [InlineData("\"C:\\Program Files\\Codex\\codex.exe\" resume 1", InstructionCli.Codex)]
    [InlineData("agy", InstructionCli.Gemini)]
    [InlineData("gemini.cmd", InstructionCli.Gemini)]
    [InlineData("powershell.exe -NoLogo", null)]
    [InlineData("", null)]
    public void DetectCli_ReadsTheExecutableName(string command, InstructionCli? expected) =>
        Assert.Equal(expected, InstructionFiles.DetectCli(command));

    [Fact]
    public void GlobalFiles_PointAtEachCliHome_AndPreferCodexOverride()
    {
        var files = InstructionFiles.GlobalFiles(Path.Combine(_root, ".claude"), Path.Combine(_root, ".codex"), Path.Combine(_root, ".gemini"));
        Assert.Equal(Path.Combine(_root, ".claude", "CLAUDE.md"), files[0].Path);
        Assert.Equal(Path.Combine(_root, ".codex", "AGENTS.md"), files[1].Path);
        Assert.Equal(Path.Combine(_root, ".gemini", "GEMINI.md"), files[2].Path);

        Directory.CreateDirectory(Path.Combine(_root, ".codex"));
        File.WriteAllText(Path.Combine(_root, ".codex", "AGENTS.override.md"), "x");
        files = InstructionFiles.GlobalFiles(Path.Combine(_root, ".claude"), Path.Combine(_root, ".codex"), Path.Combine(_root, ".gemini"));
        Assert.Equal(Path.Combine(_root, ".codex", "AGENTS.override.md"), files[1].Path);
    }

    [Fact]
    public void RepoFiles_AlwaysListTheThreeRootFiles_AndOptionalClaudeFilesOnlyWhenPresent()
    {
        Assert.Equal(["CLAUDE.md", "AGENTS.md", "GEMINI.md"], InstructionFiles.RepoFiles(_root).Select(f => f.Label));

        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "CLAUDE.md"), "x");
        File.WriteAllText(Path.Combine(_root, "CLAUDE.local.md"), "x");
        Assert.Equal(["CLAUDE.md", ".claude\\CLAUDE.md", "CLAUDE.local.md", "AGENTS.md", "GEMINI.md"],
            InstructionFiles.RepoFiles(_root).Select(f => f.Label));
    }

    [Fact]
    public void Save_KeepsLineEndingsAndByteOrderMark()
    {
        var lf = Path.Combine(_root, "lf.md");
        File.WriteAllText(lf, "# A\n- one\n");
        var loaded = InstructionFiles.Load(lf);
        Assert.Equal("# A\r\n- one\r\n", loaded.Text);
        Assert.Equal(SaveOutcome.Saved, InstructionFiles.Save(lf, loaded.Text + "- two\r\n", loaded, false, out _));
        Assert.Equal("# A\n- one\n- two\n", File.ReadAllText(lf));

        var bom = Path.Combine(_root, "bom.md");
        File.WriteAllBytes(bom, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n']);
        loaded = InstructionFiles.Load(bom);
        InstructionFiles.Save(bom, "a\r\nb", loaded, false, out _);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b' }, File.ReadAllBytes(bom));
    }

    [Fact]
    public void Save_RefusesWhenTheFileChangedSinceLoad_UnlessOverwriting()
    {
        var path = Path.Combine(_root, "CLAUDE.md");
        File.WriteAllText(path, "mine\n");
        var loaded = InstructionFiles.Load(path);
        File.WriteAllText(path, "someone else's\n");

        Assert.Equal(SaveOutcome.ChangedOnDisk, InstructionFiles.Save(path, "edited", loaded, false, out _));
        Assert.Equal("someone else's\n", File.ReadAllText(path));

        Assert.Equal(SaveOutcome.Saved, InstructionFiles.Save(path, "edited", loaded, true, out var saved));
        Assert.Equal("edited", File.ReadAllText(path));
        // The returned state matches disk, so the next save goes straight through.
        Assert.Equal(SaveOutcome.Saved, InstructionFiles.Save(path, "again", saved, false, out _));
        Assert.Empty(Directory.GetFiles(_root, "*.agenthub-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Save_CreatesAMissingFileAndItsFolder_ButNotOverAFileCreatedMeanwhile()
    {
        var path = Path.Combine(_root, "new home", "GEMINI.md");
        var loaded = InstructionFiles.Load(path);
        Assert.False(loaded.Exists);
        Assert.Equal(SaveOutcome.Saved, InstructionFiles.Save(path, "# Rules\r\n", loaded, false, out _));
        Assert.Equal("# Rules\n", File.ReadAllText(path));

        var other = Path.Combine(_root, "AGENTS.md");
        var missing = InstructionFiles.Load(other);
        File.WriteAllText(other, "created elsewhere");
        Assert.Equal(SaveOutcome.ChangedOnDisk, InstructionFiles.Save(other, "mine", missing, false, out _));
    }
}

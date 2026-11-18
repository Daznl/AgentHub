using System;
using System.IO;
using System.Linq;
using System.Text;
using AgentHub.Services.World;
using Xunit;

namespace AgentHub.Tests;

public class TranscriptActionsTests
{
    private const string Ts = "\"timestamp\":\"2026-11-18T06:00:00.000Z\"";

    [Fact]
    public void Claude_tool_calls_become_reads_edits_writes_and_commands()
    {
        var line = "{\"type\":\"assistant\"," + Ts + ",\"message\":{\"content\":[" +
                   "{\"type\":\"text\",\"text\":\"Looking at the file.\"}," +
                   "{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"C:\\\\repo\\\\src\\\\a.cs\"}}," +
                   "{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"C:\\\\repo\\\\src\\\\a.cs\",\"old_string\":\"x\"}}," +
                   "{\"type\":\"tool_use\",\"name\":\"Write\",\"input\":{\"file_path\":\"C:\\\\repo\\\\b.md\"}}," +
                   "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"git commit -m 'feat: x'\"}}," +
                   "{\"type\":\"tool_use\",\"name\":\"mcp__claude_ai_Snowflake__query\",\"input\":{}}]}}";
        var actions = TranscriptActions.ParseClaudeLine(line);
        Assert.Equal(["reply", "read", "edit", "write", "commit", "snowflake"], actions.Select(a => a.Kind).ToArray());
        Assert.Equal(@"C:\repo\src\a.cs", actions[1].Path);
        Assert.Equal("Edit a.cs", actions[2].Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 6, 0, 0, TimeSpan.Zero), actions[0].Time);
    }

    [Fact]
    public void Claude_prompts_count_but_tool_results_and_meta_lines_do_not()
    {
        Assert.Equal("prompt", Assert.Single(TranscriptActions.ParseClaudeLine(
            "{\"type\":\"user\"," + Ts + ",\"message\":{\"content\":\"launch it for me\"}}")).Kind);
        Assert.Empty(TranscriptActions.ParseClaudeLine(
            "{\"type\":\"user\"," + Ts + ",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}"));
        Assert.Empty(TranscriptActions.ParseClaudeLine(
            "{\"type\":\"user\",\"isMeta\":true," + Ts + ",\"message\":{\"content\":\"<command-name>x</command-name>\"}}"));
        Assert.Empty(TranscriptActions.ParseClaudeLine("not json"));
        Assert.Empty(TranscriptActions.ParseClaudeLine("{\"type\":\"summary\"}"));
    }

    [Fact]
    public void Codex_shell_calls_patches_and_prompts_are_read()
    {
        var shell = "{" + Ts + ",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"shell\"," +
                    "\"arguments\":\"{\\\"command\\\":[\\\"bash\\\",\\\"-lc\\\",\\\"git push origin feature/x\\\"]}\"}}";
        Assert.Equal("push", Assert.Single(TranscriptActions.ParseCodexLine(shell)).Kind);

        var patch = "{" + Ts + ",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"apply_patch\"," +
                    "\"input\":\"*** Begin Patch\\n*** Update File: src/a.cs\\n@@\\n*** Add File: docs/new.md\\n*** End Patch\"}}";
        var edits = TranscriptActions.ParseCodexLine(patch);
        Assert.Equal(["edit", "write"], edits.Select(a => a.Kind).ToArray());
        Assert.Equal("src/a.cs", edits[0].Path);
        Assert.Equal("docs/new.md", edits[1].Path);

        var prompt = "{" + Ts + ",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"fix the build\"}}";
        Assert.Equal("prompt", Assert.Single(TranscriptActions.ParseCodexLine(prompt)).Kind);
    }

    [Theory]
    [InlineData("git -C \"C:\\x y\" commit -m wip", "commit")]
    [InlineData("git push -u origin feature/world-3d", "push")]
    [InlineData("gh pr merge 23 --merge", "merge")]
    [InlineData("git merge origin/main", "merge")]
    [InlineData("gh pr create --base main", "pr")]
    [InlineData("git worktree add -b feature/x C:\\wt\\x origin/main", "branch")]
    [InlineData("snow sql -q \"select 1\"", "snowflake")]
    [InlineData("az webapp deploy --name app", "azure")]
    [InlineData("New-AzResourceGroupDeployment -TemplateFile main.bicep", "azure")]
    [InlineData("Invoke-RestMethod https://example.com", "web")]
    [InlineData("dotnet test tests\\AgentHub.Tests --no-build", "test")]
    [InlineData("dotnet build AgentHub.sln -c Release", "build")]
    [InlineData("Get-ChildItem src", "bash")]
    [InlineData("echo azure is a colour", "bash")]
    public void Commands_are_classified(string command, string kind) =>
        Assert.Equal(kind, TranscriptActions.Command(DateTimeOffset.UnixEpoch, command).Kind);

    [Fact]
    public void Tail_reads_only_appended_whole_lines_and_starts_near_the_end()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenthub tail " + Guid.NewGuid().ToString("N")[..6] + ".jsonl");
        try
        {
            File.WriteAllText(path, string.Concat(Enumerable.Range(0, 100).Select(i => $"old line {i}\n")));
            var tail = new TranscriptTail();
            var first = tail.Read(path, backlog: 40);
            Assert.True(first.Count is > 0 and < 5);
            Assert.Equal("old line 99", first[^1]);
            Assert.DoesNotContain(first, l => !l.StartsWith("old line ", StringComparison.Ordinal)); // no half line from the backlog cut

            using (var w = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = Encoding.UTF8.GetBytes("new one\nhalf");
                w.Write(bytes, 0, bytes.Length);
            }
            Assert.Equal(["new one"], tail.Read(path).ToArray());
            File.AppendAllText(path, " written\n");
            Assert.Equal(["half written"], tail.Read(path).ToArray());
            Assert.Empty(tail.Read(path));

            File.WriteAllText(path, "replaced\n"); // shorter: read again from the start
            Assert.Equal(["replaced"], tail.Read(path).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

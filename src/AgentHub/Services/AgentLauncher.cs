using System.Diagnostics;
using AgentHub.Models;

namespace AgentHub.Services;

public sealed class AgentLauncher
{
    public void Launch(AgentDefinition agent, string repoPath, bool preferWindowsTerminal)
    {
        EnsureHandoffFile(repoPath);

        if (preferWindowsTerminal && CommandExists("wt.exe"))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wt.exe",
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("new-tab");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(repoPath);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add($"AgentHub · {agent.Name} · {Path.GetFileName(repoPath)}");
            psi.ArgumentList.Add("powershell.exe");
            psi.ArgumentList.Add("-NoExit");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(BuildPowerShellCommand(agent));
            Process.Start(psi);
            return;
        }

        var fallback = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoPath,
            UseShellExecute = true,
            Arguments = $"-NoExit -Command \"{EscapeForQuotedPowerShell(BuildPowerShellCommand(agent))}\""
        };
        Process.Start(fallback);
    }

    private static string BuildPowerShellCommand(AgentDefinition agent)
    {
        var command = agent.Command.Trim();
        var args = agent.Arguments.Trim();
        return string.IsNullOrWhiteSpace(args) ? command : $"{command} {args}";
    }

    private static string EscapeForQuotedPowerShell(string value) => value.Replace("\"", "`\"");

    private static bool CommandExists(string command)
    {
        try
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            return paths.Any(p => File.Exists(Path.Combine(p.Trim(), command)));
        }
        catch { return false; }
    }

    private static void EnsureHandoffFile(string repoPath)
    {
        var dir = Path.Combine(repoPath, ".agenthub");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "handoff.md");
        if (File.Exists(file)) return;

        File.WriteAllText(file,
            "# Agent Handoff\n\n" +
            "Use this file when handing work between Codex, Claude Code, and Antigravity.\n\n" +
            "## Current task\n\n- Describe the active task here.\n\n" +
            "## Completed\n\n- Nothing recorded yet.\n\n" +
            "## Next steps\n\n- Add the next concrete action.\n\n" +
            "## Relevant files\n\n- Add important paths here.\n");
    }
}

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// A fact that talks to a real installed CLI. Reported as skipped, not failed, on machines (and CI) where
/// <paramref name="command"/> is not on PATH. With <paramref name="optIn"/>, also skipped unless that environment variable
/// is 1: for tests that spend the user's model usage on every run.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(string command, string? optIn = null)
    {
        if (!IsOnPath(command))
            Skip = $"'{command}' is not on PATH; live CLI test skipped.";
        else if (optIn is not null && Environment.GetEnvironmentVariable(optIn) != "1")
            Skip = $"Set {optIn}=1 to run this live test; it uses model usage.";
    }

    private static bool IsOnPath(string command)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend("");
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir.Trim('"'), command + ext))));
    }
}

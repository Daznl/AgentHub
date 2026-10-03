using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentHub.Models;

/// <summary>
/// A saved SSH target, launched in a Cockpit pane with the user's own OpenSSH client.
/// Only the address is stored: passwords and keys stay with ssh / ssh-agent, never AgentHub.
/// </summary>
public sealed partial class SshConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Optional friendly label, e.g. "Desktop PC". Falls back to the target.</summary>
    public string? Name { get; set; }

    /// <summary>What follows <c>ssh</c>: <c>user@host</c> or <c>user@host:port</c>.</summary>
    public string Target { get; set; } = "";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Target : $"{Name} ({Target})";

    // [user@]host[:port]. Host is a DNS name or IPv4 address. Restricting the character set also
    // keeps the target safe to place on the PowerShell command line.
    [GeneratedRegex(@"^(?:(?<user>[A-Za-z0-9._-]+)@)?(?<host>[A-Za-z0-9.-]+)(?::(?<port>\d{1,5}))?$")]
    private static partial Regex TargetPattern();

    /// <summary>Returns null when the target is valid, otherwise a message describing what is wrong.</summary>
    public static string? Validate(string target)
    {
        var match = TargetPattern().Match(target.Trim());
        if (!match.Success)
            return "Enter the address as user@host or user@host:port, for example daniel@192.168.8.186.";
        if (match.Groups["port"].Success && (!int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535))
            return "The port must be between 1 and 65535.";
        return null;
    }

    /// <summary>
    /// PowerShell command line that runs ssh and then leaves a local prompt open, so disconnecting
    /// drops back into PowerShell in the same pane instead of ending the session.
    /// </summary>
    public string BuildCommandLine()
    {
        var match = TargetPattern().Match(Target.Trim());
        var user = match.Groups["user"].Success ? match.Groups["user"].Value + "@" : "";
        var host = match.Groups["host"].Value;
        var portArg = match.Groups["port"].Success ? $"-p {match.Groups["port"].Value} " : "";
        return $"powershell.exe -NoLogo -NoExit -Command ssh {portArg}{user}{host}";
    }

    public override string ToString() => DisplayName;
}

using System.Text.Json.Serialization;

namespace AgentHub.Models;

/// <summary>
/// One account known to the local GitHub CLI, as reported by <c>gh auth status --json hosts</c>.
/// AgentHub never sees the token itself; it only learns which logins exist and which is active.
/// </summary>
public sealed class GitHubAccount
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "github.com";

    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("active")]
    public bool IsActive { get; set; }

    /// <summary>"success" when gh could validate the token, otherwise an error/timeout state.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("tokenSource")]
    public string? TokenSource { get; set; }

    [JsonPropertyName("gitProtocol")]
    public string? GitProtocol { get; set; }

    [JsonPropertyName("scopes")]
    public string? Scopes { get; set; }

    public bool IsHealthy => string.Equals(State, "success", StringComparison.OrdinalIgnoreCase);

    public string DisplayName
    {
        get
        {
            var host = string.Equals(Host, "github.com", StringComparison.OrdinalIgnoreCase) ? "" : $" ({Host})";
            var health = IsHealthy ? "" : " ⚠";
            return $"@{Login}{host}{health}";
        }
    }

    public override string ToString() => DisplayName;
}

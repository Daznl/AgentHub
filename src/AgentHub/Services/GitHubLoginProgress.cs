namespace AgentHub.Services;

/// <summary>
/// Progress emitted while <c>gh auth login --web</c> runs non-interactively.
/// Once <see cref="OneTimeCode"/> and <see cref="VerificationUrl"/> are known the user
/// completes the real GitHub device-flow verification in their browser.
/// </summary>
public sealed record GitHubLoginProgress(string? OneTimeCode, string? VerificationUrl, string RawLine);

public sealed record GitHubLoginResult(bool Succeeded, string? Login, string? Error);

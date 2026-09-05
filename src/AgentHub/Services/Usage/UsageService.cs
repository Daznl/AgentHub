using System.ComponentModel;
using AgentHub.Models;

namespace AgentHub.Services.Usage;

public sealed class UsageService(ProcessRunner runner)
{
    public async Task<IReadOnlyList<UsageSnapshot>> RefreshAsync(
        IEnumerable<UsageProviderDefinition> providers,
        CancellationToken cancellationToken = default)
    {
        var tasks = providers.Where(provider => provider.Enabled)
            .Select(provider => CollectSafelyAsync(provider, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<UsageSnapshot> CollectSafelyAsync(
        UsageProviderDefinition provider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.Command))
            return Failure(provider, provider.Command, UsageCollectionStatus.NotInstalled, "No command configured.");

        try
        {
            // Codex and Claude both expose usage from clean on-disk sources rather than
            // fragile interactive terminal scraping.
            if (provider.Id.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                provider.Command.Contains("codex", StringComparison.OrdinalIgnoreCase))
            {
                return await Task.Run(() => CodexUsageReader.Read(provider), cancellationToken);
            }

            if (provider.Id.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
                provider.Command.Contains("claude", StringComparison.OrdinalIgnoreCase))
            {
                return await ClaudeUsageReader.ReadAsync(provider, cancellationToken: cancellationToken);
            }

            // Everything else (e.g. Antigravity `agy -p /usage`) prints usage non-interactively.
            var sourceDescription = $"{provider.Command} {string.Join(' ', provider.Arguments)}".Trim();
            var result = await runner.RunAsync(
                provider.Command,
                provider.Arguments,
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(15));

            if (result.ExitCode == -1)
                return Failure(provider, sourceDescription, UsageCollectionStatus.TimedOut, result.StdErr, result.StdOut);

            var rawOutput = string.Join(Environment.NewLine,
                new[] { result.StdOut, result.StdErr }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(rawOutput))
            {
                var msg = FirstUsefulLine(result.StdErr) ?? FirstUsefulLine(result.StdOut) ?? $"Command exited with code {result.ExitCode}.";
                return Failure(provider, sourceDescription, UsageCollectionStatus.Failed, msg, rawOutput);
            }

            var limits = UsageOutputParser.Parse(rawOutput, provider);

            if (limits.Count == 0)
            {
                return Failure(provider, sourceDescription, UsageCollectionStatus.UnsupportedOutput,
                    "No recognizable usage limits found in output.", rawOutput);
            }

            return new UsageSnapshot(
                provider.Id,
                provider.Name,
                DateTimeOffset.Now,
                limits,
                sourceDescription,
                UsageCollectionStatus.Available,
                RawOutput: rawOutput);
        }
        catch (Win32Exception)
        {
            return Failure(provider, provider.Command, UsageCollectionStatus.NotInstalled,
                $"{provider.Command} was not found on PATH.");
        }
        catch (Exception ex)
        {
            return Failure(provider, provider.Command, UsageCollectionStatus.Failed, ex.Message);
        }
    }

    private static UsageSnapshot Failure(
        UsageProviderDefinition provider,
        string sourceDescription,
        UsageCollectionStatus status,
        string? error,
        string? rawOutput = null) =>
        new(provider.Id, provider.Name, DateTimeOffset.Now, [], sourceDescription, status,
            string.IsNullOrWhiteSpace(error) ? "Usage refresh failed." : error.Trim(),
            rawOutput);

    private static string? FirstUsefulLine(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();
}

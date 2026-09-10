using System.Diagnostics;
using System.Text;

namespace AgentHub.Services;

public sealed class ProcessRunner
{
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // gh and git emit UTF-8; without this .NET decodes with the console codepage and em dashes etc. become mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(linkedCts.Token);

            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            var timeoutText = timeout ?? TimeSpan.FromSeconds(30);
            return (-1, stdout.ToString(), $"Process execution timed out after {timeoutText.TotalSeconds:g} seconds.");
        }
    }

    /// <summary>
    /// Like <see cref="RunAsync"/> but invokes <paramref name="onLine"/> for every stdout/stderr line
    /// as it arrives, so callers can react to long-running processes (e.g. a device-code login) before exit.
    /// Callbacks run on a thread-pool thread; marshal to the UI thread if needed.
    /// </summary>
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string> onLine,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // gh and git emit UTF-8; without this .NET decodes with the console codepage and em dashes etc. become mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onLine(e.Data);
        };

        try
        {
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(linkedCts.Token);

            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            var reason = cancellationToken.IsCancellationRequested
                ? "Process was cancelled."
                : $"Process execution timed out after {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:g} seconds.";
            return (-1, stdout.ToString(), reason);
        }
    }
}

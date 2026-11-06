namespace AgentHub.Hosting;

/// <summary>
/// File writes that survive another process holding the file for a moment. <see cref="Replace"/> puts a finished temp
/// file in place of the target in one step, so a reader never sees half a file. Windows refuses the replace (access
/// denied, or a sharing violation) while another process has either file open, which antivirus and the search indexer
/// do for a moment after every write; so it retries for about a second before giving up. Appends and shared reads
/// follow the same rule.
/// 2026-11-06: the test suite failed on this in most full runs (run.json, steering.json), and a lost run.json write in
/// the live app is a lost record.
/// </summary>
public static class SharedFile
{
    private const int Attempts = 10;

    public static void Replace(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(temp, target, overwrite: true); return; }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex)) { Thread.Sleep(Backoff(attempt)); }
        }
    }

    public static async Task ReplaceAsync(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(temp, target, overwrite: true); return; }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex)) { await Task.Delay(Backoff(attempt)); }
        }
    }

    /// <summary>
    /// Appends text to a log-style file. Only the open is retried (a file someone else holds for a moment), never the
    /// write, so a retry can never append a line twice.
    /// </summary>
    public static async Task AppendAsync(string path, string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex)) { await Task.Delay(Backoff(attempt)); continue; }
            await using (stream)
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(text);
            return;
        }
    }

    /// <summary>Reads a file line by line without blocking a writer that is appending to it at the same time.</summary>
    public static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>A file held open by someone else; a missing file or folder is a real error and is not retried.</summary>
    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException || ex is IOException and not FileNotFoundException and not DirectoryNotFoundException;

    /// <summary>10, 20, 40, 80, then 160 ms per attempt: about 1.2 s in all.</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(10 << Math.Min(attempt - 1, 4));
}

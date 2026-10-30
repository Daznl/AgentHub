using System.Windows.Threading;
using AgentHub.Services.Context;

namespace AgentHub.Services.History;

/// <summary>What one archive pass did.</summary>
public sealed record ArchiveResult(int Copied, int Appended, int Superseded, long BytesWritten, int Failed)
{
    public static readonly ArchiveResult Empty = new(0, 0, 0, 0, 0);

    public ArchiveResult Add(ArchiveResult other) => new(Copied + other.Copied, Appended + other.Appended,
        Superseded + other.Superseded, BytesWritten + other.BytesWritten, Failed + other.Failed);
}

/// <summary>
/// Keeps an AgentHub-owned copy of every coding CLI transcript so history outlives the CLIs' own cleanup
/// (Claude Code deletes sessions after <c>cleanupPeriodDays</c>), reinstalls and format changes.
/// <para>Layout: <c>&lt;root&gt;\claude</c> mirrors <c>~/.claude/projects</c> (transcripts, sub-agent transcripts,
/// persisted tool output) and <c>&lt;root&gt;\codex</c> mirrors <c>~/.codex/sessions</c>.</para>
/// <para>Nothing is ever deleted from the archive. Transcripts are append-only, so a grown <c>.jsonl</c> has only its
/// new bytes appended. If a transcript was rewritten instead, the archived version is kept beside the new copy as
/// <c>&lt;name&gt;.superseded-&lt;utc time&gt;.jsonl</c>. Other files (metadata, notes) are replaced when they change.</para>
/// </summary>
public static class HistoryArchiver
{
    public const string DefaultRoot = @"C:\data\agenthub\history";

    /// <summary>Bytes compared just before the archived length to confirm the source only grew.</summary>
    private const int VerifyBytes = 4096;

    public static string ClaudeArchiveDir(string root) => Path.Combine(root, "claude");
    public static string CodexArchiveDir(string root) => Path.Combine(root, "codex");

    /// <summary>Archives both CLIs' transcript folders into <paramref name="root"/>.</summary>
    public static ArchiveResult SyncAll(string root) =>
        Sync(SessionContextReader.ClaudeProjectsDir, ClaudeArchiveDir(root))
            .Add(Sync(SessionContextReader.CodexSessionsDir, CodexArchiveDir(root)));

    /// <summary>Brings <paramref name="archiveDir"/> up to date with every file under <paramref name="sourceDir"/>.</summary>
    public static ArchiveResult Sync(string sourceDir, string archiveDir)
    {
        var result = ArchiveResult.Empty;
        if (!Directory.Exists(sourceDir)) return result;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sourceDir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result with { Failed = 1 };
        }

        foreach (var source in files)
        {
            var target = Path.Combine(archiveDir, Path.GetRelativePath(sourceDir, source));
            try
            {
                result = result.Add(SyncFile(source, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or vanished mid-pass; picked up next time.
                result = result.Add(ArchiveResult.Empty with { Failed = 1 });
            }
        }
        return result;
    }

    private static ArchiveResult SyncFile(string source, string target)
    {
        var src = new FileInfo(source);
        var dst = new FileInfo(target);
        var srcWrite = src.LastWriteTimeUtc;

        if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == srcWrite)
            return ArchiveResult.Empty;

        Directory.CreateDirectory(dst.DirectoryName!);
        var isTranscript = string.Equals(src.Extension, ".jsonl", StringComparison.OrdinalIgnoreCase);

        if (!dst.Exists)
        {
            var written = CopyRange(source, target, 0, append: false);
            File.SetLastWriteTimeUtc(target, srcWrite);
            return new ArchiveResult(1, 0, 0, written, 0);
        }

        if (isTranscript)
        {
            if (dst.Length == src.Length && SameRange(source, target, Math.Max(0, dst.Length - VerifyBytes), dst.Length))
            {
                File.SetLastWriteTimeUtc(target, srcWrite);
                return ArchiveResult.Empty;
            }
            if (src.Length > dst.Length && SameRange(source, target, Math.Max(0, dst.Length - VerifyBytes), dst.Length))
            {
                var appended = CopyRange(source, target, dst.Length, append: true);
                File.SetLastWriteTimeUtc(target, srcWrite);
                return new ArchiveResult(0, 1, 0, appended, 0);
            }

            // Rewritten or shrunk: never lose what was archived.
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var kept = Path.Combine(dst.DirectoryName!, $"{Path.GetFileNameWithoutExtension(target)}.superseded-{stamp}{dst.Extension}");
            File.Move(target, kept);
            var copied = CopyRange(source, target, 0, append: false);
            File.SetLastWriteTimeUtc(target, srcWrite);
            return new ArchiveResult(0, 0, 1, copied, 0);
        }

        var replaced = CopyRange(source, target, 0, append: false);
        File.SetLastWriteTimeUtc(target, srcWrite);
        return new ArchiveResult(1, 0, 0, replaced, 0);
    }

    // The CLI may be writing the source, so it is opened shared and only its length at open time is copied.
    private static long CopyRange(string source, string target, long from, bool append)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        var length = input.Length;
        input.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[1024 * 1024];
        long written = 0;
        while (written < length - from)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - from - written));
            if (read == 0) break;
            output.Write(buffer, 0, read);
            written += read;
        }
        return written;
    }

    private static bool SameRange(string a, string b, long from, long to)
    {
        var count = (int)(to - from);
        if (count == 0) return true;
        return ReadRange(a, from, count).AsSpan().SequenceEqual(ReadRange(b, from, count));
    }

    private static byte[] ReadRange(string path, long from, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(from, SeekOrigin.Begin);
        var bytes = new byte[count];
        var read = stream.ReadAtLeast(bytes, count, throwOnEndOfStream: false);
        return read == count ? bytes : bytes[..read];
    }
}

/// <summary>Runs <see cref="HistoryArchiver.SyncAll"/> at startup and then on an interval, never two passes at once.</summary>
public sealed class HistoryArchiveService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly string _root;
    private bool _running;

    /// <summary>Raised on the UI thread after each pass.</summary>
    public event Action<ArchiveResult>? Synced;

    public HistoryArchiveService(string root)
    {
        _root = root;
        _timer.Tick += async (_, _) => await RunAsync();
    }

    public string Root => _root;

    public void Start()
    {
        _timer.Start();
        _ = RunAsync();
    }

    public async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            var result = await Task.Run(() => HistoryArchiver.SyncAll(_root));
            Synced?.Invoke(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Synced?.Invoke(ArchiveResult.Empty with { Failed = 1 });
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose() => _timer.Stop();
}

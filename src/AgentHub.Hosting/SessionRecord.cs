using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHub.Hosting;

/// <summary>
/// One hosted session as both sides know it: <c>C:\data\agenthub\sessions\&lt;id&gt;\session.json</c>. AgentHub writes it
/// before starting the host (what to run, where, how big); the host fills in its own process id and the CLI's, then the
/// exit code; AgentHub adds the transcript path once the context badge has found it.
/// </summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    /// <summary>The pane title the session was started with, e.g. "Terminal 2 · Claude Code".</summary>
    public string Title { get; set; } = "";
    /// <summary>The repository or place shown in the pane header, e.g. "AgentHub" or "SSH · build box".</summary>
    public string SessionName { get; set; } = "";
    public bool IsAgent { get; set; }
    public int Cols { get; set; } = 120;
    public int Rows { get; set; } = 30;
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Process id of the AgentHub.SessionHost process, once it runs.</summary>
    public int HostPid { get; set; }
    /// <summary>Process id of the CLI inside the pseudo-console, once it runs.</summary>
    public int ProcessId { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset? ExitedAt { get; set; }
    /// <summary>Why the host could not run the session, when it could not.</summary>
    public string? Error { get; set; }

    /// <summary>Transcript the pane's context badge bound to, so a reattached pane finds it again at once.</summary>
    public string? TranscriptPath { get; set; }

    /// <summary>
    /// Stream offset of the first byte of <c>replay.bin</c>, written by the host when the CLI exits (the ring dropped what
    /// came before). Lets AgentHub lay a saved screen and the rest of the replay end to end. -1 until the host has exited.
    /// </summary>
    public long ReplayStart { get; set; } = -1;

    [JsonIgnore]
    public bool HasExited => ExitCode is not null || Error is not null;
}

/// <summary>
/// The terminal exactly as a pane showed it when AgentHub let go of the session: the terminal's own state (scrollback,
/// colours, cursor) serialised as escape sequences, the size it had, and the stream offset it had consumed. Written by
/// AgentHub to <c>screen.json</c> on close; on the next attach the pane restores it and plays only the output after
/// <see cref="Offset"/>, so what was on screen comes back as it was. Nothing the host reads.
/// </summary>
public sealed class ScreenSnapshot
{
    public long Offset { get; set; } = -1;
    public int Cols { get; set; }
    public int Rows { get; set; }
    public string Screen { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>Where session records live and how both processes find each other through them.</summary>
public sealed class SessionStore
{
    public const string DefaultRoot = @"C:\data\agenthub\sessions";
    public const string HostProcessName = "AgentHub.SessionHost";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public SessionStore(string? root = null) => Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;

    public string Root { get; }

    public static string NewId() => DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];

    public static string PipeName(string id) => "agenthub-session-" + id;

    public string Directory(string id) => Path.Combine(Root, id);
    public string RecordPath(string id) => Path.Combine(Directory(id), "session.json");
    /// <summary>The replay buffer as it was when the CLI exited, so an ended session can still show its last screen.</summary>
    public string ReplayPath(string id) => Path.Combine(Directory(id), "replay.bin");
    public string LogPath(string id) => Path.Combine(Directory(id), "host.log");
    /// <summary>The pane's terminal state when AgentHub last let go of the session (<see cref="ScreenSnapshot"/>).</summary>
    public string ScreenPath(string id) => Path.Combine(Directory(id), "screen.json");

    public void SaveScreen(string id, ScreenSnapshot screen)
    {
        System.IO.Directory.CreateDirectory(Directory(id));
        var path = ScreenPath(id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(screen, JsonOptions));
        SharedFile.Replace(temp, path);
    }

    public ScreenSnapshot? LoadScreen(string id)
    {
        var path = ScreenPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ScreenSnapshot>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(SessionRecord record)
    {
        System.IO.Directory.CreateDirectory(Directory(record.Id));
        var path = RecordPath(record.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions));
        SharedFile.Replace(temp, path);
    }

    public SessionRecord? Load(string id)
    {
        var path = RecordPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Every record on disk, newest start first.</summary>
    public List<SessionRecord> ListAll()
    {
        if (!System.IO.Directory.Exists(Root)) return [];
        var records = new List<SessionRecord>();
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
        {
            var record = Load(Path.GetFileName(dir));
            if (record is not null) records.Add(record);
        }
        return records.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>Removes a session's folder (record, replay, log). Only for sessions whose host is gone.</summary>
    public void Delete(string id)
    {
        var dir = Directory(id);
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
    }

    /// <summary>True when the record's host process still runs (and is a session host, not a reused process id).</summary>
    public static bool IsHostAlive(SessionRecord record)
    {
        if (record.HostPid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(record.HostPid);
            return !process.HasExited && string.Equals(process.ProcessName, HostProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

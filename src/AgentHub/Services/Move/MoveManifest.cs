using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHub.Services.Move;

/// <summary>
/// <c>manifest.json</c> at the root of a move package: where it came from, what it holds, and the source computer's
/// folders so import can map paths. Files sit under <c>items/&lt;key&gt;/</c>, relative to that item's folder.
/// </summary>
public sealed class MoveManifest
{
    public const string FormatName = "agenthub-move";
    public const int CurrentVersion = 1;
    public const string EntryName = "manifest.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset CreatedUtc { get; set; }
    public string Machine { get; set; } = "";
    public string UserName { get; set; } = "";
    public string UserProfile { get; set; } = "";
    public string? AppVersion { get; set; }
    /// <summary>Each item's folder on the source computer, by item key.</summary>
    public Dictionary<string, string> Locations { get; set; } = [];
    public List<MoveManifestItem> Items { get; set; } = [];
    /// <summary>The registered repositories, so import can say which are missing and clone them.</summary>
    public List<MoveRepository> Repositories { get; set; } = [];
    /// <summary>Claude Code MCP servers by name only: their settings can hold keys, so they are added again by hand.</summary>
    public List<string> McpServers { get; set; } = [];
    /// <summary>Files that could not be read (locked, vanished) and are not in the package.</summary>
    public List<MoveSkipped> Skipped { get; set; } = [];

    public static string ItemPrefix(string key) => $"items/{key}/";
}

public sealed record MoveManifestItem(string Key, int Files, long Bytes);

public sealed record MoveRepository(string Name, string LocalPath, string? RemoteUrl);

public sealed record MoveSkipped(string Path, string Reason);

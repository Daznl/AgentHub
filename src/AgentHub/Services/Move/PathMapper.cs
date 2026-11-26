using System.Text;
using System.Text.RegularExpressions;
using AgentHub.Services.Context;

namespace AgentHub.Services.Move;

/// <summary>One folder on the old computer and where it is on this one.</summary>
public sealed record PathMapping(string From, string To)
{
    public bool IsChange => !string.Equals(Normalise(From), Normalise(To), StringComparison.OrdinalIgnoreCase);

    public static string Normalise(string path) => path.Trim().TrimEnd('\\', '/');
}

/// <summary>
/// Rewrites paths from the old computer to this one: inside files (JSON-escaped <c>C:\\Users\\a</c>, plain
/// <c>C:\Users\a</c> and <c>C:/Users/a</c>) and in Claude Code's encoded project folder names
/// (<c>C--Users-a-...</c>). A path is only matched as a whole folder: <c>C:\Users\dan</c> never rewrites
/// <c>C:\Users\daniel</c>.
/// </summary>
public sealed class PathMapper
{
    private readonly List<(Regex Pattern, string Replacement)> _text = [];
    private readonly List<(string From, string To)> _encoded = [];

    public IReadOnlyList<PathMapping> Mappings { get; }

    public bool IsEmpty => Mappings.Count == 0;

    public PathMapper(IEnumerable<PathMapping> mappings)
    {
        // Longest first, so C:\Users\a\Repos maps before C:\Users\a when both are listed.
        Mappings = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.From) && !string.IsNullOrWhiteSpace(m.To) && m.IsChange)
            .Select(m => new PathMapping(PathMapping.Normalise(m.From), PathMapping.Normalise(m.To)))
            .DistinctBy(m => m.From, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(m => m.From.Length)
            .ToList();

        foreach (var m in Mappings)
        {
            // JSON-escaped (\\), plain (\) and forward-slash (/) spellings, each replaced in kind.
            AddText(m.From.Replace(@"\", @"\\"), m.To.Replace(@"\", @"\\"));
            AddText(m.From, m.To);
            AddText(m.From.Replace('\\', '/'), m.To.Replace('\\', '/'));
            _encoded.Add((SessionContextReader.EncodeClaudeProjectName(m.From), SessionContextReader.EncodeClaudeProjectName(m.To)));
        }
    }

    private void AddText(string from, string to)
    {
        if (_text.Any(t => t.Pattern.ToString() == Pattern(from))) return;
        _text.Add((new Regex(Pattern(from), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), to));
    }

    // Not followed by a character that would continue the folder's name.
    private static string Pattern(string from) => Regex.Escape(from) + @"(?![A-Za-z0-9_.\-])";

    /// <summary>Rewrites every mapped path in <paramref name="text"/>. One pass per mapping, longest first, so nothing is mapped twice.</summary>
    public string MapText(string text)
    {
        if (IsEmpty || text.Length == 0) return text;
        // One combined pass would be faster, but a replaced path must not be matched again by a shorter mapping:
        // placeholders keep each mapping's output out of later passes.
        var held = new List<string>();
        foreach (var (pattern, replacement) in _text)
        {
            text = pattern.Replace(text, _ =>
            {
                held.Add(replacement);
                return "\u0001" + (held.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0002";
            });
        }
        if (held.Count == 0) return text;
        return Regex.Replace(text, "\u0001(\\d+)\u0002", m => held[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    /// <summary>
    /// Renames a Claude Code project folder (the encoded working directory). Claude Code cuts names past 200 characters
    /// and adds a hash; only the leading part is renamed then, which AgentHub's History still matches.
    /// </summary>
    public string MapEncodedFolder(string name)
    {
        foreach (var (from, to) in _encoded)
        {
            if (name.Length < from.Length || !name.StartsWith(from, StringComparison.OrdinalIgnoreCase)) continue;
            // Whole folder only: the next character must be the encoded separator or the end.
            if (name.Length > from.Length && name[from.Length] != '-') continue;
            return to + name[from.Length..];
        }
        return name;
    }

    /// <summary>File types whose contents are rewritten. Others are copied as they are.</summary>
    public static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".json" or ".jsonl" or ".md" or ".txt" or ".toml" or ".yaml" or ".yml" or ".ps1" or ".cmd" or ".bat" or ".sh" or ".ndjson" or ".xml" or ".csv";

    /// <summary>
    /// Streams <paramref name="input"/> to <paramref name="output"/> one line at a time with paths rewritten, keeping line
    /// endings as they were. Transcripts reach hundreds of megabytes, so the whole file is never held at once.
    /// </summary>
    public void MapStream(Stream input, Stream output)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(input, utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16, leaveOpen: true);
        using var writer = new StreamWriter(output, utf8, bufferSize: 1 << 16, leaveOpen: true);
        var buffer = new char[1 << 16];
        var line = new StringBuilder();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                line.Append(buffer, start, i - start + 1);
                writer.Write(MapText(line.ToString()));
                line.Clear();
                start = i + 1;
            }
            line.Append(buffer, start, read - start);
        }
        if (line.Length > 0) writer.Write(MapText(line.ToString()));
    }
}

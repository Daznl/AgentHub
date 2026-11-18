namespace AgentHub.Services.Instructions;

/// <summary>
/// A run of lines left as it is, or one change: <see cref="OldLines"/> replaced by <see cref="NewLines"/> (either may be
/// empty). <see cref="OldStart"/> is the 1-based line in the old text where the run begins.
/// </summary>
public sealed record DiffSegment(bool IsChange, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines, int OldStart);

/// <summary>Line helpers for review changes, and merging a text back with a chosen subset of its changes.</summary>
public static class LineDiff
{
    /// <summary>Splits on any line ending. A trailing newline gives a trailing empty line, so a split and join round-trips.</summary>
    public static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static string JoinLines(IEnumerable<string> lines) => string.Join("\r\n", lines);

    /// <summary>
    /// Rebuilds the text keeping unchanged runs, and for each change (numbered from 0 in order) its new lines when
    /// <paramref name="accept"/> says so, else its old lines. Lines are joined with CRLF.
    /// </summary>
    public static string Merge(IReadOnlyList<DiffSegment> segments, Func<int, bool> accept)
    {
        var lines = new List<string>();
        var change = 0;
        foreach (var segment in segments)
        {
            if (!segment.IsChange) lines.AddRange(segment.OldLines);
            else lines.AddRange(accept(change++) ? segment.NewLines : segment.OldLines);
        }
        return JoinLines(lines);
    }
}

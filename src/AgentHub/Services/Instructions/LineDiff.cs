namespace AgentHub.Services.Instructions;

/// <summary>
/// A run of lines that is the same in both texts, or one change: <see cref="OldLines"/> replaced by <see cref="NewLines"/>
/// (either may be empty). <see cref="OldStart"/> is the 1-based line in the old text where the run begins.
/// </summary>
public sealed record DiffSegment(bool IsChange, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines, int OldStart);

/// <summary>Line diff of two texts into same / changed runs, and merging back with a chosen subset of the changes.</summary>
public static class LineDiff
{
    /// <summary>Above this many line comparisons the middle is shown as one change rather than diffed (memory stays small).</summary>
    private const long MaxCells = 4_000_000;

    /// <summary>Splits on any line ending. A trailing newline gives a trailing empty line, so a split and join round-trips.</summary>
    public static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static string JoinLines(IEnumerable<string> lines) => string.Join("\r\n", lines);

    public static IReadOnlyList<DiffSegment> Compute(string oldText, string newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        var ops = new List<(char Op, string Line)>();
        for (var i = 0; i < prefix; i++) ops.Add(('=', a[i]));
        ops.AddRange(DiffMiddle(a[prefix..(a.Length - suffix)], b[prefix..(b.Length - suffix)]));
        for (var i = a.Length - suffix; i < a.Length; i++) ops.Add(('=', a[i]));

        return Group(ops);
    }

    // Longest common subsequence over the lines that differ.
    private static IEnumerable<(char Op, string Line)> DiffMiddle(string[] a, string[] b)
    {
        if ((long)a.Length * b.Length > MaxCells)
        {
            foreach (var line in a) yield return ('-', line);
            foreach (var line in b) yield return ('+', line);
            yield break;
        }

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { yield return ('=', a[x]); x++; y++; }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) { yield return ('+', b[y]); y++; }
            else { yield return ('-', a[x]); x++; }
        }
    }

    // Consecutive same lines form one run; consecutive removed/added lines form one change.
    private static List<DiffSegment> Group(List<(char Op, string Line)> ops)
    {
        var segments = new List<DiffSegment>();
        var oldLine = 1;
        var i = 0;
        while (i < ops.Count)
        {
            var start = oldLine;
            if (ops[i].Op == '=')
            {
                var same = new List<string>();
                while (i < ops.Count && ops[i].Op == '=') { same.Add(ops[i].Line); i++; }
                oldLine += same.Count;
                segments.Add(new DiffSegment(false, same, same, start));
            }
            else
            {
                var removed = new List<string>();
                var added = new List<string>();
                while (i < ops.Count && ops[i].Op != '=')
                {
                    (ops[i].Op == '-' ? removed : added).Add(ops[i].Line);
                    i++;
                }
                oldLine += removed.Count;
                segments.Add(new DiffSegment(true, removed, added, start));
            }
        }
        return segments;
    }

    /// <summary>
    /// Rebuilds the text keeping same runs, and for each change (numbered from 0 in order) its new lines when
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

using System.Text.RegularExpressions;

namespace AgentHub.Services.Todo;

/// <summary>One task line of a Markdown to-do list: <c>- [ ] text</c> or <c>- [x] text</c>.</summary>
public sealed class TodoTask
{
    public TodoTask(string text, bool done = false, string indent = "", string bullet = "-")
    {
        Text = text;
        Done = done;
        Indent = indent;
        Bullet = bullet;
    }

    public string Text { get; set; }
    public bool Done { get; set; }
    public string Indent { get; }
    public string Bullet { get; }

    public string ToLine() => $"{Indent}{Bullet} [{(Done ? "x" : " ")}] {Text}";
}

/// <summary>
/// A Markdown to-do list, parsed so tasks can be edited and reordered while everything else (headings, notes, blank lines)
/// is written back exactly as it was. Reordering swaps the places two tasks hold, so a heading never moves.
/// </summary>
public sealed class TodoDocument
{
    private static readonly Regex TaskLine = new(@"^(?<indent>\s*)(?<bullet>[-*+])\s+\[(?<mark>[ xX])\]\s?(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>Candidate names, in the order they are looked for. Claude Code usually writes TODO.md.</summary>
    public static readonly string[] FileNames = ["TODO.md", "TASKS.md", "todo.md", "tasks.md"];

    /// <summary>One line of the file: a task, or any other line kept as it was.</summary>
    private readonly List<object> _lines = [];

    /// <summary>Header lines kept when a new file is built.</summary>
    public const string NewFileHeading = "# To do";

    public IReadOnlyList<TodoTask> Tasks => [.. _lines.OfType<TodoTask>()];

    /// <summary>The file in <paramref name="folder"/> to use: the first that exists, else TODO.md (not created until saved).</summary>
    public static string FindPath(string folder)
    {
        foreach (var name in FileNames)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }
        return Path.Combine(folder, FileNames[0]);
    }

    public static TodoDocument Parse(string text)
    {
        var doc = new TodoDocument();
        if (text.Length == 0) return doc;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // A trailing newline gives one empty last element; Render adds it back.
        var count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (var i = 0; i < count; i++)
        {
            var m = TaskLine.Match(lines[i]);
            if (m.Success)
                doc._lines.Add(new TodoTask(m.Groups["text"].Value, m.Groups["mark"].Value != " ", m.Groups["indent"].Value, m.Groups["bullet"].Value));
            else
                doc._lines.Add(lines[i]);
        }
        return doc;
    }

    /// <summary>Text with Windows line endings, ending in a new line unless empty.</summary>
    public string Render()
    {
        if (_lines.Count == 0) return "";
        return string.Join("\r\n", _lines.Select(l => l is TodoTask t ? t.ToLine() : (string)l)) + "\r\n";
    }

    public TodoTask Add(string text)
    {
        var task = new TodoTask(text.Trim());
        // First task of an empty file gets a heading, so the file reads well on its own.
        if (_lines.Count == 0)
        {
            _lines.Add(NewFileHeading);
            _lines.Add("");
        }
        // After the last task; with none yet, at the end (a blank line first when the file does not end in one).
        var last = _lines.FindLastIndex(l => l is TodoTask);
        if (last >= 0) _lines.Insert(last + 1, task);
        else
        {
            if (_lines[^1] is string s && s.Length > 0) _lines.Add("");
            _lines.Add(task);
        }
        return task;
    }

    public void Remove(TodoTask task) => _lines.Remove(task);

    /// <summary>Moves a task one place up (-1) or down (+1) among the tasks. Returns false at either end.</summary>
    public bool Move(TodoTask task, int direction)
    {
        var slots = _lines.Select((l, i) => (l, i)).Where(p => p.l is TodoTask).Select(p => p.i).ToList();
        var at = slots.FindIndex(i => ReferenceEquals(_lines[i], task));
        var to = at + direction;
        if (at < 0 || to < 0 || to >= slots.Count) return false;
        (_lines[slots[at]], _lines[slots[to]]) = (_lines[slots[to]], _lines[slots[at]]);
        return true;
    }
}

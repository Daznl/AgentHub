using System;
using System.IO;
using System.Linq;
using AgentHub.Services.Todo;
using Xunit;

namespace AgentHub.Tests;

public class TodoDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub todo " + Guid.NewGuid().ToString("N"));

    public TodoDocumentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Sample = "# Plan\n\n- [ ] first\n- [x] second\nnotes here\n\n## Later\n* [ ] third\n  - [ ] nested\n";

    [Fact]
    public void Parse_reads_tasks_and_keeps_other_lines()
    {
        var doc = TodoDocument.Parse(Sample);
        Assert.Equal(["first", "second", "third", "nested"], doc.Tasks.Select(t => t.Text));
        Assert.Equal([false, true, false, false], doc.Tasks.Select(t => t.Done));
    }

    [Fact]
    public void Render_of_an_untouched_file_matches_it_with_Windows_line_endings()
    {
        Assert.Equal(Sample.Replace("\n", "\r\n"), TodoDocument.Parse(Sample).Render());
    }

    [Fact]
    public void Move_swaps_places_so_headings_stay_put()
    {
        var doc = TodoDocument.Parse(Sample);
        Assert.True(doc.Move(doc.Tasks[2], -1)); // third above second
        Assert.Equal("# Plan\r\n\r\n- [ ] first\r\n* [ ] third\r\nnotes here\r\n\r\n## Later\r\n- [x] second\r\n  - [ ] nested\r\n", doc.Render());
    }

    [Fact]
    public void Move_refuses_at_either_end()
    {
        var doc = TodoDocument.Parse(Sample);
        Assert.False(doc.Move(doc.Tasks[0], -1));
        Assert.False(doc.Move(doc.Tasks[^1], +1));
    }

    [Fact]
    public void Add_puts_a_new_task_after_the_last_one()
    {
        var doc = TodoDocument.Parse("- [ ] a\n\nnotes\n");
        doc.Add("b");
        Assert.Equal("- [ ] a\r\n- [ ] b\r\n\r\nnotes\r\n", doc.Render());
    }

    [Fact]
    public void Add_to_an_empty_file_builds_a_heading_and_the_task()
    {
        var doc = TodoDocument.Parse("");
        doc.Add("  first  ");
        Assert.Equal("# To do\r\n\r\n- [ ] first\r\n", doc.Render());
    }

    [Fact]
    public void Add_to_a_file_with_only_notes_leaves_a_blank_line_first()
    {
        var doc = TodoDocument.Parse("# Notes\ntext\n");
        doc.Add("x");
        Assert.Equal("# Notes\r\ntext\r\n\r\n- [ ] x\r\n", doc.Render());
    }

    [Fact]
    public void Remove_drops_only_that_task()
    {
        var doc = TodoDocument.Parse(Sample);
        doc.Remove(doc.Tasks[1]);
        Assert.DoesNotContain("second", doc.Render());
        Assert.Contains("notes here", doc.Render());
    }

    [Fact]
    public void FindPath_prefers_an_existing_file_and_otherwise_names_TODO_md()
    {
        Assert.Equal(Path.Combine(_root, "TODO.md"), TodoDocument.FindPath(_root));
        File.WriteAllText(Path.Combine(_root, "TASKS.md"), "- [ ] x");
        Assert.Equal(Path.Combine(_root, "TASKS.md"), TodoDocument.FindPath(_root));
    }
}

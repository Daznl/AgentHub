using AgentHub.Hosting;
using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Services.Instructions;

/// <summary>Which coding CLI an instruction file belongs to.</summary>
public enum InstructionCli { ClaudeCode, Codex, Gemini }

/// <summary>One Markdown instruction file a CLI reads, whether or not it exists yet.</summary>
public sealed record InstructionFile(InstructionCli Cli, string Label, string Path, string Note);

/// <summary>
/// A file as read from disk. <see cref="Text"/> uses Windows line endings (what a WPF TextBox edits);
/// <see cref="Hash"/> is the SHA-256 of the bytes read, so a save can tell whether anything changed the file since.
/// </summary>
public sealed record LoadedInstructionFile(string Text, bool Exists, string Hash, bool HasBom, string NewLine);

public enum SaveOutcome { Saved, ChangedOnDisk }

/// <summary>
/// Finds, reads and writes the Markdown files that Claude Code, Codex and Gemini/Antigravity load as standing
/// instructions: the user-wide one in each CLI's home folder, and the ones at a repository's root.
/// Saves never silently replace a file someone else changed after it was loaded.
/// </summary>
public static class InstructionFiles
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Claude Code's config folder: <c>CLAUDE_CONFIG_DIR</c> when set, else <c>~\.claude</c>.</summary>
    public static string ClaudeHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude");

    /// <summary>Codex's home folder: <c>CODEX_HOME</c> when set, else <c>~\.codex</c>.</summary>
    public static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".codex");

    /// <summary>Gemini CLI and Antigravity share <c>~\.gemini</c>.</summary>
    public static string GeminiHome => Path.Combine(Home, ".gemini");

    /// <summary>The user-wide instruction file of each CLI, in tab order.</summary>
    public static IReadOnlyList<InstructionFile> GlobalFiles(string? claudeHome = null, string? codexHome = null, string? geminiHome = null)
    {
        claudeHome ??= ClaudeHome;
        codexHome ??= CodexHome;
        geminiHome ??= GeminiHome;

        // Codex reads AGENTS.override.md instead of AGENTS.md when the override exists, so edit the one it uses.
        var codexOverride = Path.Combine(codexHome, "AGENTS.override.md");
        var codex = File.Exists(codexOverride)
            ? new InstructionFile(InstructionCli.Codex, "Codex", codexOverride, "Codex uses this override instead of AGENTS.md while it exists.")
            : new InstructionFile(InstructionCli.Codex, "Codex", Path.Combine(codexHome, "AGENTS.md"), "Read by Codex in every folder.");

        return
        [
            new(InstructionCli.ClaudeCode, "Claude Code", Path.Combine(claudeHome, "CLAUDE.md"), "Read by Claude Code in every folder."),
            codex,
            new(InstructionCli.Gemini, "Gemini / Antigravity", Path.Combine(geminiHome, "GEMINI.md"), "Read by Gemini CLI and Antigravity in every folder."),
        ];
    }

    /// <summary>
    /// The instruction files at a folder's root: CLAUDE.md, AGENTS.md and GEMINI.md always (saving creates a missing one),
    /// plus Claude Code's optional <c>.claude\CLAUDE.md</c> and <c>CLAUDE.local.md</c> when present.
    /// </summary>
    public static IReadOnlyList<InstructionFile> RepoFiles(string folder)
    {
        var files = new List<InstructionFile>
        {
            new(InstructionCli.ClaudeCode, "CLAUDE.md", Path.Combine(folder, "CLAUDE.md"), "Read by Claude Code in this folder."),
        };
        // In the home folder .claude\CLAUDE.md is the user-wide file, which Context already shows.
        var nested = Path.Combine(folder, ".claude", "CLAUDE.md");
        if (File.Exists(nested) && !string.Equals(Path.GetFullPath(nested), Path.GetFullPath(Path.Combine(ClaudeHome, "CLAUDE.md")), StringComparison.OrdinalIgnoreCase))
            files.Add(new(InstructionCli.ClaudeCode, ".claude\\CLAUDE.md", nested, "Also read by Claude Code in this folder."));
        var local = Path.Combine(folder, "CLAUDE.local.md");
        if (File.Exists(local))
            files.Add(new(InstructionCli.ClaudeCode, "CLAUDE.local.md", local, "Personal Claude Code notes for this folder, usually not committed."));
        files.Add(new(InstructionCli.Codex, "AGENTS.md", Path.Combine(folder, "AGENTS.md"), "Read by Codex (and other agents that follow AGENTS.md) in this folder."));
        files.Add(new(InstructionCli.Gemini, "GEMINI.md", Path.Combine(folder, "GEMINI.md"), "Read by Gemini CLI and Antigravity in this folder."));
        return files;
    }

    /// <summary>The CLI a command line starts, from its executable name; null for plain shells.</summary>
    public static InstructionCli? DetectCli(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var trimmed = commandLine.TrimStart();
        string exe;
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            exe = close > 0 ? trimmed[1..close] : trimmed[1..];
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            exe = space > 0 ? trimmed[..space] : trimmed;
        }
        return Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
        {
            "claude" => InstructionCli.ClaudeCode,
            "codex" => InstructionCli.Codex,
            "gemini" or "agy" => InstructionCli.Gemini,
            _ => null
        };
    }

    public static LoadedInstructionFile Load(string path)
    {
        if (!File.Exists(path)) return new("", false, HashOf(null), false, "\n");

        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var crlf = CountOf(text, "\r\n");
        var lf = CountOf(text, "\n") - crlf;
        var newLine = crlf > lf ? "\r\n" : "\n";
        return new(ToWindowsNewLines(text), true, HashOf(bytes), hasBom, newLine);
    }

    /// <summary>
    /// Writes <paramref name="text"/> (edited with Windows line endings) back in the file's original line-ending style and
    /// byte-order-mark choice. Refuses with <see cref="SaveOutcome.ChangedOnDisk"/> when the file on disk no longer matches
    /// <paramref name="loaded"/>, unless <paramref name="overwrite"/>. The write goes to a temporary file in the same folder
    /// first, so a failure part-way never leaves the instruction file half written.
    /// </summary>
    public static SaveOutcome Save(string path, string text, LoadedInstructionFile loaded, bool overwrite, out LoadedInstructionFile saved)
    {
        if (!overwrite && CurrentHash(path) != loaded.Hash)
        {
            saved = loaded;
            return SaveOutcome.ChangedOnDisk;
        }

        var content = ToWindowsNewLines(text).Replace("\r\n", loaded.NewLine);
        var body = new UTF8Encoding(false).GetBytes(content);
        var bytes = loaded.HasBom ? [0xEF, 0xBB, 0xBF, .. body] : body;

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.agenthub-tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            SharedFile.Replace(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        saved = new(ToWindowsNewLines(content), true, HashOf(bytes), loaded.HasBom, loaded.NewLine);
        return SaveOutcome.Saved;
    }

    /// <summary>SHA-256 of the file as it is now; a fixed value when it does not exist.</summary>
    public static string CurrentHash(string path)
    {
        try
        {
            return HashOf(File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
        catch (IOException)
        {
            return "unreadable";
        }
    }

    private static string HashOf(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));

    private static string ToWindowsNewLines(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

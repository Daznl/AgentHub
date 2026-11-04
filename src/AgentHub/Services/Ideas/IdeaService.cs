using System.IO;
using System.Text.Json;
using AgentHub.Models;

namespace AgentHub.Services.Ideas;

/// <summary>
/// Manages the list of application ideas to build, persisted to %APPDATA%\AgentHub\ideas.json.
/// Thread-safe for concurrent read/write access.
/// </summary>
public sealed class IdeaService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storagePath;

    public event Action<IReadOnlyList<ProjectIdea>>? IdeasChanged;

    public IdeaService(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _storagePath = customPath;
        }
        else
        {
            var baseDir = Environment.GetEnvironmentVariable("AGENTHUB_SETTINGS_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentHub");
            _storagePath = Path.Combine(baseDir, "ideas.json");
        }
    }

    public string StoragePath => _storagePath;

    public async Task<List<ProjectIdea>> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(_storagePath))
            {
                var defaults = SeedDefaults();
                await SaveInternalAsync(defaults);
                return defaults;
            }

            await using var stream = File.OpenRead(_storagePath);
            var ideas = await JsonSerializer.DeserializeAsync<List<ProjectIdea>>(stream, JsonOptions);
            return ideas ?? [];
        }
        catch
        {
            return [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<ProjectIdea> ideas)
    {
        await _lock.WaitAsync();
        try
        {
            var list = ideas.ToList();
            await SaveInternalAsync(list);
            IdeasChanged?.Invoke(list);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddIdeaAsync(ProjectIdea idea)
    {
        var current = await LoadAsync();
        current.Add(idea);
        await SaveAsync(current);
    }

    public async Task UpdateIdeaAsync(ProjectIdea updated)
    {
        var current = await LoadAsync();
        var index = current.FindIndex(i => i.Id == updated.Id);
        if (index >= 0)
        {
            current[index] = updated;
            await SaveAsync(current);
        }
    }

    public async Task DeleteIdeaAsync(string id)
    {
        var current = await LoadAsync();
        var count = current.RemoveAll(i => i.Id == id);
        if (count > 0)
        {
            await SaveAsync(current);
        }
    }

    public async Task ReorderAsync(string id, int targetIndex)
    {
        var current = await LoadAsync();
        var index = current.FindIndex(i => i.Id == id);
        if (index < 0) return;

        var item = current[index];
        current.RemoveAt(index);
        targetIndex = Math.Clamp(targetIndex, 0, current.Count);
        current.Insert(targetIndex, item);
        await SaveAsync(current);
    }

    public async Task<ProjectIdea?> GetNextQueuedAsync()
    {
        var current = await LoadAsync();
        return current.FirstOrDefault(i => i.Status is IdeaStatus.Building or IdeaStatus.WaitingForReset)
            ?? current.FirstOrDefault(i => i.Status == IdeaStatus.Queued);
    }

    private async Task SaveInternalAsync(List<ProjectIdea> ideas)
    {
        var dir = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = _storagePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, ideas, JsonOptions);
        }

        File.Move(tempPath, _storagePath, overwrite: true);
    }

    private static List<ProjectIdea> SeedDefaults()
    {
        var defaultProjectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "ClaudeCProjects");

        return
        [
            new ProjectIdea
            {
                Title = "Autonomous Code Review Assistant",
                Description = "A CLI tool that scans changed git files, verifies coding standards and test coverage, and proposes clean PR comments.",
                TargetDirectory = Path.Combine(defaultProjectsDir, "CodeReviewAssistant"),
                AgentCommand = "claude",
                ContextHandoffThreshold = 0.75,
                Status = IdeaStatus.Queued,
                StatusNote = "Ready to build"
            }
        ];
    }
}

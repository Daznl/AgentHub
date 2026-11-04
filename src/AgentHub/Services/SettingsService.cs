using System.Text.Json;
using AgentHub.Models;

namespace AgentHub.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Several places save without awaiting each other; one write at a time keeps the file whole.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// %APPDATA%\AgentHub, or the folder in <c>AGENTHUB_SETTINGS_DIR</c> so a test copy of AgentHub keeps its own settings
    /// and pane layout instead of sharing (and overwriting) the main instance's.
    /// </summary>
    public string SettingsDirectory { get; } =
        Environment.GetEnvironmentVariable("AGENTHUB_SETTINGS_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentHub");

    public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        Directory.CreateDirectory(SettingsDirectory);
        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults);
            return defaults;
        }

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
                   ?? new AppSettings();
            if (settings.UsageProviders.Count == 0)
            {
                settings.UsageProviders = new AppSettings().UsageProviders;
                await SaveAsync(settings);
            }
            return settings;
        }
        catch
        {
            var backup = SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(SettingsPath, backup, overwrite: true);
            var defaults = new AppSettings();
            await SaveAsync(defaults);
            return defaults;
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            await using var stream = File.Create(SettingsPath);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes before returning. For shutdown, where an awaited save would not finish.</summary>
    public void Save(AppSettings settings)
    {
        _writeLock.Wait();
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

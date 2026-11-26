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
    public string SettingsDirectory { get; }

    /// <param name="settingsDirectory">A folder of its own, for tests; by default the one described above.</param>
    public SettingsService(string? settingsDirectory = null) =>
        SettingsDirectory = !string.IsNullOrWhiteSpace(settingsDirectory) ? settingsDirectory
            : Environment.GetEnvironmentVariable("AGENTHUB_SETTINGS_DIR") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentHub");

    public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        Directory.CreateDirectory(SettingsDirectory);
        AppSettings settings;
        if (!File.Exists(SettingsPath))
        {
            settings = new AppSettings();
            await SaveAsync(settings);
        }
        else
        {
            try
            {
                AppSettings? read;
                await using (var stream = File.OpenRead(SettingsPath))
                    read = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions);
                settings = read ?? new AppSettings();
                if (settings.UsageProviders.Count == 0)
                {
                    settings.UsageProviders = new AppSettings().UsageProviders;
                    await SaveAsync(settings);
                }
            }
            catch
            {
                var backup = SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(SettingsPath, backup, overwrite: true);
                settings = new AppSettings();
                await SaveAsync(settings);
            }
        }

        return await ApplyPendingImportAsync(settings);
    }

    /// <summary>
    /// Settings brought from another computer (Settings → Move to another computer) are merged here, at start, before
    /// anything reads them: the AgentHub that imported them saved its own on exit after the import.
    /// </summary>
    private async Task<AppSettings> ApplyPendingImportAsync(AppSettings settings)
    {
        try
        {
            if (Move.SettingsImport.ApplyPending(settings, SettingsDirectory, SettingsPath, JsonOptions) is not { } merged) return settings;
            await SaveAsync(merged);
            PendingImportApplied = true;
            return merged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            PendingImportError = ex.Message;
            return settings;
        }
    }

    /// <summary>The last <see cref="LoadAsync"/> merged settings imported from another computer.</summary>
    public bool PendingImportApplied { get; private set; }

    /// <summary>Why imported settings waiting to be merged could not be; they stay waiting for the next start.</summary>
    public string? PendingImportError { get; private set; }

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

using System.Text.Json;
using HeadPointerMvp.Domain;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            var created = new AppSettings();
            await SaveAsync(created);
            return created;
        }

        try
        {
            await using var stream = File.OpenRead(AppPaths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", true);
            var created = new AppSettings();
            await SaveAsync(created);
            return created;
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        AppPaths.EnsureCreated();
        var temporaryFile = AppPaths.SettingsFile + ".new";
        await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(settings, Options));
        File.Move(temporaryFile, AppPaths.SettingsFile, true);
    }
}

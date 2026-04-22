using System.Text.Json;
using Serilog;

namespace AudioInOutTranscribing.App.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public string SettingsPath => _settingsPath;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            var defaults = AppSettings.CreateDefault();
            EnsureDefaults(defaults);
            return defaults;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken);
            if (settings is null)
            {
                var defaults = AppSettings.CreateDefault();
                EnsureDefaults(defaults);
                return defaults;
            }

            EnsureDefaults(settings);
            return settings;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings from {SettingsPath}. Falling back to defaults.", _settingsPath);
            var defaults = AppSettings.CreateDefault();
            EnsureDefaults(defaults);
            return defaults;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        EnsureDefaults(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? AppPaths.AppDataRoot);

        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
    }

    private static void EnsureDefaults(AppSettings settings)
    {
        if (settings.ChunkSeconds < 5)
        {
            settings.ChunkSeconds = 30;
        }

        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
        {
            settings.OutputFolder = AppPaths.DefaultTranscriptRoot;
        }

        settings.TranscriptFormat = string.IsNullOrWhiteSpace(settings.TranscriptFormat) ? "txt+jsonl" : settings.TranscriptFormat;
        settings.ApiProvider = string.IsNullOrWhiteSpace(settings.ApiProvider) ? "mistral" : settings.ApiProvider;
        settings.Model = NormalizeModel(settings.Model);
        settings.MistralApiKey ??= string.Empty;
        settings.AutoStartOnLaunch = false;
    }

    private static string NormalizeModel(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            return "voxtral-mini-latest";
        }

        // Migrate legacy/invalid default from early scaffold versions.
        if (string.Equals(configuredModel, "voxtral-mini-transcribe-v2", StringComparison.OrdinalIgnoreCase))
        {
            return "voxtral-mini-latest";
        }

        return configuredModel.Trim();
    }
}

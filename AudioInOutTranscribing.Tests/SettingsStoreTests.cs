using AudioInOutTranscribing.App.Core;

namespace AudioInOutTranscribing.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSettings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var store = new SettingsStore(settingsPath);
            var expected = new AppSettings
            {
                InputDeviceId = "input-1",
                InputDeviceName = "Microphone",
                OutputDeviceId = "output-1",
                OutputDeviceName = "Speakers",
                OutputFolder = @"D:\Transcripts",
                AutoStartOnLaunch = false,
                SaveRawAudio = true,
                ChunkSeconds = 30,
                TranscriptFormat = "txt+jsonl",
                ApiProvider = "mistral",
                Model = "voxtral-mini-transcribe-v2",
                MistralApiKey = "plain-test-key"
            };

            await store.SaveAsync(expected);
            var loaded = await store.LoadAsync();

            Assert.Equal(expected.InputDeviceId, loaded.InputDeviceId);
            Assert.Equal(expected.OutputDeviceId, loaded.OutputDeviceId);
            Assert.Equal(expected.OutputFolder, loaded.OutputFolder);
            Assert.Equal(expected.SaveRawAudio, loaded.SaveRawAudio);
            Assert.Equal(expected.ChunkSeconds, loaded.ChunkSeconds);
            Assert.Equal(expected.MistralApiKey, loaded.MistralApiKey);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Load_MigratesLegacyModelNameToLatestAlias()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var legacyJson = """
                             {
                               "model": "voxtral-mini-transcribe-v2",
                               "outputFolder": "C:\\Transcripts"
                             }
                             """;
            await File.WriteAllTextAsync(settingsPath, legacyJson);

            var store = new SettingsStore(settingsPath);
            var loaded = await store.LoadAsync();

            Assert.Equal("voxtral-mini-latest", loaded.Model);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Load_DisablesAutoStartOnLaunch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var json = """
                       {
                         "autoStartOnLaunch": true
                       }
                       """;
            await File.WriteAllTextAsync(settingsPath, json);

            var store = new SettingsStore(settingsPath);
            var loaded = await store.LoadAsync();

            Assert.False(loaded.AutoStartOnLaunch);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Load_PreservesMergedTranscriptFormat()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var json = """
                       {
                         "transcriptFormat": "txt+jsonl+merged"
                       }
                       """;
            await File.WriteAllTextAsync(settingsPath, json);

            var store = new SettingsStore(settingsPath);
            var loaded = await store.LoadAsync();

            Assert.Equal("txt+jsonl+merged", loaded.TranscriptFormat);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}

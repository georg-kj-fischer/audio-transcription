using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Core;
using AudioInOutTranscribing.App.UI;
using Serilog;

namespace AudioInOutTranscribing.App;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore;
    private readonly DeviceEnumerator _deviceEnumerator;
    private readonly SessionManager _sessionManager;
    private AppSettings _settings;

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;
    private readonly Icon _errorIcon;

    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;

    private bool _isExiting;

    public TrayApplicationContext(
        SettingsStore settingsStore,
        DeviceEnumerator deviceEnumerator,
        AppSettings initialSettings)
    {
        _settingsStore = settingsStore;
        _deviceEnumerator = deviceEnumerator;
        _settings = CloneSettings(initialSettings);

        EnsureDefaultDeviceSelections();

        _sessionManager = new SessionManager(_deviceEnumerator);
        _sessionManager.StateChanged += OnSessionStateChanged;

        using var baseIcon = LoadBaseIcon();
        _idleIcon = CreateStatusIcon(baseIcon, Color.DimGray);
        _recordingIcon = CreateStatusIcon(baseIcon, Color.Firebrick);
        _errorIcon = CreateStatusIcon(baseIcon, Color.Goldenrod);

        _startItem = new ToolStripMenuItem("Start Recording");
        _startItem.Click += async (_, _) => await StartRecordingAsync();

        _stopItem = new ToolStripMenuItem("Stop Recording");
        _stopItem.Click += async (_, _) => await StopRecordingAsync();

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += async (_, _) => await OpenSettingsAsync();

        var openOutputItem = new ToolStripMenuItem("Open Output Folder");
        openOutputItem.Click += (_, _) => OpenOutputFolder();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += async (_, _) => await ExitApplicationAsync();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _startItem,
            _stopItem,
            new ToolStripSeparator(),
            settingsItem,
            openOutputItem,
            new ToolStripSeparator(),
            exitItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "Audio Transcriber - Idle",
            ContextMenuStrip = menu,
            Visible = true
        };

        UpdateMenuState();
        SetTrayState(TrayState.Idle, "Idle");
    }

    protected override void ExitThreadCore()
    {
        if (!_isExiting)
        {
            _isExiting = true;
            try
            {
                _ = _sessionManager.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to stop session during exit.");
            }
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _idleIcon.Dispose();
        _recordingIcon.Dispose();
        _errorIcon.Dispose();
        _sessionManager.Dispose();

        base.ExitThreadCore();
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            var recoverySummary = await _sessionManager.RecoverPendingChunksAsync(_settings);
            if (recoverySummary.PendingChunksFound > 0)
            {
                var recoveryMessage =
                    $"Retried: {recoverySummary.RetriedChunks}, OK: {recoverySummary.SucceededChunks}, " +
                    $"Failed: {recoverySummary.FailedChunks}, Maxed: {recoverySummary.SkippedDueToMaxRetries}";
                ShowBalloon("Pending chunk recovery", recoveryMessage, ToolTipIcon.Info);
            }

            await _sessionManager.StartAsync(_settings);
            UpdateMenuState();
        }
        catch (Exception ex)
        {
            SetTrayState(TrayState.Error, "Failed to start recording");
            ShowBalloon("Start failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task StopRecordingAsync()
    {
        try
        {
            var summary = await _sessionManager.StopAsync();
            UpdateMenuState();
            if (summary is not null)
            {
                var message =
                    $"Saved to: {summary.SessionRootPath}{Environment.NewLine}" +
                    $"Mic OK: {summary.Mic.SucceededChunks}, Speaker OK: {summary.Speaker.SucceededChunks}";
                ShowBalloon("Recording stopped", message, ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            SetTrayState(TrayState.Error, "Stop failed");
            ShowBalloon("Stop failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task OpenSettingsAsync()
    {
        using var form = new SettingsForm(_settings, _deviceEnumerator);
        if (form.ShowDialog() != DialogResult.OK || form.UpdatedSettings is null)
        {
            return;
        }

        _settings = form.UpdatedSettings;
        EnsureDefaultDeviceSelections();
        await _settingsStore.SaveAsync(_settings);
        ShowBalloon("Settings saved", "Configuration updated.", ToolTipIcon.Info);
    }

    private void OpenOutputFolder()
    {
        var folder = _settings.OutputFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = AppPaths.DefaultTranscriptRoot;
        }

        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private async Task ExitApplicationAsync()
    {
        _isExiting = true;
        try
        {
            await _sessionManager.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while stopping recording during app exit.");
        }

        ExitThread();
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        SetTrayState(e.State, e.Message);
        UpdateMenuState();
    }

    private void SetTrayState(TrayState state, string message)
    {
        _notifyIcon.Icon = state switch
        {
            TrayState.Recording => _recordingIcon,
            TrayState.Error => _errorIcon,
            _ => _idleIcon
        };

        var text = $"Audio Transcriber - {message}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void UpdateMenuState()
    {
        var recording = _sessionManager.IsRecording;
        _startItem.Enabled = !recording;
        _stopItem.Enabled = recording;
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private void EnsureDefaultDeviceSelections()
    {
        if (string.IsNullOrWhiteSpace(_settings.OutputFolder))
        {
            _settings.OutputFolder = AppPaths.DefaultTranscriptRoot;
        }

        if (string.IsNullOrWhiteSpace(_settings.InputDeviceId))
        {
            var firstInput = _deviceEnumerator.GetInputDevices().FirstOrDefault();
            if (firstInput is not null)
            {
                _settings.InputDeviceId = firstInput.Id;
                _settings.InputDeviceName = firstInput.Name;
            }
        }

        if (string.IsNullOrWhiteSpace(_settings.OutputDeviceId))
        {
            var firstOutput = _deviceEnumerator.GetOutputDevices().FirstOrDefault();
            if (firstOutput is not null)
            {
                _settings.OutputDeviceId = firstOutput.Id;
                _settings.OutputDeviceName = firstOutput.Name;
            }
        }
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        return new AppSettings
        {
            InputDeviceId = source.InputDeviceId,
            InputDeviceName = source.InputDeviceName,
            OutputDeviceId = source.OutputDeviceId,
            OutputDeviceName = source.OutputDeviceName,
            OutputFolder = source.OutputFolder,
            AutoStartOnLaunch = source.AutoStartOnLaunch,
            SaveRawAudio = source.SaveRawAudio,
            ChunkSeconds = source.ChunkSeconds,
            TranscriptFormat = source.TranscriptFormat,
            ApiProvider = source.ApiProvider,
            Model = source.Model,
            MistralApiKey = source.MistralApiKey
        };
    }

    private static Icon LoadBaseIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null)
            {
                return (Icon)icon.Clone();
            }
        }
        catch
        {
            // Fall back to default icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static Icon CreateStatusIcon(Icon baseIcon, Color stateColor)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.DrawIcon(baseIcon, new Rectangle(0, 0, 16, 16));

        using var stateBrush = new SolidBrush(stateColor);
        using var stateBorder = new Pen(Color.White, 1f);
        graphics.FillEllipse(stateBrush, 8, 8, 7, 7);
        graphics.DrawEllipse(stateBorder, 8, 8, 7, 7);
        var iconHandle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            _ = DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}

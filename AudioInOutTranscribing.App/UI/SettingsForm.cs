using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Core;
using AudioInOutTranscribing.App.Transcription;
using NAudio.Wave;
using Serilog;

namespace AudioInOutTranscribing.App.UI;

public sealed class SettingsForm : Form
{
    private readonly DeviceEnumerator _deviceEnumerator;
    private readonly AppSettings _workingCopy;
    private bool _modelsLoaded;

    private readonly ComboBox _inputDeviceCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _outputDeviceCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _outputFolderTextBox = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _chunkSecondsNumeric = new() { Dock = DockStyle.Left, Minimum = 5, Maximum = 120, Width = 100 };
    private readonly ComboBox _transcriptFormatCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _saveRawAudioCheckBox = new() { Text = "Save raw WAV chunk files" };
    private readonly TextBox _apiKeyTextBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly ComboBox _modelCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _refreshModelsButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _testApiButton = new() { Text = "Test Mistral API", AutoSize = true };
    private readonly Button _refreshDevicesButton = new() { Text = "Refresh Devices", AutoSize = true };

    public SettingsForm(AppSettings currentSettings, DeviceEnumerator deviceEnumerator)
    {
        _deviceEnumerator = deviceEnumerator;
        _workingCopy = CloneSettings(currentSettings);

        Text = "Audio Transcriber Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 640;
        Height = 460;

        BuildLayout();
        LoadData();
        Shown += async (_, _) => await EnsureModelsLoadedAsync();
    }

    public AppSettings? UpdatedSettings { get; private set; }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var topBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        topBar.Controls.Add(_refreshDevicesButton);
        _refreshDevicesButton.Click += (_, _) => RefreshDevices();
        topBar.Controls.Add(_testApiButton);
        _testApiButton.Click += async (_, _) => await TestMistralApiAsync();

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 8,
            AutoSize = true
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        AddRow(grid, 0, "Input device", _inputDeviceCombo);
        AddRow(grid, 1, "Output device", _outputDeviceCombo);

        var browseButton = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browseButton.Click += (_, _) => BrowseOutputFolder();
        AddRow(grid, 2, "Output folder", _outputFolderTextBox, browseButton);

        AddRow(grid, 3, "Chunk seconds", _chunkSecondsNumeric);
        AddRow(grid, 4, "Transcript format", _transcriptFormatCombo);
        AddRow(grid, 5, "Mistral API key", _apiKeyTextBox);
        AddRow(grid, 6, "Mistral model", _modelCombo, _refreshModelsButton);
        _refreshModelsButton.Click += async (_, _) => await RefreshModelsAsync(showResultPopup: true);

        grid.Controls.Add(_saveRawAudioCheckBox, 1, 7);
        grid.SetColumnSpan(_saveRawAudioCheckBox, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false
        };

        var saveButton = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        saveButton.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        root.Controls.Add(topBar, 0, 0);
        root.Controls.Add(grid, 0, 1);
        root.Controls.Add(buttons, 0, 2);

        Controls.Add(root);
    }

    private void LoadData()
    {
        var chunkSeconds = Math.Clamp(_workingCopy.ChunkSeconds, (int)_chunkSecondsNumeric.Minimum, (int)_chunkSecondsNumeric.Maximum);
        _chunkSecondsNumeric.Value = chunkSeconds;
        _outputFolderTextBox.Text = _workingCopy.OutputFolder;
        _saveRawAudioCheckBox.Checked = _workingCopy.SaveRawAudio;
        _apiKeyTextBox.Text = _workingCopy.MistralApiKey;
        _modelCombo.Text = string.IsNullOrWhiteSpace(_workingCopy.Model) ? "voxtral-mini-latest" : _workingCopy.Model;

        _transcriptFormatCombo.Items.Clear();
        _transcriptFormatCombo.Items.Add("txt+jsonl");
        _transcriptFormatCombo.Items.Add("txt+jsonl+merged");

        var selectedFormat = string.IsNullOrWhiteSpace(_workingCopy.TranscriptFormat)
            ? "txt+jsonl"
            : _workingCopy.TranscriptFormat.Trim();

        if (!_transcriptFormatCombo.Items
                .OfType<string>()
                .Any(item => string.Equals(item, selectedFormat, StringComparison.OrdinalIgnoreCase)))
        {
            selectedFormat = "txt+jsonl";
        }

        _transcriptFormatCombo.SelectedItem = selectedFormat;

        RefreshDevices();
    }

    private async Task EnsureModelsLoadedAsync()
    {
        if (_modelsLoaded)
        {
            return;
        }

        _modelsLoaded = true;
        await RefreshModelsAsync(showResultPopup: false);
    }

    private void RefreshDevices()
    {
        var inputSelection = (_workingCopy.InputDeviceId, _workingCopy.InputDeviceName);
        var outputSelection = (_workingCopy.OutputDeviceId, _workingCopy.OutputDeviceName);

        _inputDeviceCombo.Items.Clear();
        foreach (var device in _deviceEnumerator.GetInputDevices())
        {
            _inputDeviceCombo.Items.Add(new DeviceChoice(device.Id, device.Name));
        }

        _outputDeviceCombo.Items.Clear();
        foreach (var device in _deviceEnumerator.GetOutputDevices())
        {
            _outputDeviceCombo.Items.Add(new DeviceChoice(device.Id, device.Name));
        }

        SelectDevice(_inputDeviceCombo, inputSelection.Item1, inputSelection.Item2);
        SelectDevice(_outputDeviceCombo, outputSelection.Item1, outputSelection.Item2);
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = _outputFolderTextBox.Text,
            Description = "Choose transcript output folder"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private void SaveAndClose()
    {
        if (_inputDeviceCombo.SelectedItem is not DeviceChoice inputChoice)
        {
            MessageBox.Show(this, "Please select an input device.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_outputDeviceCombo.SelectedItem is not DeviceChoice outputChoice)
        {
            MessageBox.Show(this, "Please select an output device.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputFolderTextBox.Text))
        {
            MessageBox.Show(this, "Please choose an output folder.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _workingCopy.InputDeviceId = inputChoice.Id;
        _workingCopy.InputDeviceName = inputChoice.Name;
        _workingCopy.OutputDeviceId = outputChoice.Id;
        _workingCopy.OutputDeviceName = outputChoice.Name;
        _workingCopy.OutputFolder = _outputFolderTextBox.Text.Trim();
        _workingCopy.ChunkSeconds = (int)_chunkSecondsNumeric.Value;
        _workingCopy.TranscriptFormat = _transcriptFormatCombo.SelectedItem?.ToString() ?? "txt+jsonl";
        _workingCopy.SaveRawAudio = _saveRawAudioCheckBox.Checked;
        _workingCopy.AutoStartOnLaunch = false;
        _workingCopy.MistralApiKey = _apiKeyTextBox.Text.Trim();
        _workingCopy.Model = ResolveSelectedModelId();

        UpdatedSettings = CloneSettings(_workingCopy);
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task RefreshModelsAsync(bool showResultPopup)
    {
        var apiKey = _apiKeyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (showResultPopup)
            {
                MessageBox.Show(this, "Please enter a Mistral API key first.", "Missing API key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return;
        }

        var currentModel = ResolveSelectedModelId();
        _refreshModelsButton.Enabled = false;
        _refreshModelsButton.Text = "Loading...";

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var catalogClient = new MistralModelCatalogClient(httpClient);
            var result = await catalogClient.GetModelsAsync(apiKey, CancellationToken.None);

            if (!result.IsSuccess)
            {
                if (showResultPopup)
                {
                    MessageBox.Show(
                        this,
                        $"Failed to load models from Mistral.{Environment.NewLine}{result.Error}",
                        "Mistral models",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                return;
            }

            _refreshModelsButton.Text = "Verifying...";
            var choices = new List<ModelChoice>(result.Models.Count);
            foreach (var model in result.Models)
            {
                var probe = await catalogClient.ProbeDiarizationSupportAsync(apiKey, model.Id, CancellationToken.None);
                choices.Add(ModelChoice.FromModelInfo(model, probe));
            }

            _modelCombo.BeginUpdate();
            try
            {
                _modelCombo.Items.Clear();
                foreach (var choice in choices)
                {
                    _modelCombo.Items.Add(choice);
                }
            }
            finally
            {
                _modelCombo.EndUpdate();
            }

            var preferredModel = string.IsNullOrWhiteSpace(currentModel)
                ? (string.IsNullOrWhiteSpace(_workingCopy.Model) ? "voxtral-mini-latest" : _workingCopy.Model)
                : currentModel;

            var match = _modelCombo.Items
                .OfType<ModelChoice>()
                .FirstOrDefault(choice => string.Equals(choice.Id, preferredModel, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                _modelCombo.SelectedItem = match;
                _modelCombo.Text = match.ToString();
            }
            else
            {
                _modelCombo.Text = preferredModel;
            }

            if (showResultPopup)
            {
                var verifiedYes = choices.Count(choice => choice.IsVerified && choice.DiarizationSupport == DiarizationSupport.Supported);
                var verifiedNo = choices.Count(choice => choice.IsVerified && choice.DiarizationSupport == DiarizationSupport.NotSupported);
                var unknown = choices.Count(choice => !choice.IsVerified || choice.DiarizationSupport == DiarizationSupport.Unknown);
                MessageBox.Show(
                    this,
                    $"Loaded {choices.Count} transcription-capable model(s) from Mistral.{Environment.NewLine}" +
                    $"Diarization support: Yes={verifiedYes}, No={verifiedNo}, Unknown={unknown}",
                    "Mistral models",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh Mistral model list.");
            if (showResultPopup)
            {
                MessageBox.Show(
                    this,
                    $"Failed to refresh models: {ex.Message}",
                    "Mistral models",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _refreshModelsButton.Text = "Refresh";
            _refreshModelsButton.Enabled = true;
        }
    }

    private async Task TestMistralApiAsync()
    {
        var apiKey = _apiKeyTextBox.Text.Trim();
        var model = ResolveSelectedModelId();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(this, "Please enter a Mistral API key first.", "Missing API key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            MessageBox.Show(this, "Please enter a Mistral model first.", "Missing model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _testApiButton.Enabled = false;
        _testApiButton.Text = "Testing...";
        UseWaitCursor = true;
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var client = new MistralTranscriptionClient(httpClient, apiKey, model);
            var wavPath = CreateProbeWaveFile();
            try
            {
                var start = DateTimeOffset.UtcNow;
                var job = new ChunkJob(
                    SessionId: "settings-api-probe",
                    Source: AudioSourceKind.Mic,
                    ChunkIndex: 1,
                    StartUtc: start,
                    EndUtc: start.AddSeconds(1),
                    WavPath: wavPath,
                    RetryCount: 0);

                var result = await client.TranscribeAsync(job, CancellationToken.None);

                if (result.Status == TranscriptionStatus.Success)
                {
                    var sampleText = string.IsNullOrWhiteSpace(result.Text) ? "(empty transcript is OK for probe audio)" : result.Text;
                    MessageBox.Show(
                        this,
                        $"API test succeeded.{Environment.NewLine}Model: {model}{Environment.NewLine}Response text: {sampleText}",
                        "Mistral API",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(
                        this,
                        $"API test failed.{Environment.NewLine}Model: {model}{Environment.NewLine}Status: {result.Status}{Environment.NewLine}Error: {result.Error}",
                        "Mistral API",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            finally
            {
                TryDeleteFile(wavPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to run Mistral API test.");
            MessageBox.Show(
                this,
                $"Could not run API test: {ex.Message}",
                "Mistral API",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _testApiButton.Text = "Test Mistral API";
            _testApiButton.Enabled = true;
        }
    }

    private static string CreateProbeWaveFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mistral-probe-{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(16000, 16, 1);
        var oneSecond = new byte[format.AverageBytesPerSecond];
        using var writer = new WaveFileWriter(path, format);
        writer.Write(oneSecond, 0, oneSecond.Length);
        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string ResolveSelectedModelId()
    {
        var typedValue = _modelCombo.Text.Trim();
        if (_modelCombo.SelectedItem is ModelChoice selected)
        {
            if (string.IsNullOrWhiteSpace(typedValue) ||
                string.Equals(typedValue, selected.ToString(), StringComparison.Ordinal) ||
                string.Equals(typedValue, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                return selected.Id;
            }
        }

        return typedValue;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string labelText, Control mainControl, Control? trailingControl = null)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = labelText,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 8)
        };

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(mainControl, 1, row);
        if (trailingControl is not null)
        {
            grid.Controls.Add(trailingControl, 2, row);
        }
    }

    private static void SelectDevice(ComboBox combo, string? preferredId, string? preferredName)
    {
        var best = combo.Items
            .OfType<DeviceChoice>()
            .FirstOrDefault(item => string.Equals(item.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items
                .OfType<DeviceChoice>()
                .FirstOrDefault(item => string.Equals(item.Name, preferredName, StringComparison.OrdinalIgnoreCase));

        if (best is not null)
        {
            combo.SelectedItem = best;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
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

    private sealed record DeviceChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ModelChoice(string Id, string Label, bool IsVerified, DiarizationSupport DiarizationSupport)
    {
        public static ModelChoice FromModelInfo(MistralModelInfo model, MistralDiarizationProbeResult probe)
        {
            var support = probe.IsVerified ? probe.Support : model.DiarizationSupport;
            var diarizationText = (support, probe.IsVerified) switch
            {
                (DiarizationSupport.Supported, true) => "Diarization: Yes",
                (DiarizationSupport.NotSupported, true) => "Diarization: No",
                (DiarizationSupport.Supported, false) => "Diarization: Yes (catalog)",
                (DiarizationSupport.NotSupported, false) => "Diarization: No (catalog)",
                _ => "Diarization: Unknown"
            };
            return new ModelChoice(model.Id, $"{model.Id} ({diarizationText})", probe.IsVerified, support);
        }

        public override string ToString() => Label;
    }
}

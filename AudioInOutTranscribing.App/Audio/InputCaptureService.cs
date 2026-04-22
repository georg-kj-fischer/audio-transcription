using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace AudioInOutTranscribing.App.Audio;

public sealed class InputCaptureService : IDisposable
{
    private WasapiCapture? _capture;

    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat? WaveFormat => _capture?.WaveFormat;

    public void Start(MMDevice inputDevice)
    {
        if (_capture is not null)
        {
            return;
        }

        _capture = new WasapiCapture(inputDevice);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        Log.Information("Microphone capture started. Device={DeviceName}", inputDevice.FriendlyName);
    }

    public void Stop()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.StopRecording();
    }

    public void Dispose()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);

        try
        {
            DataAvailable?.Invoke(this, new AudioDataEventArgs(copy, e.BytesRecorded));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception in microphone audio callback pipeline.");
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log.Warning(e.Exception, "Microphone capture stopped with error.");
        }

        try
        {
            RecordingStopped?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception while notifying microphone stop event.");
        }
    }
}

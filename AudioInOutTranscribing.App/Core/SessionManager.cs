using System.Threading.Channels;
using System.Runtime.InteropServices;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Transcription;
using NAudio.CoreAudioApi;
using Serilog;

namespace AudioInOutTranscribing.App.Core;

public sealed class SessionManager : IDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);
    private const int RecoveryMaxRetryAttempts = 5;

    private readonly DeviceEnumerator _deviceEnumerator;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private ActiveSession? _activeSession;
    private int _stopRequested;
    private int _emergencyStopRequested;

    public SessionManager(DeviceEnumerator deviceEnumerator)
    {
        _deviceEnumerator = deviceEnumerator;
        _httpClient = new HttpClient();
    }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public bool IsRecording => _activeSession is not null;

    public string? LastSessionPath { get; private set; }

    public async Task<PendingChunkRecoverySummary> RecoverPendingChunksAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeSession is not null)
            {
                return new PendingChunkRecoverySummary();
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (string.IsNullOrWhiteSpace(settings.OutputFolder) ||
            !Directory.Exists(settings.OutputFolder) ||
            string.IsNullOrWhiteSpace(settings.MistralApiKey))
        {
            return new PendingChunkRecoverySummary();
        }

        var transcriptionClient = new MistralTranscriptionClient(_httpClient, settings.MistralApiKey, settings.Model);
        var recoveryService = new PendingChunkRecoveryService(RecoveryMaxRetryAttempts);
        return await recoveryService.RecoverAsync(
            outputRoot: settings.OutputFolder,
            saveRawAudio: settings.SaveRawAudio,
            transcriptionClient: transcriptionClient,
            cancellationToken: cancellationToken);
    }

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        MMDevice? inputDevice = null;
        MMDevice? outputDevice = null;
        ActiveSession? pendingSession = null;

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeSession is not null)
            {
                return;
            }

            Interlocked.Exchange(ref _stopRequested, 0);
            Interlocked.Exchange(ref _emergencyStopRequested, 0);

            ValidateSettings(settings);

            inputDevice = _deviceEnumerator.ResolveInputDevice(settings.InputDeviceId, settings.InputDeviceName)
                ?? throw new InvalidOperationException("Input device could not be resolved.");
            outputDevice = _deviceEnumerator.ResolveOutputDevice(settings.OutputDeviceId, settings.OutputDeviceName)
                ?? throw new InvalidOperationException("Output device could not be resolved.");

            var nowLocal = DateTimeOffset.Now;
            var dateFolder = Path.Combine(settings.OutputFolder, nowLocal.ToString("yyyy-MM-dd"));
            var sessionId = $"session-{nowLocal:HHmmss}";
            var sessionRoot = Path.Combine(dateFolder, sessionId);
            var micFolder = Path.Combine(sessionRoot, "mic");
            var speakerFolder = Path.Combine(sessionRoot, "speaker");

            Directory.CreateDirectory(micFolder);
            Directory.CreateDirectory(speakerFolder);

            var chunkDuration = TimeSpan.FromSeconds(settings.ChunkSeconds);
            var micChunker = new AudioChunker(AudioSourceKind.Mic, inputDevice.AudioClient.MixFormat, chunkDuration, micFolder);
            var speakerChunker = new AudioChunker(AudioSourceKind.Speaker, outputDevice.AudioClient.MixFormat, chunkDuration, speakerFolder);

            var micChannel = Channel.CreateUnbounded<ChunkJob>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var speakerChannel = Channel.CreateUnbounded<ChunkJob>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            var transcriptWriter = new TranscriptWriter(sessionRoot);
            var transcriptionClient = new MistralTranscriptionClient(_httpClient, settings.MistralApiKey, settings.Model);
            var micWorker = new TranscriptionWorker(
                AudioSourceKind.Mic,
                micChannel.Reader,
                transcriptionClient,
                transcriptWriter,
                settings.SaveRawAudio);
            var speakerWorker = new TranscriptionWorker(
                AudioSourceKind.Speaker,
                speakerChannel.Reader,
                transcriptionClient,
                transcriptWriter,
                settings.SaveRawAudio);

            var workerCts = new CancellationTokenSource();
            var micWorkerTask = Task.Run(() => micWorker.RunAsync(workerCts.Token), workerCts.Token);
            var speakerWorkerTask = Task.Run(() => speakerWorker.RunAsync(workerCts.Token), workerCts.Token);

            var inputCapture = new InputCaptureService();
            var outputCapture = new OutputCaptureService();

            inputCapture.DataAvailable += (_, e) => OnCaptureDataAvailable(micChunker, e, AudioSourceKind.Mic);
            outputCapture.DataAvailable += (_, e) => OnCaptureDataAvailable(speakerChunker, e, AudioSourceKind.Speaker);
            inputCapture.RecordingStopped += (_, e) => OnCaptureStopped(e, AudioSourceKind.Mic);
            outputCapture.RecordingStopped += (_, e) => OnCaptureStopped(e, AudioSourceKind.Speaker);

            micChunker.ChunkReady += (_, e) => EnqueueChunk(e, sessionId, micChannel.Writer);
            speakerChunker.ChunkReady += (_, e) => EnqueueChunk(e, sessionId, speakerChannel.Writer);

            pendingSession = new ActiveSession(
                sessionId,
                sessionRoot,
                DateTimeOffset.UtcNow,
                inputDevice,
                outputDevice,
                inputCapture,
                outputCapture,
                micChunker,
                speakerChunker,
                micChannel,
                speakerChannel,
                micWorker,
                speakerWorker,
                micWorkerTask,
                speakerWorkerTask,
                transcriptWriter,
                workerCts);

            inputDevice = null;
            outputDevice = null;

            inputCapture.Start(pendingSession.InputDevice);
            outputCapture.Start(pendingSession.OutputDevice);

            _activeSession = pendingSession;
            pendingSession = null;

            LastSessionPath = sessionRoot;
            Log.Information("Recording started. session={SessionId} path={SessionRoot}", sessionId, sessionRoot);
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(TrayState.Recording, "Recording in progress."));
        }
        catch (Exception ex)
        {
            pendingSession?.Dispose();
            inputDevice?.Dispose();
            outputDevice?.Dispose();
            Log.Error(ex, "Failed to start recording session.");
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(TrayState.Error, ex.Message));
            throw;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<RecordingSessionSummary?> StopAsync(CancellationToken cancellationToken = default)
    {
        ActiveSession? session;

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            session = _activeSession;
            _activeSession = null;
        }
        finally
        {
            _stateGate.Release();
        }

        if (session is null)
        {
            return null;
        }

        try
        {
            Interlocked.Exchange(ref _stopRequested, 1);

            session.InputCapture.Stop();
            session.OutputCapture.Stop();
            session.MicChunker.Flush();
            session.SpeakerChunker.Flush();

            session.MicChannel.Writer.TryComplete();
            session.SpeakerChannel.Writer.TryComplete();

            var combinedWorkers = Task.WhenAll(session.MicWorkerTask, session.SpeakerWorkerTask);
            try
            {
                using var drainCts = new CancellationTokenSource(DrainTimeout);
                await combinedWorkers.WaitAsync(drainCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Timed out while draining transcription queue. Cancelling remaining work.");
                session.WorkerCancellation.Cancel();
                try
                {
                    await combinedWorkers;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Workers aborted after drain timeout.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Transcription workers terminated with an unexpected error.");
            }

            var summary = new RecordingSessionSummary
            {
                SessionId = session.SessionId,
                SessionRootPath = session.SessionRoot,
                StartedUtc = session.StartedUtc,
                EndedUtc = DateTimeOffset.UtcNow,
                Mic = session.MicWorker.Summary,
                Speaker = session.SpeakerWorker.Summary
            };

            await session.TranscriptWriter.WriteSessionSummaryAsync(summary, CancellationToken.None);
            Log.Information("Recording stopped. session={SessionId} path={SessionRoot}", session.SessionId, session.SessionRoot);

            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(TrayState.Idle, "Recording stopped."));
            return summary;
        }
        finally
        {
            session.Dispose();
            Interlocked.Exchange(ref _stopRequested, 0);
            Interlocked.Exchange(ref _emergencyStopRequested, 0);
        }
    }

    public void Dispose()
    {
        try
        {
            _ = StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Session manager dispose encountered an error while stopping active session.");
        }

        _httpClient.Dispose();
        _stateGate.Dispose();
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
        {
            throw new InvalidOperationException("Output folder is required.");
        }

        if (settings.ChunkSeconds < 5)
        {
            throw new InvalidOperationException("Chunk duration must be at least 5 seconds.");
        }

        if (string.IsNullOrWhiteSpace(settings.MistralApiKey))
        {
            throw new InvalidOperationException("Mistral API key is missing in Settings.");
        }
    }

    private static void EnqueueChunk(AudioChunkReadyEventArgs chunk, string sessionId, ChannelWriter<ChunkJob> writer)
    {
        var job = new ChunkJob(
            sessionId,
            chunk.Source,
            chunk.ChunkIndex,
            chunk.StartUtc,
            chunk.EndUtc,
            chunk.WavPath,
            0);

        if (!writer.TryWrite(job))
        {
            Log.Warning("Failed to enqueue chunk. source={Source} index={ChunkIndex}", chunk.Source, chunk.ChunkIndex);
        }
    }

    private void OnCaptureStopped(NAudio.Wave.StoppedEventArgs args, AudioSourceKind source)
    {
        if (Volatile.Read(ref _stopRequested) == 1)
        {
            return;
        }

        if (args.Exception is not null)
        {
            var reason = DescribeCaptureException(args.Exception);
            TriggerEmergencyStop(source, $"capture stopped with error: {reason}", args.Exception);
            return;
        }

        TriggerEmergencyStop(source, "capture stopped unexpectedly.", null);
    }

    private void OnCaptureDataAvailable(AudioChunker chunker, AudioDataEventArgs args, AudioSourceKind source)
    {
        try
        {
            chunker.AddSamples(args.Buffer, args.BytesRecorded);
        }
        catch (Exception ex)
        {
            TriggerEmergencyStop(source, "capture pipeline failed while chunking audio.", ex);
        }
    }

    private void TriggerEmergencyStop(AudioSourceKind source, string reason, Exception? ex)
    {
        var message = $"{source} {reason}";
        if (ex is null)
        {
            Log.Warning("{Message}", message);
        }
        else
        {
            Log.Error(ex, "{Message}", message);
        }

        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(TrayState.Error, message));

        if (Interlocked.CompareExchange(ref _emergencyStopRequested, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync();
            }
            catch (Exception stopEx)
            {
                Log.Error(stopEx, "Emergency stop failed after capture pipeline fault.");
            }
            finally
            {
                Interlocked.Exchange(ref _emergencyStopRequested, 0);
            }
        });
    }

    private static string DescribeCaptureException(Exception ex)
    {
        if (ex is COMException comException)
        {
            var code = unchecked((uint)comException.ErrorCode);
            if (code == 0x88890004)
            {
                return "audio device was disconnected or changed (AUDCLNT_E_DEVICE_INVALIDATED, 0x88890004)";
            }

            return $"COM error 0x{code:X8}";
        }

        return ex.Message;
    }

    private sealed class ActiveSession : IDisposable
    {
        public ActiveSession(
            string sessionId,
            string sessionRoot,
            DateTimeOffset startedUtc,
            MMDevice inputDevice,
            MMDevice outputDevice,
            InputCaptureService inputCapture,
            OutputCaptureService outputCapture,
            AudioChunker micChunker,
            AudioChunker speakerChunker,
            Channel<ChunkJob> micChannel,
            Channel<ChunkJob> speakerChannel,
            TranscriptionWorker micWorker,
            TranscriptionWorker speakerWorker,
            Task micWorkerTask,
            Task speakerWorkerTask,
            TranscriptWriter transcriptWriter,
            CancellationTokenSource workerCancellation)
        {
            SessionId = sessionId;
            SessionRoot = sessionRoot;
            StartedUtc = startedUtc;
            InputDevice = inputDevice;
            OutputDevice = outputDevice;
            InputCapture = inputCapture;
            OutputCapture = outputCapture;
            MicChunker = micChunker;
            SpeakerChunker = speakerChunker;
            MicChannel = micChannel;
            SpeakerChannel = speakerChannel;
            MicWorker = micWorker;
            SpeakerWorker = speakerWorker;
            MicWorkerTask = micWorkerTask;
            SpeakerWorkerTask = speakerWorkerTask;
            TranscriptWriter = transcriptWriter;
            WorkerCancellation = workerCancellation;
        }

        public string SessionId { get; }

        public string SessionRoot { get; }

        public DateTimeOffset StartedUtc { get; }

        public MMDevice InputDevice { get; }

        public MMDevice OutputDevice { get; }

        public InputCaptureService InputCapture { get; }

        public OutputCaptureService OutputCapture { get; }

        public AudioChunker MicChunker { get; }

        public AudioChunker SpeakerChunker { get; }

        public Channel<ChunkJob> MicChannel { get; }

        public Channel<ChunkJob> SpeakerChannel { get; }

        public TranscriptionWorker MicWorker { get; }

        public TranscriptionWorker SpeakerWorker { get; }

        public Task MicWorkerTask { get; }

        public Task SpeakerWorkerTask { get; }

        public TranscriptWriter TranscriptWriter { get; }

        public CancellationTokenSource WorkerCancellation { get; }

        public void Dispose()
        {
            WorkerCancellation.Cancel();
            WorkerCancellation.Dispose();
            InputCapture.Dispose();
            OutputCapture.Dispose();
            InputDevice.Dispose();
            OutputDevice.Dispose();
        }
    }
}

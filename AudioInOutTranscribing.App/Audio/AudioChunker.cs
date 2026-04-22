using NAudio.Wave;

namespace AudioInOutTranscribing.App.Audio;

public sealed class AudioChunker
{
    private readonly object _gate = new();
    private readonly AudioSourceKind _source;
    private readonly WaveFormat _waveFormat;
    private readonly int _targetChunkBytes;
    private readonly string _outputDirectory;
    private readonly MemoryStream _pendingBuffer = new();
    private int _chunkIndex = 1;
    private DateTimeOffset? _currentChunkStartUtc;

    public AudioChunker(
        AudioSourceKind source,
        WaveFormat waveFormat,
        TimeSpan chunkDuration,
        string outputDirectory)
    {
        if (chunkDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkDuration));
        }

        _source = source;
        _waveFormat = waveFormat;
        _outputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);

        var rawTarget = (int)Math.Round(_waveFormat.AverageBytesPerSecond * chunkDuration.TotalSeconds);
        _targetChunkBytes = AlignToBlockBoundary(rawTarget, _waveFormat.BlockAlign);
    }

    public event EventHandler<AudioChunkReadyEventArgs>? ChunkReady;

    public void AddSamples(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_currentChunkStartUtc is null)
            {
                _currentChunkStartUtc = DateTimeOffset.UtcNow;
            }

            _pendingBuffer.Position = _pendingBuffer.Length;
            _pendingBuffer.Write(buffer, 0, bytesRecorded);

            while (_pendingBuffer.Length >= _targetChunkBytes)
            {
                EmitNextChunk(_targetChunkBytes);
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_pendingBuffer.Length <= 0)
            {
                return;
            }

            EmitNextChunk((int)_pendingBuffer.Length);
        }
    }

    private void EmitNextChunk(int size)
    {
        _pendingBuffer.Position = 0;
        var chunkData = new byte[size];
        _ = _pendingBuffer.Read(chunkData, 0, size);

        var remainingBytes = (int)_pendingBuffer.Length - size;
        var tail = new byte[remainingBytes];
        _ = _pendingBuffer.Read(tail, 0, remainingBytes);
        _pendingBuffer.SetLength(0);
        _pendingBuffer.Position = 0;
        _pendingBuffer.Write(tail, 0, remainingBytes);

        var chunkStart = _currentChunkStartUtc ?? DateTimeOffset.UtcNow;
        var chunkDuration = TimeSpan.FromSeconds(size / (double)_waveFormat.AverageBytesPerSecond);
        var chunkEnd = chunkStart + chunkDuration;
        var wavPath = Path.Combine(_outputDirectory, $"{_chunkIndex:0000}.wav");

        WaveFileChunkWriter.WriteChunk(wavPath, _waveFormat, chunkData, chunkData.Length);

        ChunkReady?.Invoke(this, new AudioChunkReadyEventArgs(_source, _chunkIndex, wavPath, chunkStart, chunkEnd, size));

        _chunkIndex++;
        _currentChunkStartUtc = chunkEnd;

        if (_pendingBuffer.Length == 0)
        {
            _currentChunkStartUtc = null;
        }
    }

    private static int AlignToBlockBoundary(int bytes, int blockAlign)
    {
        if (blockAlign <= 0)
        {
            return Math.Max(bytes, 1);
        }

        var remainder = bytes % blockAlign;
        if (remainder == 0)
        {
            return bytes;
        }

        return bytes + (blockAlign - remainder);
    }
}

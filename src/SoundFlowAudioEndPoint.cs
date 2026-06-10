using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Components;
using SoundFlow.Providers;
using SIPSorceryMedia.Abstractions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SIPSorceryMedia.SoundFlow;

/// <summary>Performance statistics for the audio sink.</summary>
public struct AudioSinkStats
{
    public long UnderrunCount { get; set; }
    public long DroppedFrames { get; set; }
    public int  QueueDepth    { get; set; }
    public bool IsActive      { get; set; }
}

public class SoundFlowAudioEndPoint : IAudioSink, IDisposable
{
    private const int MAX_AUDIO_RENT    = 1024 * 1024;
    private const int MAX_QUEUE_FRAMES  = 10;
    // ~500ms of F32 mono at 16kHz
    private const int QUEUE_MAX_SAMPLES = 8000;

    private readonly ILogger log = SIPSorcery.LogFactory.CreateLogger<SoundFlowAudioEndPoint>();

    private readonly string _deviceNameHint;
    private readonly IAudioEncoder _audioEncoder;
    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;

    private AudioPlaybackDevice? _playbackDevice;
    private QueueDataProvider?   _queueProvider;
    private SoundPlayer?         _player;

    private readonly object _stateLock = new object();
    private bool _isStarted = false;
    private bool _isPaused  = true;
    private bool _disposed  = false;

    private readonly Channel<(byte[] Buffer, int Length)> _playbackChannel;
    private CancellationTokenSource? _playCts;
    private Task? _playbackTask;

    private long _underrunCount = 0;
    private long _droppedFrames = 0;
    private int  _channelCount  = 0;

    public event SourceErrorDelegate? OnAudioSinkError;

    public SoundFlowAudioEndPoint(string audioOutDeviceName, IAudioEncoder audioEncoder)
    {
        if (audioEncoder == null) throw new ArgumentNullException(nameof(audioEncoder));

        _deviceNameHint    = audioOutDeviceName ?? string.Empty;
        _audioEncoder      = audioEncoder;
        _audioFormatManager = new MediaFormatManager<AudioFormat>(audioEncoder.SupportedFormats);

        _playbackChannel = Channel.CreateBounded<(byte[], int)>(
            new BoundedChannelOptions(MAX_QUEUE_FRAMES)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    ~SoundFlowAudioEndPoint() => Dispose(false);

    // -------------------------------------------------------------------------
    // Device init / teardown
    // -------------------------------------------------------------------------

    private void InitPlaybackDevice()
    {
        SoundPlayer?         prevPlayer = null;
        AudioPlaybackDevice? prevDevice = null;

        lock (_stateLock)
        {
            prevPlayer = _player;
            prevDevice = _playbackDevice;
            _player        = null;
            _queueProvider = null;
            _playbackDevice = null;
            _isStarted = false;
            _isPaused  = true;
        }

        if (prevPlayer != null)
        {
            try { prevPlayer.Stop();    } catch (Exception) { }
            try { prevPlayer.Dispose(); } catch (Exception) { }
        }
        if (prevDevice != null)
        {
            try { prevDevice.Stop();    } catch (Exception) { }
            try { prevDevice.Dispose(); } catch (Exception) { }
        }

        try
        {
            AudioFormat audioFormat = _audioFormatManager.SelectedFormat;
            var sfFormat   = SoundFlowHelper.ToSoundFlowFormat(audioFormat.ClockRate, channels: 1);
            var deviceInfo = SoundFlowHelper.FindPlaybackDevice(_deviceNameHint);

            var newDevice   = SoundFlowHelper.Engine.InitializePlaybackDevice(deviceInfo, sfFormat);
            var newProvider = new QueueDataProvider(sfFormat, QUEUE_MAX_SAMPLES, QueueFullBehavior.Drop);
            var newPlayer   = new SoundPlayer(SoundFlowHelper.Engine, sfFormat, newProvider);
            newDevice.MasterMixer.AddComponent(newPlayer);

            lock (_stateLock)
            {
                _playbackDevice = newDevice;
                _queueProvider  = newProvider;
                _player         = newPlayer;
            }

            log.LogDebug("[InitPlaybackDevice] SoundFlowAudioEndPoint opened playback device.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[InitPlaybackDevice] Failed to initialise playback device.");
            RaiseAudioSinkError($"SoundFlowAudioEndPoint failed to open playback device: {ex.Message}");
        }
    }

    private void CloseSync()
    {
        SoundPlayer?         playerToStop;
        AudioPlaybackDevice? deviceToStop;

        lock (_stateLock)
        {
            playerToStop   = _player;
            deviceToStop   = _playbackDevice;
            _player        = null;
            _queueProvider = null;
            _playbackDevice = null;
            _isStarted = false;
            _isPaused  = true;
        }

        if (playerToStop != null)
        {
            try { playerToStop.Stop();    } catch (Exception) { }
            try { playerToStop.Dispose(); } catch (Exception) { }
        }
        if (deviceToStop != null)
        {
            try { deviceToStop.Stop();    } catch (Exception) { }
            try { deviceToStop.Dispose(); } catch (Exception) { }
        }

        var pool = ArrayPool<byte>.Shared;
        while (_playbackChannel.Reader.TryRead(out var seg))
        {
            Interlocked.Decrement(ref _channelCount);
            try { pool.Return(seg.Buffer); } catch (Exception) { }
        }
    }

    private void RaiseAudioSinkError(string err)
    {
        _ = CloseAudioSinkAsync();
        OnAudioSinkError?.Invoke(err);
    }

    // -------------------------------------------------------------------------
    // Playback worker — S16 bytes → F32 normalized → QueueDataProvider
    // -------------------------------------------------------------------------

    private async Task PlaybackWorkerAsync(CancellationToken ct)
    {
        var bytePool  = ArrayPool<byte>.Shared;
        var floatPool = ArrayPool<float>.Shared;

        try
        {
            await foreach (var (buf, len) in _playbackChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _channelCount);

                if (buf == null || len <= 0 || len > buf.Length)
                {
                    if (buf != null) try { bytePool.Return(buf); } catch (Exception) { }
                    continue;
                }

                QueueDataProvider? provider;
                lock (_stateLock) { provider = _queueProvider; }

                if (provider == null)
                {
                    try { bytePool.Return(buf); } catch (Exception) { }
                    continue;
                }

                try
                {
                    if (provider.SamplesAvailable == 0)
                        Interlocked.Increment(ref _underrunCount);

                    int sampleCount = len / 2;
                    float[] floats  = floatPool.Rent(sampleCount);
                    try
                    {
                        for (int i = 0; i < sampleCount; i++)
                        {
                            short s   = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(i * 2));
                            floats[i] = s / 32768f;
                        }
                        provider.AddSamples(floats.AsSpan(0, sampleCount));
                    }
                    finally { floatPool.Return(floats); }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { log.LogError(ex, "PlaybackWorker: error converting/queuing audio"); }
                finally
                { try { bytePool.Return(buf); } catch (Exception) { } }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { log.LogError(ex, "PlaybackWorker: unexpected error"); }
    }

    // -------------------------------------------------------------------------
    // IAudioSink public surface
    // -------------------------------------------------------------------------

    public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
    {
        var audioFormat = encodedMediaFrame.AudioFormat;
        if (!audioFormat.IsEmpty())
        {
            var pcm      = _audioEncoder.DecodeAudio(encodedMediaFrame.EncodedAudio, audioFormat);
            var pcmBytes = new byte[pcm.Length * sizeof(short)];
            Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
            PutAudioSample(pcmBytes);
        }
    }

    /// <summary>Queues raw PCM bytes (S16 LE) for playback. Oldest frames are dropped when the queue is full.</summary>
    public void PutAudioSample(byte[] pcmSample)
    {
        if (pcmSample == null || pcmSample.Length == 0) return;

        if (pcmSample.Length > MAX_AUDIO_RENT)
        {
            log.LogWarning("PutAudioSample: {Size} bytes exceeds cap, dropping", pcmSample.Length);
            return;
        }

        var pool = ArrayPool<byte>.Shared;
        var buf  = pool.Rent(pcmSample.Length);
        Buffer.BlockCopy(pcmSample, 0, buf, 0, pcmSample.Length);

        int depth = Interlocked.Increment(ref _channelCount);
        if (depth > MAX_QUEUE_FRAMES)
            Interlocked.Increment(ref _droppedFrames);

        if (!_playbackChannel.Writer.TryWrite((buf, pcmSample.Length)))
        {
            Interlocked.Decrement(ref _channelCount);
            try { pool.Return(buf); } catch (Exception) { }
        }
    }

    [Obsolete("Use GotEncodedMediaFrame instead.")]
    public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp,
        int payloadID, bool marker, byte[] payload)
    {
        var pcm      = _audioEncoder.DecodeAudio(payload, _audioFormatManager.SelectedFormat);
        var pcmBytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
        PutAudioSample(pcmBytes);
    }

    public void SetAudioSinkFormat(AudioFormat audioFormat)
    {
        _audioFormatManager.SetSelectedFormat(audioFormat);
        InitPlaybackDevice();
        StartAudioSink();
    }

    public List<AudioFormat> GetAudioSinkFormats() => _audioFormatManager.GetSourceFormats();

    public void RestrictFormats(Func<AudioFormat, bool> filter) =>
        _audioFormatManager.RestrictFormats(filter);

    public MediaEndPoints ToMediaEndPoints() => new MediaEndPoints { AudioSink = this };

    /// <summary>
    /// The underlying SoundFlow playback device.
    /// Pass this to <see cref="SoundFlowAudioSource.EnableAudioProcessing"/> as the
    /// <c>referenceDevice</c> when echo cancellation is required.
    /// </summary>
    public AudioPlaybackDevice? PlaybackDevice
    {
        get { lock (_stateLock) { return _playbackDevice; } }
    }

    /// <summary>Playback volume as a gain multiplier. 1.0 = unity gain, 0.0 = silent.</summary>
    public float Volume
    {
        get
        {
            SoundPlayer? player;
            lock (_stateLock) { player = _player; }
            return player?.Volume ?? 1f;
        }
        set
        {
            SoundPlayer? player;
            lock (_stateLock) { player = _player; }
            if (player != null) player.Volume = Math.Max(0f, value);
        }
    }

    // -------------------------------------------------------------------------
    // Start / pause / resume / close
    // -------------------------------------------------------------------------

    public Task StartAudioSink() => StartAudioSinkAsync();

    public async Task StartAudioSinkAsync()
    {
        bool needResume = false;
        lock (_stateLock)
        {
            if (!_isStarted && _playbackDevice != null)
            {
                _isStarted = true;
                _isPaused  = true;
                needResume = true;
            }
        }
        if (needResume)
            await ResumeAudioSink().ConfigureAwait(false);
    }

    public Task PauseAudioSink()
    {
        bool doPause = false;
        SoundPlayer? player = null;

        lock (_stateLock)
        {
            if (_isStarted && !_isPaused)
            {
                _isPaused = true;
                doPause   = true;
                player    = _player;
            }
        }

        if (doPause)
        {
            try { _playCts?.Cancel(); } catch (Exception) { }
            if (player != null) try { player.Pause(); } catch (Exception) { }
            log.LogDebug("[PauseAudioSink] SoundFlowAudioEndPoint paused.");
        }

        return Task.CompletedTask;
    }

    public Task ResumeAudioSink()
    {
        bool doResume = false;
        AudioPlaybackDevice? dev    = null;
        SoundPlayer?         player = null;

        lock (_stateLock)
        {
            if (_isStarted && _isPaused)
            {
                _isPaused = false;
                doResume  = true;
                dev    = _playbackDevice;
                player = _player;
            }
        }

        if (doResume && dev != null && player != null)
        {
            StartPlaybackTask();
            try { dev.Start();   } catch (Exception) { }
            try { player.Play(); } catch (Exception) { }
            log.LogDebug("[ResumeAudioSink] SoundFlowAudioEndPoint resumed.");
        }

        return Task.CompletedTask;
    }

    private void StartPlaybackTask()
    {
        var oldCts = _playCts;
        _playCts   = new CancellationTokenSource();
        try { oldCts?.Cancel(); } catch (Exception) { }
        oldCts?.Dispose();
        _playbackTask = Task.Run(() => PlaybackWorkerAsync(_playCts.Token));
    }

    public Task CloseAudioSink() => CloseAudioSinkAsync();

    public async Task CloseAudioSinkAsync()
    {
        await PauseAudioSink().ConfigureAwait(false);
        CloseSync();
    }

    // -------------------------------------------------------------------------
    // Stats
    // -------------------------------------------------------------------------

    public AudioSinkStats GetStats()
    {
        bool isActive;
        lock (_stateLock) { isActive = _isStarted && !_isPaused; }
        return new AudioSinkStats
        {
            UnderrunCount = Interlocked.Read(ref _underrunCount),
            DroppedFrames = Interlocked.Read(ref _droppedFrames),
            QueueDepth    = Interlocked.CompareExchange(ref _channelCount, 0, 0),
            IsActive      = isActive,
        };
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref _underrunCount, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try { _playCts?.Cancel(); } catch (Exception) { }
            CloseSync();
            _playbackChannel.Writer.TryComplete();
            _playCts?.Dispose();
            _playCts = null;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SIPSorceryMedia.Abstractions;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SIPSorceryMedia.SoundFlow;

/// <summary>Performance statistics for the audio source.</summary>
public struct AudioSourceStats
{
    public long OverrunCount { get; set; }
    public long DroppedFrames { get; set; }
    public bool IsActive { get; set; }
}

public class SoundFlowAudioSource : IAudioSource, IDisposable
{
    private const int MAX_AUDIO_RENT = 1024 * 1024;
    private const int CHANNEL_CAPACITY = 25;

    private static readonly ILogger log = SIPSorcery.LogFactory.CreateLogger<SoundFlowAudioSource>();

    private readonly string _deviceNameHint;
    private readonly IAudioEncoder _audioEncoder;
    private readonly MediaFormatManager<AudioFormat> _audioFormatManager;
    private readonly int _frameSize;

    private AudioCaptureDevice? _captureDevice;
    private Recorder? _recorder;
    private AudioSamplingRatesEnum _audioSamplingRates;

    private readonly object _stateLock = new object();
    private bool _isStarted = false;
    private bool _isPaused  = true;
    private bool _disposed  = false;

    private readonly Channel<(byte[] Buffer, int Length)> _callbackChannel;
    private CancellationTokenSource? _callbackCts;
    private Task? _callbackTask;

    private long  _droppedFrames = 0;
    private float _volumeGain    = 1.0f;

#region IAudioSource events
    public event EncodedSampleDelegate  OnAudioSourceEncodedSample      = null!;
    public event RawAudioSampleDelegate OnAudioSourceRawSample           = null!;
    public event SourceErrorDelegate    OnAudioSourceError               = null!;
    public event Action<EncodedAudioFrame> OnAudioSourceEncodedFrameReady = null!;
#endregion

    public SoundFlowAudioSource(string audioInDeviceName, IAudioEncoder audioEncoder, int frameSize = 1920)
    {
        if (audioEncoder == null) throw new ArgumentNullException(nameof(audioEncoder));

        _deviceNameHint   = audioInDeviceName ?? string.Empty;
        _audioEncoder     = audioEncoder;
        _frameSize        = frameSize;
        _audioFormatManager = new MediaFormatManager<AudioFormat>(audioEncoder.SupportedFormats);

        _callbackChannel = Channel.CreateBounded<(byte[], int)>(
            new BoundedChannelOptions(CHANNEL_CAPACITY)
            {
                FullMode      = BoundedChannelFullMode.DropOldest,
                SingleReader  = true,
                SingleWriter  = false,
            });
    }

    ~SoundFlowAudioSource() => Dispose(false);

    // -------------------------------------------------------------------------
    // Device init / teardown
    // -------------------------------------------------------------------------

    private void InitRecordingDevice()
    {
        CloseSync();

        try
        {
            AudioFormat audioFormat = _audioFormatManager.SelectedFormat;
            _audioSamplingRates = audioFormat.ClockRate == AudioFormat.DEFAULT_CLOCK_RATE * 2
                ? AudioSamplingRatesEnum.Rate16KHz
                : AudioSamplingRatesEnum.Rate8KHz;

            var sfFormat    = SoundFlowHelper.ToSoundFlowFormat(audioFormat.ClockRate, channels: 1);
            var deviceInfo  = SoundFlowHelper.FindCaptureDevice(_deviceNameHint);
            var newDevice   = SoundFlowHelper.Engine.InitializeCaptureDevice(deviceInfo, sfFormat);
            var newRecorder = new Recorder(newDevice, (AudioProcessCallback)OnSoundFlowCapture);

            lock (_stateLock)
            {
                _captureDevice = newDevice;
                _recorder      = newRecorder;
            }

            log.LogDebug("[InitRecordingDevice] SoundFlowAudioSource opened capture device.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[InitRecordingDevice] Failed to initialise capture device.");
            OnAudioSourceError?.Invoke($"SoundFlowAudioSource failed to open capture device: {ex.Message}");
        }
    }

    private void CloseSync()
    {
        Recorder?          recToStop;
        AudioCaptureDevice? devToStop;

        lock (_stateLock)
        {
            recToStop  = _recorder;
            devToStop  = _captureDevice;
            _recorder      = null;
            _captureDevice = null;
            _isStarted = false;
            _isPaused  = true;
        }

        try { _callbackCts?.Cancel(); } catch (Exception) { }

        if (recToStop != null)
        {
            try { recToStop.StopRecording(); } catch (Exception) { }
            try { recToStop.Dispose();       } catch (Exception) { }
        }

        if (devToStop != null)
        {
            try { devToStop.Stop();    } catch (Exception) { }
            try { devToStop.Dispose(); } catch (Exception) { }
        }
    }

    // -------------------------------------------------------------------------
    // SoundFlow capture callback — F32 normalized → S16 LE bytes → channel
    // -------------------------------------------------------------------------

    private void OnSoundFlowCapture(Span<float> samples, Capability cap)
    {
        if (cap != Capability.Record || _disposed || samples.IsEmpty) return;

        var pool      = ArrayPool<byte>.Shared;
        int sampleCount = Math.Min(samples.Length, MAX_AUDIO_RENT / 2);
        int byteCount   = sampleCount * 2;
        byte[] rented   = pool.Rent(byteCount);

        float gain = _volumeGain;
        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short s = (short)Math.Clamp(samples[i] * gain * 32767f, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(rented.AsSpan(i * 2), s);
            }

            if (!_callbackChannel.Writer.TryWrite((rented, byteCount)))
            {
                Interlocked.Increment(ref _droppedFrames);
                pool.Return(rented);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            log.LogError(ex, "OnSoundFlowCapture: error converting audio");
            try { pool.Return(rented); } catch (Exception) { }
        }
    }

    // -------------------------------------------------------------------------
    // Callback worker — reads channel, calls ProcessAudioBuffer
    // -------------------------------------------------------------------------

    private async Task CallbackWorkerLoopAsync(CancellationToken ct)
    {
        var pool   = ArrayPool<byte>.Shared;
        var reader = _callbackChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                int processed = 0;
                while (processed < 5 && reader.TryRead(out var seg))
                {
                    processed++;
                    try   { ProcessAudioBuffer(seg.Buffer, seg.Length); }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    { log.LogError(ex, "CallbackWorker: error processing audio buffer"); }
                    finally { pool.Return(seg.Buffer); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { log.LogError(ex, "CallbackWorkerLoopAsync: unexpected error"); }
    }

    private void ProcessAudioBuffer(byte[] buffer, int length)
    {
        int shortCount = length / sizeof(short);
        var pool = ArrayPool<short>.Shared;
        var pcm  = pool.Rent(shortCount);
        try
        {
            Buffer.BlockCopy(buffer, 0, pcm, 0, shortCount * sizeof(short));

            OnAudioSourceRawSample?.Invoke(_audioSamplingRates, (uint)shortCount, pcm);

            if (OnAudioSourceEncodedSample != null || OnAudioSourceEncodedFrameReady != null)
            {
                var encodedSample = _audioEncoder.EncodeAudio(pcm, _audioFormatManager.SelectedFormat);
                if (encodedSample.Length > 0)
                {
                    var fmt        = _audioFormatManager.SelectedFormat;
                    uint durationRtp = (uint)(shortCount * fmt.RtpClockRate / fmt.ClockRate);
                    uint durationMs  = (uint)(shortCount * 1000 / fmt.ClockRate);

                    OnAudioSourceEncodedSample?.Invoke(durationRtp, encodedSample);
                    OnAudioSourceEncodedFrameReady?.Invoke(new EncodedAudioFrame(0, fmt, durationMs, encodedSample));
                }
            }
        }
        finally { pool.Return(pcm); }
    }

    // -------------------------------------------------------------------------
    // Start / pause / resume / close
    // -------------------------------------------------------------------------

    public Task StartAudio() => StartAudioAsync();

    public async Task StartAudioAsync()
    {
        bool needResume = false;
        lock (_stateLock)
        {
            if (!_isStarted && _captureDevice != null)
            {
                _isStarted = true;
                _isPaused  = true;
                needResume = true;
            }
        }
        if (needResume)
            await ResumeAudio().ConfigureAwait(false);
    }

    public Task PauseAudio()
    {
        bool doPause = false;
        Recorder? rec = null;

        lock (_stateLock)
        {
            if (_isStarted && !_isPaused)
            {
                _isPaused = true;
                doPause   = true;
                rec       = _recorder;
            }
        }

        if (doPause)
        {
            try { _callbackCts?.Cancel(); } catch (Exception) { }
            if (rec != null)
            {
                try { rec.PauseRecording(); } catch (Exception) { }
            }
            log.LogDebug("[PauseAudio] SoundFlowAudioSource paused.");
        }

        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        bool doResume = false;
        AudioCaptureDevice? dev = null;
        Recorder?           rec = null;

        lock (_stateLock)
        {
            if (_isStarted && _isPaused)
            {
                _isPaused = false;
                doResume  = true;
                dev = _captureDevice;
                rec = _recorder;
            }
        }

        if (doResume && dev != null && rec != null)
        {
            var oldCts = _callbackCts;
            _callbackCts = new CancellationTokenSource();
            try { oldCts?.Cancel(); } catch (Exception) { }
            oldCts?.Dispose();
            _callbackTask = Task.Run(() => CallbackWorkerLoopAsync(_callbackCts.Token));

            try { dev.Start();                 } catch (Exception) { }
            try { rec.StartRecording(null!);   } catch (Exception) { }
            log.LogDebug("[ResumeAudio] SoundFlowAudioSource resumed.");
        }

        return Task.CompletedTask;
    }

    public Task CloseAudio() => CloseAudioAsync();

    public Task CloseAudioAsync()
    {
        CloseSync();
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // IAudioSource helpers
    // -------------------------------------------------------------------------

    public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        log.LogDebug("SoundFlowAudioSource: SetAudioSourceFormat {Id}:{Name} {Rate}Hz",
            audioFormat.FormatID, audioFormat.FormatName, audioFormat.ClockRate);
        _audioFormatManager.SetSelectedFormat(audioFormat);
        try
        {
            InitRecordingDevice();
            StartAudio().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SetAudioSourceFormat failed");
            OnAudioSourceError?.Invoke($"SetAudioSourceFormat failed: {ex.Message}");
        }
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter) =>
        _audioFormatManager.RestrictFormats(filter);

    public void ExternalAudioSourceRawSample(
        AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        if (sample == null || sample.Length == 0) return;
        var bytes = new byte[sample.Length * sizeof(short)];
        Buffer.BlockCopy(sample, 0, bytes, 0, bytes.Length);
        ProcessAudioBuffer(bytes, bytes.Length);
    }

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;

    public bool IsAudioSourcePaused()
    {
        lock (_stateLock) { return _isPaused; }
    }

    /// <summary>Capture volume as a gain multiplier applied during F32→S16 conversion. 1.0 = unity gain.</summary>
    public float Volume
    {
        get => Volatile.Read(ref _volumeGain);
        set => Volatile.Write(ref _volumeGain, Math.Max(0f, value));
    }

    public AudioSourceStats GetStats()
    {
        bool isActive;
        lock (_stateLock) { isActive = _isStarted && !_isPaused; }
        return new AudioSourceStats
        {
            DroppedFrames = Interlocked.Read(ref _droppedFrames),
            IsActive      = isActive,
        };
    }

    public void ResetStats() => Interlocked.Exchange(ref _droppedFrames, 0);

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try { _callbackCts?.Cancel(); } catch (Exception) { }
            CloseSync();
            _callbackChannel.Writer.TryComplete();
            var pool = ArrayPool<byte>.Shared;
            while (_callbackChannel.Reader.TryRead(out var seg))
                try { pool.Return(seg.Buffer); } catch (Exception) { }
            _callbackCts?.Dispose();
            _callbackCts = null;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

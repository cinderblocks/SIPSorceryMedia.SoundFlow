using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;
using System;
using System.Linq;

namespace SIPSorceryMedia.SoundFlow;

using SFAudioFormat = global::SoundFlow.Structs.AudioFormat;

/// <summary>
/// Shared utilities for SoundFlow device lookup and format mapping.
/// </summary>
public static class SoundFlowHelper
{
    // One engine per process — SoundFlow devices share it; disposal at process exit is acceptable.
    private static readonly Lazy<MiniAudioEngine> _engine =
        new Lazy<MiniAudioEngine>(() => new MiniAudioEngine());

    public static MiniAudioEngine Engine => _engine.Value;

    /// <summary>
    /// Finds a capture device whose name contains <paramref name="nameHint"/> (case-insensitive).
    /// Returns null if the hint is empty or no device matches — callers treat null as "use default".
    /// </summary>
    public static DeviceInfo? FindCaptureDevice(string nameHint)
    {
        if (string.IsNullOrWhiteSpace(nameHint)) return null;
        Engine.UpdateAudioDevicesInfo();
        var match = Engine.CaptureDevices
            .FirstOrDefault(d => d.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase));
        return match.Name != null ? match : (DeviceInfo?)null;
    }

    /// <summary>
    /// Finds a playback device whose name contains <paramref name="nameHint"/> (case-insensitive).
    /// Returns null if the hint is empty or no device matches — callers treat null as "use default".
    /// </summary>
    public static DeviceInfo? FindPlaybackDevice(string nameHint)
    {
        if (string.IsNullOrWhiteSpace(nameHint)) return null;
        Engine.UpdateAudioDevicesInfo();
        var match = Engine.PlaybackDevices
            .FirstOrDefault(d => d.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase));
        return match.Name != null ? match : (DeviceInfo?)null;
    }

    /// <summary>
    /// Maps a SIPSorcery clock rate (8000, 16000, 48000, …) to a SoundFlow AudioFormat.
    /// SoundFlow always delivers/accepts F32 internally; the SampleFormat here tells MiniAudio
    /// what format to use at the device boundary.
    /// </summary>
    public static SFAudioFormat ToSoundFlowFormat(int clockRate, int channels = 1)
    {
        var layout = channels == 1 ? ChannelLayout.Mono : ChannelLayout.Stereo;
        return new SFAudioFormat
        {
            Format     = SampleFormat.F32,
            Channels   = channels,
            Layout     = layout,
            SampleRate = clockRate,
        };
    }
}

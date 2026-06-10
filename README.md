# SIPSorceryMedia.SoundFlow

A C# library that integrates [SoundFlow](https://github.com/LSXPrime/SoundFlow) with the [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) VoIP/WebRTC stack, providing audio capture and playback for .NET 8 and above.

## Packages

| Package | Version |
|---|---|
| `SIPSorceryMedia.SoundFlow` | [![NuGet](https://img.shields.io/nuget/v/SIPSorceryMedia.SoundFlow)](https://www.nuget.org/packages/SIPSorceryMedia.SoundFlow/) |

## Requirements

- .NET 8.0, 9.0, or 10.0
- [SoundFlow](https://www.nuget.org/packages/SoundFlow/) 1.4.1+
- [SoundFlow.Extensions.WebRtc.Apm](https://www.nuget.org/packages/SoundFlow.Extensions.WebRtc.Apm/) 1.4.0+ (included as a dependency)

## Classes

### `SoundFlowAudioSource`

An `IAudioSource` implementation backed by a SoundFlow `Recorder`. Captures microphone audio, converts it to S16 LE PCM, and encodes it via the provided `IAudioEncoder`.

```csharp
var source = new SoundFlowAudioSource(
    audioInDeviceName: string.Empty,  // empty = default device
    audioEncoder:      new OpusAudioEncoder(),
    frameSize:         1920);
```

**Audio processing (WebRTC APM)**

Noise suppression, high-pass filtering, AGC, and echo cancellation are available via `EnableAudioProcessing`. The modifier survives recorder teardown and is automatically reapplied if the format changes.

```csharp
// Enable noise suppression + HPF (recommended for voice)
source.EnableAudioProcessing(
    noiseSuppression: true,
    highPassFilter:   true);

// Enable with AGC and echo cancellation
// referenceDevice should be the AudioPlaybackDevice from SoundFlowAudioEndPoint
source.EnableAudioProcessing(
    referenceDevice:   endPoint.PlaybackDevice,
    noiseSuppression:  true,
    highPassFilter:    true,
    agc2:              true,
    echoCancellation:  true,
    aecLatencyMs:      40);

// Adjust a setting at runtime via the live modifier
source.ApmModifier?.NoiseSuppression.Level = NoiseSuppressionLevel.VeryHigh;

// Turn off processing
source.DisableAudioProcessing();
```

| Parameter | Default | Description |
|---|---|---|
| `referenceDevice` | `null` (uses capture device) | `AudioPlaybackDevice` for echo cancellation reference |
| `noiseSuppression` | `true` | WebRTC noise suppressor |
| `nsLevel` | `High` | `Low`, `Moderate`, `High`, or `VeryHigh` |
| `highPassFilter` | `true` | Removes hum and low-frequency noise below ~80 Hz |
| `agc1` | `false` | Legacy AGC (hardware-style gain control) |
| `agc2` | `false` | Modern AGC (adaptive digital) |
| `echoCancellation` | `false` | AEC — requires `referenceDevice` |
| `aecLatencyMs` | `40` | Estimated render-to-capture latency for AEC |

**Other members**

| Member | Description |
|---|---|
| `Volume` | Capture gain multiplier (1.0 = unity, applied during F32→S16 conversion) |
| `ApmModifier` | The active `WebRtcApmModifier`, or `null` if processing is disabled |
| `GetStats()` | Returns `AudioSourceStats` (dropped frames, active state) |

---

### `SoundFlowAudioEndPoint`

An `IAudioSink` implementation backed by a SoundFlow `SoundPlayer` and `QueueDataProvider`. Accepts encoded or raw PCM audio and plays it through the selected output device.

```csharp
var endPoint = new SoundFlowAudioEndPoint(
    audioOutDeviceName: string.Empty,  // empty = default device
    audioEncoder:       new OpusAudioEncoder());
```

| Member | Description |
|---|---|
| `Volume` | Playback volume multiplier (0.0–∞, applied on the `SoundPlayer`) |
| `PlaybackDevice` | The underlying `AudioPlaybackDevice` — pass to `SoundFlowAudioSource.EnableAudioProcessing` as `referenceDevice` for AEC |
| `PutAudioSample(byte[])` | Queue raw S16 LE PCM bytes for playback |
| `GotEncodedMediaFrame(...)` | Decode and queue an encoded audio frame |
| `GetStats()` | Returns `AudioSinkStats` (underruns, dropped frames, queue depth) |

---

### `SoundFlowHelper`

Static helpers for interacting with the shared `MiniAudioEngine`.

| Member | Description |
|---|---|
| `Engine` | Lazily initialized process-wide `MiniAudioEngine` |
| `FindCaptureDevice(hint)` | Returns the first capture `DeviceInfo` whose name contains `hint` (case-insensitive); empty string returns the default |
| `FindPlaybackDevice(hint)` | Same for playback devices |
| `ToSoundFlowFormat(clockRate, channels)` | Converts a SIPSorcery clock rate to a SoundFlow `AudioFormat` |
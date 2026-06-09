using System;
using System.Collections.Generic;
using System.Threading;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.SoundFlow;

// Pass-through encoder: raw S16 LE PCM treated as its own "codec".
// This lets the test wire source → sink without a real codec dependency.
var encoder = new RawPcmEncoder();
var format  = new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU);

using var source   = new SoundFlowAudioSource("", encoder);
using var endpoint = new SoundFlowAudioEndPoint("", encoder);

source.SetAudioSourceFormat(format);
endpoint.SetAudioSinkFormat(format);

// Route captured PCM directly into the playback queue.
source.OnAudioSourceRawSample += (rate, duration, samples) =>
{
    var bytes = new byte[samples.Length * sizeof(short)];
    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
    endpoint.PutAudioSample(bytes);
};

Console.WriteLine("Recording and playing back. Press any key to stop...");
Console.ReadKey(intercept: true);

await source.CloseAudioAsync();
await endpoint.CloseAudioSinkAsync();

var srcStats  = source.GetStats();
var sinkStats = endpoint.GetStats();
Console.WriteLine($"Source  — dropped frames : {srcStats.DroppedFrames}");
Console.WriteLine($"Sink    — underruns       : {sinkStats.UnderrunCount}  dropped frames : {sinkStats.DroppedFrames}");

public class RawPcmEncoder : IAudioEncoder
{
    private static readonly List<AudioFormat> _formats =
        [new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU)];

    public List<AudioFormat> SupportedFormats => _formats;

    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
    {
        var shorts = new short[encodedSample.Length / sizeof(short)];
        Buffer.BlockCopy(encodedSample, 0, shorts, 0, encodedSample.Length);
        return shorts;
    }
}

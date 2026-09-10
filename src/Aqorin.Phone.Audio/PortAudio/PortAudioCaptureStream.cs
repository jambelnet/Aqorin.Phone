using System.Runtime.InteropServices;
using Aqorin.Phone.Audio.Processing;
using Aqorin.Phone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using PortAudioSharp;

namespace Aqorin.Phone.Audio.PortAudio;

/// <summary>Microphone capture. Delivers fixed frames at the requested rate regardless of the device rate.</summary>
internal sealed class PortAudioCaptureStream : PortAudioStreamBase, IAudioCaptureStream
{
    private readonly LinearResampler _resampler;
    private readonly FrameAccumulator _accumulator;
    private short[] _scratch = [];

    public PortAudioCaptureStream(AudioStreamRequest request, int deviceIndex, DeviceInfo deviceInfo, ILogger logger)
        : base(request, deviceIndex, deviceInfo, isInput: true, logger)
    {
        _resampler = new LinearResampler(DeviceSampleRate, request.SampleRate);
        _accumulator = new FrameAccumulator(request.FrameSizeSamples);
    }

    public event Action<short[]>? FrameCaptured;

    protected override StreamCallbackResult OnAudio(IntPtr input, IntPtr output, int frameCount, StreamCallbackFlags flags)
    {
        if (input == IntPtr.Zero || frameCount <= 0)
        {
            return StreamCallbackResult.Continue;
        }

        if (_scratch.Length < frameCount)
        {
            _scratch = new short[frameCount];
        }

        Marshal.Copy(input, _scratch, 0, frameCount);
        var samples = _resampler.IsIdentity ? _scratch.AsSpan(0, frameCount) : _resampler.Process(_scratch.AsSpan(0, frameCount)).AsSpan();
        var handler = FrameCaptured;
        if (handler is not null)
        {
            _accumulator.Push(samples, handler);
        }

        return StreamCallbackResult.Continue;
    }
}

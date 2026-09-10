using System.Runtime.InteropServices;
using Aqorin.Phone.Audio.Processing;
using Aqorin.Phone.Core.Abstractions;
using Microsoft.Extensions.Logging;
using PortAudioSharp;

namespace Aqorin.Phone.Audio.PortAudio;

/// <summary>Speaker playback. Accepts PCM at the requested rate, resamples to the device rate and buffers up to ~400 ms.</summary>
internal sealed class PortAudioPlaybackStream : PortAudioStreamBase, IAudioPlaybackStream
{
    private const int MaxBufferedMilliseconds = 400;
    private readonly LinearResampler _resampler;
    private readonly PcmRingBuffer _ring;
    private short[] _scratch = [];

    public PortAudioPlaybackStream(AudioStreamRequest request, int deviceIndex, DeviceInfo deviceInfo, ILogger logger)
        : base(request, deviceIndex, deviceInfo, isInput: false, logger)
    {
        _resampler = new LinearResampler(request.SampleRate, DeviceSampleRate);
        _ring = new PcmRingBuffer(DeviceSampleRate * MaxBufferedMilliseconds / 1000);
    }

    public TimeSpan Buffered => TimeSpan.FromSeconds((double)_ring.Count / DeviceSampleRate);

    public void Enqueue(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        if (_resampler.IsIdentity)
        {
            _ring.Write(pcm);
        }
        else
        {
            // The resampler is only ever driven from the network thread; PcmRingBuffer is the thread-safe boundary.
            lock (_resampler)
            {
                _ring.Write(_resampler.Process(pcm));
            }
        }
    }

    protected override StreamCallbackResult OnAudio(IntPtr input, IntPtr output, int frameCount, StreamCallbackFlags flags)
    {
        if (output == IntPtr.Zero || frameCount <= 0)
        {
            return StreamCallbackResult.Continue;
        }

        if (_scratch.Length < frameCount)
        {
            _scratch = new short[frameCount];
        }

        _ring.Read(_scratch.AsSpan(0, frameCount));
        Marshal.Copy(_scratch, 0, output, frameCount);
        return StreamCallbackResult.Continue;
    }
}

using Aqorin.Phone.Audio.Processing;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Audio;

namespace Aqorin.Phone.Core.Tests;

public class FrameAccumulatorTests
{
    [Fact]
    public void Reblocks_arbitrary_chunks_into_fixed_frames()
    {
        var accumulator = new FrameAccumulator(160);
        var frames = new List<short[]>();
        var input = Enumerable.Range(0, 500).Select(i => (short)i).ToArray();

        accumulator.Push(input.AsSpan(0, 100), frames.Add);
        accumulator.Push(input.AsSpan(100, 250), frames.Add);
        accumulator.Push(input.AsSpan(350, 150), frames.Add);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(160, f.Length));
        Assert.Equal(input.Take(480), frames.SelectMany(f => f));
        Assert.Equal(20, accumulator.Pending);
    }
}

public class PcmRingBufferTests
{
    [Fact]
    public void Reads_pad_with_silence_when_starved()
    {
        var ring = new PcmRingBuffer(100);
        ring.Write(new short[] { 1, 2, 3 });
        var dest = new short[5];

        var real = ring.Read(dest);

        Assert.Equal(3, real);
        Assert.Equal(new short[] { 1, 2, 3, 0, 0 }, dest);
        Assert.Equal(2, ring.Underruns);
    }

    [Fact]
    public void Oldest_samples_are_dropped_on_overflow()
    {
        var ring = new PcmRingBuffer(4);
        ring.Write(new short[] { 1, 2, 3 });
        ring.Write(new short[] { 4, 5, 6 });

        var dest = new short[4];
        ring.Read(dest);

        Assert.Equal(new short[] { 3, 4, 5, 6 }, dest);
        Assert.Equal(2, ring.Dropped);
    }

    [Fact]
    public void Wraps_around_correctly()
    {
        var ring = new PcmRingBuffer(5);
        var dest = new short[3];
        for (short i = 0; i < 30; i += 3)
        {
            ring.Write(new[] { i, (short)(i + 1), (short)(i + 2) });
            ring.Read(dest);
            Assert.Equal(new[] { i, (short)(i + 1), (short)(i + 2) }, dest);
        }
    }
}

public class LinearResamplerTests
{
    [Fact]
    public void Identity_returns_copy()
    {
        var r = new LinearResampler(8000, 8000);
        Assert.True(r.IsIdentity);
        Assert.Equal(new short[] { 1, 2, 3 }, r.Process(new short[] { 1, 2, 3 }));
    }

    [Fact]
    public void Integer_downsampling_averages_blocks()
    {
        var r = new LinearResampler(48000, 8000);
        var input = Enumerable.Repeat((short)600, 960).ToArray(); // 20 ms at 48 kHz
        var output = r.Process(input);
        Assert.Equal(160, output.Length);
        Assert.All(output, s => Assert.Equal(600, s));
    }

    [Fact]
    public void Downsampling_state_carries_across_calls()
    {
        var r = new LinearResampler(48000, 8000);
        var total = 0;
        for (var i = 0; i < 10; i++)
        {
            total += r.Process(new short[100]).Length; // 1000 samples -> 166 frames + remainder 4
        }

        Assert.Equal(166, total);
    }

    [Fact]
    public void Upsampling_produces_the_expected_amount_of_samples()
    {
        var r = new LinearResampler(8000, 48000);
        var output = new List<short>();
        for (var i = 0; i < 50; i++)
        {
            output.AddRange(r.Process(Enumerable.Repeat((short)1000, 160).ToArray()));
        }

        // 50 * 160 input samples => exactly 50 * 960 output samples: the rational position never drifts.
        Assert.Equal(50 * 960, output.Count);
        Assert.All(output, s => Assert.Equal(1000, s));
    }

    [Fact]
    public void Non_integer_ratio_interpolates_smoothly()
    {
        var r = new LinearResampler(44100, 8000);
        var input = Enumerable.Range(0, 4410).Select(i => (short)(i % 200)).ToArray(); // 100 ms
        var output = r.Process(input);
        Assert.InRange(output.Length, 795, 801);
    }
}

public class NullAudioDeviceServiceTests
{
    [Fact]
    public void Reports_unavailable_but_streams_work_silently()
    {
        using var service = new NullAudioDeviceService("test");
        Assert.False(service.Backend.IsAvailable);
        Assert.Equal("test", service.Backend.UnavailableReason);
        Assert.Empty(service.GetInputDevices());

        using var capture = service.OpenCapture(new AudioStreamRequest(8000));
        using var playback = service.OpenPlayback(new AudioStreamRequest(8000));
        capture.Start();
        playback.Start();
        playback.Enqueue(new short[160]);
        Assert.Equal(TimeSpan.Zero, playback.Buffered);
        Assert.Equal(160, capture.Request.FrameSizeSamples);
    }
}

public class PortAudioBackendSmokeTests
{
    /// <summary>
    /// Exercises the real native binding on the build machine. It must never fail because a machine has no sound card:
    /// the backend either initialises (possibly with zero devices) or reports a precise reason.
    /// </summary>
    [Fact]
    public void PortAudio_initialises_or_reports_a_reason()
    {
        using var service = new Aqorin.Phone.Audio.PortAudio.PortAudioDeviceService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Aqorin.Phone.Audio.PortAudio.PortAudioDeviceService>.Instance);

        Assert.Equal("PortAudio", service.Backend.Name);
        if (service.Backend.IsAvailable)
        {
            Assert.False(string.IsNullOrWhiteSpace(service.Backend.Version));
            _ = service.GetInputDevices();
            _ = service.GetOutputDevices();
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(service.Backend.UnavailableReason));
            Assert.Throws<AudioDeviceException>(() => service.OpenCapture(new AudioStreamRequest(8000)));
        }
    }
}

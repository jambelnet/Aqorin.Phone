namespace Aqorin.Phone.Audio.Processing;

/// <summary>Re-blocks an arbitrary stream of samples into fixed-size frames (e.g. 160 samples = 20 ms at 8 kHz).</summary>
public sealed class FrameAccumulator
{
    private readonly short[] _buffer;
    private int _filled;

    public FrameAccumulator(int frameSize)
    {
        if (frameSize <= 0) throw new ArgumentOutOfRangeException(nameof(frameSize));
        _buffer = new short[frameSize];
    }

    public int FrameSize => _buffer.Length;

    public int Pending => _filled;

    /// <summary>Appends samples and invokes <paramref name="onFrame"/> once per completed frame (a fresh array each time).</summary>
    public void Push(ReadOnlySpan<short> samples, Action<short[]> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        var offset = 0;
        while (offset < samples.Length)
        {
            var take = Math.Min(_buffer.Length - _filled, samples.Length - offset);
            samples.Slice(offset, take).CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            offset += take;
            if (_filled == _buffer.Length)
            {
                var frame = new short[_buffer.Length];
                Array.Copy(_buffer, frame, frame.Length);
                _filled = 0;
                onFrame(frame);
            }
        }
    }

    public void Reset() => _filled = 0;
}

namespace Aqorin.Phone.Audio.Processing;

/// <summary>
/// Thread-safe single-producer/single-consumer style PCM queue with a hard capacity.
/// When the producer outruns the consumer the oldest audio is dropped so playback latency stays bounded.
/// When the consumer is starved the read is padded with silence.
/// </summary>
public sealed class PcmRingBuffer
{
    private readonly short[] _buffer;
    private readonly object _gate = new();
    private int _readIndex;
    private int _count;

    public PcmRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        _buffer = new short[capacitySamples];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Total samples dropped because the buffer was full (diagnostics).</summary>
    public long Dropped { get; private set; }

    /// <summary>Total silence samples inserted because the buffer was empty (diagnostics).</summary>
    public long Underruns { get; private set; }

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_gate)
        {
            if (samples.Length >= _buffer.Length)
            {
                // Keep only the newest capacity-worth of samples.
                Dropped += _count + samples.Length - _buffer.Length;
                samples = samples[^_buffer.Length..];
                _readIndex = 0;
                _count = 0;
            }

            var overflow = _count + samples.Length - _buffer.Length;
            if (overflow > 0)
            {
                _readIndex = (_readIndex + overflow) % _buffer.Length;
                _count -= overflow;
                Dropped += overflow;
            }

            var writeIndex = (_readIndex + _count) % _buffer.Length;
            var firstPart = Math.Min(samples.Length, _buffer.Length - writeIndex);
            samples[..firstPart].CopyTo(_buffer.AsSpan(writeIndex));
            if (firstPart < samples.Length)
            {
                samples[firstPart..].CopyTo(_buffer.AsSpan(0));
            }

            _count += samples.Length;
        }
    }

    /// <summary>Fills <paramref name="destination"/> completely, padding with silence. Returns the number of real samples.</summary>
    public int Read(Span<short> destination)
    {
        lock (_gate)
        {
            var available = Math.Min(destination.Length, _count);
            var firstPart = Math.Min(available, _buffer.Length - _readIndex);
            _buffer.AsSpan(_readIndex, firstPart).CopyTo(destination);
            if (firstPart < available)
            {
                _buffer.AsSpan(0, available - firstPart).CopyTo(destination[firstPart..]);
            }

            _readIndex = (_readIndex + available) % _buffer.Length;
            _count -= available;

            if (available < destination.Length)
            {
                destination[available..].Clear();
                Underruns += destination.Length - available;
            }

            return available;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _readIndex = 0;
            _count = 0;
        }
    }
}

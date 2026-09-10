namespace Aqorin.Phone.Audio.Processing;

/// <summary>
/// Stateful mono PCM16 sample-rate converter suitable for narrowband voice.
/// Integer down-sampling uses block averaging (a crude anti-alias filter); every other ratio uses
/// linear interpolation with the fractional phase carried across calls so frames can be streamed.
/// Not thread-safe: drive each instance from one thread.
/// </summary>
public sealed class LinearResampler
{
    private readonly int _decimation;
    private readonly double _step;

    // Decimation state
    private long _accumulator;
    private int _accumulated;

    // Interpolation state. The read position is kept as an exact rational: position = _positionNumerator / OutputRate
    // (in input-sample units, measured from the "previous" sample) so long streams never drift.
    private long _positionNumerator;
    private short _previous;
    private bool _hasPrevious;

    public LinearResampler(int inRate, int outRate)
    {
        if (inRate <= 0) throw new ArgumentOutOfRangeException(nameof(inRate));
        if (outRate <= 0) throw new ArgumentOutOfRangeException(nameof(outRate));
        InputRate = inRate;
        OutputRate = outRate;
        _decimation = inRate % outRate == 0 ? inRate / outRate : 0;
        _step = (double)inRate / outRate;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public bool IsIdentity => InputRate == OutputRate;

    /// <summary>Converts a block of samples. Returns a new array (may be empty when the block is shorter than one output sample).</summary>
    public short[] Process(ReadOnlySpan<short> input)
    {
        if (IsIdentity)
        {
            return input.ToArray();
        }

        return _decimation > 1 ? Decimate(input) : Interpolate(input);
    }

    private short[] Decimate(ReadOnlySpan<short> input)
    {
        var result = new List<short>(input.Length / _decimation + 1);
        foreach (var sample in input)
        {
            _accumulator += sample;
            _accumulated++;
            if (_accumulated == _decimation)
            {
                result.Add((short)Math.Clamp(_accumulator / _decimation, short.MinValue, short.MaxValue));
                _accumulator = 0;
                _accumulated = 0;
            }
        }

        return result.ToArray();
    }

    private short[] Interpolate(ReadOnlySpan<short> input)
    {
        if (input.Length == 0)
        {
            return [];
        }

        if (!_hasPrevious)
        {
            // Seed with the first sample so the stream starts without a discontinuity.
            _previous = input[0];
            _hasPrevious = true;
            _positionNumerator = 0;
        }

        // Virtual sample sequence: index 0 = _previous, index k = input[k-1]. The read position interpolates
        // between floor(pos) and floor(pos)+1 and must stay below the last virtual index (input.Length).
        var limit = (long)input.Length * OutputRate;
        var result = new List<short>((int)(input.Length / _step) + 2);
        while (_positionNumerator < limit)
        {
            var idx = (int)(_positionNumerator / OutputRate);
            var frac = (_positionNumerator % OutputRate) / (double)OutputRate;
            var s0 = idx == 0 ? _previous : input[idx - 1];
            var s1 = input[idx];
            var value = s0 + (s1 - s0) * frac;
            result.Add((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
            _positionNumerator += InputRate;
        }

        // Re-base: the last input sample becomes the new "previous" (virtual index 0).
        _previous = input[^1];
        _positionNumerator -= limit;
        return result.ToArray();
    }
}

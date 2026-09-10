namespace Aqorin.Phone.Core.Retry;

/// <summary>Bounded exponential backoff: <c>min(initial * 2^(attempt-1), max)</c> with optional jitter, up to <see cref="MaxAttempts"/>.</summary>
public sealed class ExponentialBackoffPolicy
{
    public ExponentialBackoffPolicy(TimeSpan initialDelay, TimeSpan maxDelay, int maxAttempts, double jitterFraction = 0.0)
    {
        if (initialDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay));
        if (maxDelay < initialDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay));
        if (maxAttempts < 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (jitterFraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(jitterFraction));

        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
        MaxAttempts = maxAttempts;
        JitterFraction = jitterFraction;
    }

    public TimeSpan InitialDelay { get; }
    public TimeSpan MaxDelay { get; }

    /// <summary>Maximum number of retries after the first failure. 0 disables retries.</summary>
    public int MaxAttempts { get; }

    public double JitterFraction { get; }

    /// <summary>Default policy for registration: 3s, 6s, 12s, 24s, 48s, 96s, 120s, 120s for up to 8 retries — gentle enough not to trip a registrar's brute-force protection.</summary>
    public static ExponentialBackoffPolicy RegistrationDefault { get; } =
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(120), maxAttempts: 8, jitterFraction: 0.1);

    /// <summary>Returns true and the delay before retry number <paramref name="attempt"/> (1-based), or false when retries are exhausted.</summary>
    public bool TryGetDelay(int attempt, out TimeSpan delay) => TryGetDelay(attempt, Random.Shared, out delay);

    public bool TryGetDelay(int attempt, Random random, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(random);
        delay = TimeSpan.Zero;
        if (attempt < 1 || attempt > MaxAttempts)
        {
            return false;
        }

        var exponent = Math.Min(attempt - 1, 30);
        var ticks = InitialDelay.Ticks * (double)(1L << exponent);
        var bounded = Math.Min(ticks, MaxDelay.Ticks);

        if (JitterFraction > 0)
        {
            var jitter = 1.0 + (random.NextDouble() * 2 - 1) * JitterFraction;
            bounded = Math.Min(bounded * jitter, MaxDelay.Ticks);
        }

        delay = TimeSpan.FromTicks((long)bounded);
        return true;
    }
}

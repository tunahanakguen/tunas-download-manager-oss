using System.Diagnostics;

namespace TunasDM.Core;

/// <summary>Token-Bucket; kann von mehreren Downloads gemeinsam benutzt werden (globales Limit).</summary>
public sealed class SpeedLimiter
{
    private readonly object _lock = new();
    private double _tokens;
    private long _lastTimestamp = Stopwatch.GetTimestamp();
    private long _bytesPerSecond;

    public SpeedLimiter(long bytesPerSecond) => BytesPerSecond = bytesPerSecond;

    /// <summary>0 = unbegrenzt.</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                long rate = BytesPerSecond;
                if (rate <= 0)
                    return;

                long now = Stopwatch.GetTimestamp();
                double capacity = Math.Max(rate / 4.0, bytes);
                _tokens = Math.Min(capacity, _tokens + Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds * rate);
                _lastTimestamp = now;

                // Schulden sind erlaubt: Wer Guthaben hat, darf sofort; die nächste Anfrage wartet entsprechend.
                if (_tokens > 0)
                {
                    _tokens -= bytes;
                    return;
                }
                delay = TimeSpan.FromSeconds(-_tokens / rate + 0.001);
            }
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}

using System.Diagnostics;

namespace TunasDM.Core;

/// <summary>Geschwindigkeit als gleitender Durchschnitt über die letzten Sekunden.</summary>
public sealed class SpeedMeter
{
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private readonly TimeSpan _window;

    public SpeedMeter(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(5);

    /// <summary>Neuen Gesamtstand eintragen; liefert Bytes pro Sekunde.</summary>
    public double Update(long totalBytes)
    {
        long now = Stopwatch.GetTimestamp();
        _samples.Enqueue((now, totalBytes));
        while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().Timestamp, now) > _window)
            _samples.Dequeue();

        var first = _samples.Peek();
        double seconds = Stopwatch.GetElapsedTime(first.Timestamp, now).TotalSeconds;
        return seconds <= 0 ? 0 : Math.Max(0, (totalBytes - first.Bytes) / seconds);
    }
}

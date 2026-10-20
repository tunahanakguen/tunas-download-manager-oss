namespace TunasDM.Core.Management;

/// <summary>
/// Startet und stoppt Warteschlangen nach ihrem Zeitplan. Die App ruft <see cref="Tick"/> regelmäßig auf
/// (z. B. alle 15 Sekunden); die Zeit wird übergeben, damit sich der Ablauf testen lässt.
/// Verpasste Startzeiten (Programm war geschlossen) werden nicht nachgeholt.
/// </summary>
public sealed class QueueScheduler
{
    private readonly DownloadManager _manager;
    private readonly Dictionary<long, DateTime> _stopAt = new();
    private DateTime _lastTick;

    public QueueScheduler(DownloadManager manager, DateTime now)
    {
        _manager = manager;
        _lastTick = now;
    }

    /// <summary>Vom Zeitplan gestartet (für Hinweise in der Oberfläche).</summary>
    public event EventHandler<DownloadQueue>? Started;

    /// <summary>Zur Stoppzeit angehalten.</summary>
    public event EventHandler<DownloadQueue>? Stopped;

    public void Tick(DateTime now)
    {
        // Uhr zurückgestellt (Sommerzeit, manuell): nur weiterrücken, nichts doppelt starten.
        if (now < _lastTick)
        {
            _lastTick = now;
            return;
        }

        foreach (var queue in _manager.Queues)
        {
            var schedule = queue.Schedule;
            if (schedule.NextStart(_lastTick) is { } start && start <= now)
            {
                _manager.StartQueue(queue);
                if (schedule.StopAfter(start) is { } stop)
                    _stopAt[queue.Id] = stop;
                else
                    _stopAt.Remove(queue.Id);
                Started?.Invoke(this, queue);
            }

            if (_stopAt.TryGetValue(queue.Id, out var stopAt) && stopAt <= now)
            {
                _stopAt.Remove(queue.Id);
                if (_manager.IsRunning(queue))
                {
                    _manager.StopQueue(queue);
                    Stopped?.Invoke(this, queue);
                }
            }
        }
        _lastTick = now;
    }

    /// <summary>Nächster geplanter Start irgendeiner Warteschlange (für die Statuszeile).</summary>
    public (DownloadQueue Queue, DateTime At)? NextStart(DateTime now) =>
        _manager.Queues
            .Select(q => (Queue: q, At: q.Schedule.NextStart(now)))
            .Where(x => x.At is not null)
            .OrderBy(x => x.At)
            .Select(x => ((DownloadQueue, DateTime)?)(x.Queue, x.At!.Value))
            .FirstOrDefault();
}

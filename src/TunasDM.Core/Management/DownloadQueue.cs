namespace TunasDM.Core.Management;

public enum QueueRepeat
{
    /// <summary>Einmal an <see cref="QueueSchedule.Date"/>.</summary>
    Once,

    Daily,

    /// <summary>An den gewählten Wochentagen (<see cref="QueueSchedule.Days"/>).</summary>
    Weekdays,
}

/// <summary>Was nach dem Abarbeiten einer Warteschlange passiert.</summary>
public enum AfterQueueAction
{
    None,

    /// <summary>TUNAS beenden.</summary>
    ExitApp,

    Sleep,
    Hibernate,
    Shutdown,

    /// <summary>Ein Programm oder Skript starten (<see cref="DownloadQueue.AfterProgram"/>).</summary>
    RunProgram,
}

/// <summary>Zeitplan einer Warteschlange. Zeiten sind Ortszeit.</summary>
public sealed class QueueSchedule
{
    /// <summary>Automatisch starten.</summary>
    public bool StartEnabled { get; set; }

    public QueueRepeat Repeat { get; set; } = QueueRepeat.Daily;

    /// <summary>Tag für <see cref="QueueRepeat.Once"/>.</summary>
    public DateTime Date { get; set; } = DateTime.Today;

    public List<DayOfWeek> Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public TimeSpan StartTime { get; set; } = new(23, 0, 0);

    /// <summary>Zur Stoppzeit anhalten; was die Warteschlange gestartet hat, wartet wieder in ihr.</summary>
    public bool StopEnabled { get; set; }

    public TimeSpan StopTime { get; set; } = new(7, 0, 0);

    /// <summary>Nächster Start nach <paramref name="after"/> (ausschließlich), oder null.</summary>
    public DateTime? NextStart(DateTime after)
    {
        if (!StartEnabled)
            return null;
        switch (Repeat)
        {
            case QueueRepeat.Once:
                var once = Date.Date + StartTime;
                return once > after ? once : null;
            case QueueRepeat.Daily:
                var today = after.Date + StartTime;
                return today > after ? today : today.AddDays(1);
            default:
                for (int day = 0; day <= 7; day++)
                {
                    var candidate = after.Date.AddDays(day) + StartTime;
                    if (candidate > after && Days.Contains(candidate.DayOfWeek))
                        return candidate;
                }
                return null;
        }
    }

    /// <summary>Wann ein um <paramref name="startedAt"/> gestarteter Lauf anhält (nächste Stoppzeit danach), oder null.</summary>
    public DateTime? StopAfter(DateTime startedAt)
    {
        if (!StopEnabled)
            return null;
        var stop = startedAt.Date + StopTime;
        return stop > startedAt ? stop : stop.AddDays(1);
    }
}

/// <summary>Eine Warteschlange: Downloads mit Status „In Warteschlange“ und dieser <see cref="Id"/> laden nacheinander.</summary>
public sealed class DownloadQueue
{
    public const long MainId = 1;

    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Zum Anzeigen: eingebaute Namen wie „Hauptwarteschlange“ übersetzt, eigene unverändert.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => L.T(Name);

    /// <summary>So viele Dateien lädt die Warteschlange gleichzeitig.</summary>
    public int MaxParallel { get; set; } = 3;

    /// <summary>So oft wird ein fehlgeschlagener Download der Warteschlange erneut versucht.</summary>
    public int Retries { get; set; } = 3;

    public AfterQueueAction After { get; set; }

    /// <summary>Für <see cref="AfterQueueAction.RunProgram"/>: Programm, Skript oder Datei.</summary>
    public string? AfterProgram { get; set; }

    /// <summary>Argumente für <see cref="AfterProgram"/>.</summary>
    public string? AfterArguments { get; set; }

    /// <summary>
    /// Sync-Warteschlange: Bei jedem Start werden ihre fertigen Dateien auf dem Server geprüft und
    /// geänderte neu geladen; die alte Datei wird erst ersetzt, wenn die neue vollständig ist.
    /// </summary>
    public bool Sync { get; set; }

    public QueueSchedule Schedule { get; set; } = new();

    public bool IsMain => Id == MainId;

    public static DownloadQueue CreateMain(int maxParallel) =>
        new() { Id = MainId, Name = "Hauptwarteschlange", MaxParallel = maxParallel };
}

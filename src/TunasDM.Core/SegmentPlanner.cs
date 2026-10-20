namespace TunasDM.Core;

/// <summary>Ein Bereich der Datei: [Start, End), geladen bis Position (exklusiv).</summary>
internal sealed class Segment
{
    public long Start;
    public long Position;
    public long End;

    /// <summary>Bytes, die gerade gelesen werden und hinter Position noch dazukommen.</summary>
    public int Reserved;

    /// <summary>Verbindung, die das Segment lädt; -1 = frei.</summary>
    public int Owner = -1;

    /// <summary>Beim Fortsetzen erst prüfen, ob die vorhandenen Daten noch passen.</summary>
    public bool NeedsVerify;

    /// <summary>Seit der Übernahme durch die aktuelle Verbindung geladene Bytes.</summary>
    public long Transferred;

    /// <summary>Zeitpunkt der Übernahme (Stopwatch-Timestamp).</summary>
    public long AcquiredAt;

    /// <summary>Die Überwachung hat die Verbindung als zu langsam abgebrochen.</summary>
    public volatile bool AbortRequested;

    /// <summary>Bricht die laufende Anfrage dieses Segments ab.</summary>
    public CancellationTokenSource? Abort;
}

/// <summary>
/// Dynamische Segmentierung: Eine freie Verbindung übernimmt zuerst ein freies Segment,
/// sonst teilt sie das größte offene Segment in der Mitte. Unter der Mindestgröße wird nicht geteilt.
/// </summary>
internal sealed class SegmentPlanner
{
    private readonly object _lock = new();
    private readonly List<Segment> _segments;
    private readonly long _minSegmentSize;

    public SegmentPlanner(IEnumerable<SegmentState> segments, long minSegmentSize, bool canSplit, bool resumed)
    {
        _minSegmentSize = minSegmentSize;
        CanSplit = canSplit;
        _segments = segments
            .OrderBy(s => s.Start)
            .Select(s => new Segment
            {
                Start = s.Start,
                Position = s.Position,
                End = s.End,
                NeedsVerify = resumed && s.Position > s.Start && s.Position < s.End,
            })
            .ToList();
    }

    public bool CanSplit { get; }

    public Segment? Acquire(int owner)
    {
        lock (_lock)
        {
            foreach (var segment in _segments)
            {
                if (segment.Owner < 0 && segment.Position < segment.End)
                    return Assign(segment, owner);
            }

            if (!CanSplit)
                return null;

            int index = -1;
            long largestRest = 0;
            for (int i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                if (segment.Owner < 0)
                    continue;
                long rest = segment.End - segment.Position - segment.Reserved;
                if (rest > largestRest)
                {
                    largestRest = rest;
                    index = i;
                }
            }

            if (index < 0 || largestRest < 2 * _minSegmentSize)
                return null;

            var largest = _segments[index];
            long middle = largest.Position + largest.Reserved + largestRest / 2;
            var split = new Segment { Start = middle, Position = middle, End = largest.End };
            largest.End = middle;
            _segments.Insert(index + 1, split);
            return Assign(split, owner);
        }
    }

    private static Segment Assign(Segment segment, int owner)
    {
        segment.Owner = owner;
        segment.Transferred = 0;
        segment.AcquiredAt = System.Diagnostics.Stopwatch.GetTimestamp();
        segment.AbortRequested = false;
        return segment;
    }

    /// <summary>Segmente, die gerade geladen werden (für die Geschwindigkeitsüberwachung).</summary>
    public List<Segment> ActiveSegments()
    {
        lock (_lock)
            return _segments.Where(s => s.Owner >= 0).ToList();
    }

    public long RemainingOf(Segment segment)
    {
        lock (_lock)
            return segment.End - segment.Position;
    }

    public long TransferredOf(Segment segment)
    {
        lock (_lock)
            return segment.Transferred;
    }

    /// <summary>Reserviert bis zu <paramref name="wanted"/> Bytes; 0 = Segment fertig (Ende evtl. verkürzt).</summary>
    public int Reserve(Segment segment, int wanted)
    {
        lock (_lock)
        {
            long available = segment.End - segment.Position;
            segment.Reserved = available <= 0 ? 0 : (int)Math.Min(wanted, available);
            return segment.Reserved;
        }
    }

    public void Commit(Segment segment, int count)
    {
        lock (_lock)
        {
            segment.Position += count;
            segment.Transferred += count;
            segment.Reserved = 0;
        }
    }

    public void Release(Segment segment)
    {
        lock (_lock)
        {
            segment.Owner = -1;
            segment.Reserved = 0;
        }
    }

    /// <summary>Für Server ohne Range-Unterstützung: von vorne beginnen.</summary>
    public void Reset(Segment segment)
    {
        lock (_lock)
            segment.Position = segment.Start;
    }

    /// <summary>Für Dateien unbekannter Größe: Ende ist dort, wo der Server aufgehört hat.</summary>
    public void FinishAt(Segment segment)
    {
        lock (_lock)
            segment.End = segment.Position;
    }

    public bool IsComplete
    {
        get
        {
            lock (_lock)
                return _segments.All(s => s.Position >= s.End);
        }
    }

    /// <summary>Speicherstand; fertige Nachbarn werden zusammengefasst.</summary>
    public List<SegmentState> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<SegmentState>();
            foreach (var segment in _segments)
            {
                var last = result.Count > 0 ? result[^1] : null;
                if (last is not null && last.Position == last.End && last.End == segment.Start)
                {
                    last.End = segment.End;
                    last.Position = segment.Position;
                }
                else
                {
                    result.Add(new SegmentState { Start = segment.Start, Position = segment.Position, End = segment.End });
                }
            }
            return result;
        }
    }

    public (IReadOnlyList<SegmentView> Segments, long Downloaded, int Active) View()
    {
        lock (_lock)
        {
            var views = _segments.Select(s => new SegmentView(s.Start, s.Position, s.End, s.Owner >= 0)).ToList();
            return (views, _segments.Sum(s => s.Position - s.Start), _segments.Count(s => s.Owner >= 0));
        }
    }
}

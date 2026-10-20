using System.Globalization;
using System.Text.RegularExpressions;

using TunasDM.Core;

namespace TunasDM.Core.Management;

/// <summary>
/// Batch-Downloads: Platzhalter in eckigen Klammern erzeugen viele Adressen.
/// <c>[1-20]</c> zählt, <c>[01-20]</c> mit führenden Nullen, <c>[0-100:5]</c> in Schritten, <c>[a-z]</c> Buchstaben.
/// Mehrere Platzhalter werden kombiniert (z. B. Staffel × Folge).
/// </summary>
public static partial class BatchPattern
{
    public const int MaxLinks = 10_000;

    public static bool HasPattern(string text) => Range().IsMatch(text);

    /// <summary>Anzahl der Adressen, ohne sie zu erzeugen (0 = kein gültiges Muster).</summary>
    public static long Count(string text)
    {
        var ranges = Range().Matches(text).Select(Parse).ToList();
        if (ranges.Count == 0 || ranges.Any(r => r is null))
            return 0;
        long count = 1;
        foreach (var range in ranges)
            count = Math.Min(count * range!.Count, long.MaxValue / 2);
        return count;
    }

    /// <summary>Alle Adressen in Reihenfolge; wirft bei mehr als <see cref="MaxLinks"/>.</summary>
    public static IReadOnlyList<string> Expand(string text)
    {
        var matches = Range().Matches(text);
        if (matches.Count == 0)
            return [text];
        var ranges = matches.Select(Parse).ToList();
        if (ranges.Any(r => r is null))
            throw new FormatException(L.T("Ungültiger Platzhalter. Beispiele: [1-20], [01-20], [0-100:5], [a-z]."));
        long total = Count(text);
        if (total > MaxLinks)
            throw new FormatException(L.F("Das ergibt {0:N0} Adressen; höchstens {1:N0} sind möglich.", total, MaxLinks));

        var results = new List<string> { "" };
        int last = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            string literal = text[last..matches[i].Index];
            var values = ranges[i]!.Values().ToList();
            results = results.SelectMany(prefix => values.Select(v => prefix + literal + v)).ToList();
            last = matches[i].Index + matches[i].Length;
        }
        string tail = text[last..];
        return results.Select(r => r + tail).ToList();
    }

    private sealed record RangeSpec(long From, long To, long Step, int Width, bool Letters, bool Upper)
    {
        public long Count => (Math.Abs(To - From) / Step) + 1;

        public IEnumerable<string> Values()
        {
            long direction = To >= From ? 1 : -1;
            for (long i = 0, value = From; i < Count; i++, value += Step * direction)
            {
                if (Letters)
                    yield return ((char)((Upper ? 'A' : 'a') + value)).ToString();
                else
                    yield return value.ToString(CultureInfo.InvariantCulture).PadLeft(Width, '0');
            }
        }
    }

    private static RangeSpec? Parse(Match match)
    {
        string from = match.Groups["from"].Value;
        string to = match.Groups["to"].Value;
        long step = match.Groups["step"].Success ? long.Parse(match.Groups["step"].Value, CultureInfo.InvariantCulture) : 1;
        if (step <= 0)
            return null;
        if (char.IsLetter(from[0]))
        {
            // Nur gleiche Schreibweise: [a-z] oder [A-Z].
            if (from.Length != 1 || to.Length != 1 || char.IsUpper(from[0]) != char.IsUpper(to[0]))
                return null;
            bool upper = char.IsUpper(from[0]);
            char baseChar = upper ? 'A' : 'a';
            return new RangeSpec(from[0] - baseChar, to[0] - baseChar, step, 0, true, upper);
        }
        if (!long.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out long a)
            || !long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out long b))
            return null;
        // Führende Null = feste Breite (01-99 → 01, 02, …); sonst ohne Auffüllen.
        int width = from.Length > 1 && from[0] == '0' || to.Length > 1 && to[0] == '0' ? Math.Max(from.Length, to.Length) : 0;
        return new RangeSpec(a, b, step, width, false, false);
    }

    [GeneratedRegex(@"\[(?<from>\d{1,9}|[a-zA-Z])-(?<to>\d{1,9}|[a-zA-Z])(?::(?<step>\d{1,6}))?\]")]
    private static partial Regex Range();
}

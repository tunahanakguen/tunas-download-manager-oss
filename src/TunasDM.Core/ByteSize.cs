using System.Globalization;

namespace TunasDM.Core;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(double bytes)
    {
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes:0} {Units[unit]}"
            : bytes.ToString(bytes < 10 ? "0.00" : bytes < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>„500K“, „2M“, „1.5G“ oder reine Bytezahl.</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim().ToUpperInvariant().TrimEnd('B');
        if (text.Length == 0)
            return false;

        long factor = text[^1] switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, _ => 1 };
        if (factor > 1)
            text = text[..^1];

        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
            return false;
        bytes = (long)(value * factor);
        return true;
    }
}

using System.Windows;
using System.Windows.Media;
using TunasDM.Core;

namespace TunasDM.App.Controls;

/// <summary>
/// Fortschritt: blau = geladen, dunkel bzw. hell = offen,
/// graue Striche = Startpunkte der Segmente, helle Striche = Verbindungen, die gerade laden.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(DownloadProgress), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Geladenes in Markenblau; Spur und Striche schwarz bzw. weiß je nach Design (steht beim Start fest).
    private static readonly byte Ink = Theme.IsLight ? (byte)0x00 : (byte)0xFF;
    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(Theme.IsLight ? (byte)0x18 : (byte)0x24, Ink, Ink, Ink)));
    private static readonly Brush Done = Freeze(new SolidColorBrush(Theme.AccentColor));
    private static readonly Brush ActiveDone = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, Theme.AccentColor.R, Theme.AccentColor.G, Theme.AccentColor.B)));
    private static readonly Pen StartLine = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x60, Ink, Ink, Ink)), 1));

    public DownloadProgress? Progress
    {
        get => (DownloadProgress?)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, width, height), 3, 3);

        var progress = Progress;
        if (progress is null || width <= 0)
            return;

        if (progress.TotalSize <= 0 || progress.Segments.Count == 0)
        {
            // Größe unbekannt oder kein Live-Stand: einfacher Balken.
            double fraction = progress.TotalSize > 0 ? Math.Clamp(progress.Downloaded / (double)progress.TotalSize, 0, 1) : 0;
            if (fraction > 0)
                dc.DrawRoundedRectangle(Done, null, new Rect(0, 0, width * fraction, height), 3, 3);
            return;
        }

        double scale = width / progress.TotalSize;
        foreach (var segment in progress.Segments)
        {
            double x = segment.Start * scale;
            double w = Math.Max(0, (segment.Position - segment.Start) * scale);
            if (w > 0)
                dc.DrawRectangle(segment.Active ? ActiveDone : Done, null, new Rect(x, 0, w, height));
            if (segment.Start > 0)
                dc.DrawLine(StartLine, new Point(Math.Round(x) + 0.5, 0), new Point(Math.Round(x) + 0.5, height));
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

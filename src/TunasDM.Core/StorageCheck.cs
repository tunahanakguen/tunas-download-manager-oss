namespace TunasDM.Core;

/// <summary>Vor dem Anlegen der Datei: passt sie auf das Laufwerk?</summary>
public static class StorageCheck
{
    /// <summary>Größte Datei auf FAT32: 4 GiB − 1 Byte.</summary>
    public const long Fat32MaxFileSize = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>Puffer, damit das Laufwerk nicht bis aufs letzte Byte vollläuft.</summary>
    public const long Reserve = 16L * 1024 * 1024;

    public static void EnsureFits(string directory, long size)
    {
        if (size <= 0)
            return;
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
            if (!drive.IsReady)
                return;
            Check(drive.Name, drive.DriveFormat, drive.AvailableFreeSpace, size);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Netzlaufwerk oder unbekannter Pfad: nicht prüfbar, das Schreiben meldet Fehler selbst.
        }
    }

    internal static void Check(string drive, string format, long free, long size)
    {
        if (size > Fat32MaxFileSize && string.Equals(format, "FAT32", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException(
                L.F("Die Datei ({0}) ist größer als 4 GB, das Laufwerk {1} ist aber mit FAT32 formatiert. Bitte einen anderen Zielordner wählen (NTFS oder exFAT).",
                    ByteSize.Format(size), drive));
        if (free < size + Reserve)
            throw new DownloadException(
                L.F("Nicht genug Speicherplatz auf {0}: frei {1}, nötig {2}.", drive, ByteSize.Format(free), ByteSize.Format(size)));
    }
}

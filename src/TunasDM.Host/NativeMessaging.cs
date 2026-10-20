using System.Buffers.Binary;

namespace TunasDM.Host;

/// <summary>Rahmen von Chrome/Edge: 4 Byte Länge (little endian, Systemreihenfolge) + UTF-8-JSON.</summary>
internal static class NativeMessaging
{
    /// <summary>Chrome erlaubt vom Host höchstens 1 MB pro Nachricht.</summary>
    public const int MaxOutgoing = 1024 * 1024;

    /// <summary>Großzügige Grenze für eingehende Nachrichten (Linklisten), schützt vor Unsinn.</summary>
    public const int MaxIncoming = 16 * 1024 * 1024;

    /// <summary>Liest eine Nachricht; null = Browser hat die Verbindung geschlossen.</summary>
    public static async Task<byte[]?> ReadAsync(Stream input, CancellationToken token = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(input, header, token))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxIncoming)
            throw new InvalidDataException($"Ungültige Nachrichtenlänge {length}.");

        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(input, body, token))
            throw new EndOfStreamException("Nachricht unvollständig.");
        return body;
    }

    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> json, CancellationToken token = default)
    {
        if (json.Length > MaxOutgoing)
            throw new InvalidDataException("Nachricht zu groß für den Browser.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await output.WriteAsync(header, token);
        await output.WriteAsync(json, token);
        await output.FlushAsync(token);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(filled), token);
            if (read == 0)
                return filled == 0 ? false : throw new EndOfStreamException("Nachricht unvollständig.");
            filled += read;
        }
        return true;
    }
}

using System.Buffers.Binary;
using System.Text.Json;

namespace PeripheralMonitor.Shared.Models;

/// <summary>
/// Settings the server bakes into a downloaded Windows setup .exe.
/// </summary>
public sealed record InstallerConfig(string SignalRUrl, string JwtToken);

/// <summary>
/// Format for the config block appended to the end of the agent .exe at download time:
///
///   [agent exe bytes][UTF-8 JSON][int64 little-endian JSON length][16-byte magic]
///
/// The .NET single-file host locates its bundle by an offset stored inside the exe,
/// so trailing bytes are ignored at runtime. This lets the (Linux) server produce a
/// per-institution installer by streaming the stock exe and appending this trailer,
/// with no rebuild and no Windows tooling.
/// </summary>
public static class InstallerConfigTrailer
{
    private static readonly byte[] Magic = "PMAGENT-CONFIG01"u8.ToArray();
    private const int FooterSize = 8 + 16;           // length + magic
    private const int MaxPayload = 64 * 1024;        // sanity limit

    public static byte[] Build(InstallerConfig config)
    {
        var json   = JsonSerializer.SerializeToUtf8Bytes(config);
        var result = new byte[json.Length + FooterSize];
        json.CopyTo(result, 0);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(json.Length, 8), json.Length);
        Magic.CopyTo(result, json.Length + 8);
        return result;
    }

    /// <summary>Returns the embedded config, or null if the file has none.</summary>
    public static InstallerConfig? TryRead(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < FooterSize) return null;

            var footer = new byte[FooterSize];
            fs.Seek(-FooterSize, SeekOrigin.End);
            fs.ReadExactly(footer);
            if (!footer.AsSpan(8).SequenceEqual(Magic)) return null;

            var len = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(0, 8));
            if (len <= 0 || len > MaxPayload || len > fs.Length - FooterSize) return null;

            var json = new byte[len];
            fs.Seek(-(FooterSize + len), SeekOrigin.End);
            fs.ReadExactly(json);
            var cfg = JsonSerializer.Deserialize<InstallerConfig>(json);
            return cfg is { SignalRUrl.Length: > 0, JwtToken.Length: > 0 } ? cfg : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

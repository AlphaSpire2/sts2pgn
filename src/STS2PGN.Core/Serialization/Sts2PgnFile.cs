using System.IO.Compression;
using System.Text;

namespace STS2PGN.Serialization;

/// <summary>File transport for plain UTF-8 traces and gzip-wrapped traces.</summary>
public static class Sts2PgnFile
{
    public const string GzipExtension = ".gz";

    /// <summary>
    /// Opens a trace for the existing text parser. Gzip is detected by its magic bytes,
    /// including when a compressed file has been renamed. The caller owns the reader.
    /// </summary>
    public static StreamReader OpenText(string path)
    {
        FileStream file = File.OpenRead(path);
        try
        {
            bool gzip = file.ReadByte() == 0x1f && file.ReadByte() == 0x8b;
            file.Position = 0;
            Stream stream = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
            return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Recognizes both filename forms when discovering playback inputs.</summary>
    public static bool IsTracePath(string path) =>
        path.EndsWith(FormatConstants.FileExtension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(FormatConstants.FileExtension + GzipExtension, StringComparison.OrdinalIgnoreCase);
}

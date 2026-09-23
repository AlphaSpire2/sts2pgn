using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using STS2PGN.Model;

namespace STS2PGN.Serialization;

/// <summary>
/// Atomically updates the PGN-style Result header after the append-only stream has
/// already written and durably flushed its authoritative terminal result.
/// </summary>
public static class Sts2PgnFileFinalizer
{
    /// <summary>
    /// Finalizes a completed recording, then replaces it with a verified gzip file.
    /// The plain recording survives any failure before the compressed file is published.
    /// Existing compressed files are never overwritten. Incomplete recordings must stay plain.
    /// </summary>
    public static string FinalizeRecording(string path, RunResult result)
    {
        FinalizeResultHeader(path, result);
        string compressedPath = path + Sts2PgnFile.GzipExtension;
        string temporaryPath = $"{compressedPath}.compressing-{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream source = File.OpenRead(path))
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (FileStream temporary = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (GZipStream gzip = new(temporary, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        byte[] buffer = new byte[64 * 1024];
                        int read;
                        while ((read = source.Read(buffer)) != 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            gzip.Write(buffer, 0, read);
                        }
                    }

                    // Dispose gzip first so its trailer is included in the durable flush.
                    temporary.Flush(flushToDisk: true);
                }

                using FileStream verificationFile = File.OpenRead(temporaryPath);
                using GZipStream verification = new(verificationFile, CompressionMode.Decompress);
                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(verification)))
                {
                    throw new InvalidDataException("Compressed recording did not reproduce the finalized trace.");
                }
            }

            // Publish on the same filesystem, then remove the original only after verification.
            File.Move(temporaryPath, compressedPath, overwrite: false);
            File.Delete(path);
            return compressedPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void FinalizeResultHeader(string path, RunResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (result == RunResult.Incomplete)
        {
            throw new ArgumentException("A finalized file requires a terminal result.", nameof(result));
        }

        const string incompleteHeader = "[Result \"*\"]";
        string finalHeader = $"[Result \"{result.ToToken()}\"]";
        string contents = File.ReadAllText(path, Encoding.UTF8);
        int headerIndex = contents.IndexOf(incompleteHeader, StringComparison.Ordinal);
        if (headerIndex < 0)
        {
            throw new InvalidDataException("The trace Result header could not be finalized.");
        }

        contents = contents[..headerIndex] + finalHeader + contents[(headerIndex + incompleteHeader.Length)..];
        string temporaryPath = $"{path}.finalizing-{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream temporary = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(
                temporary,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024,
                leaveOpen: true))
            {
                writer.NewLine = "\n";
                writer.Write(contents);
                writer.Flush();
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

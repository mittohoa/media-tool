using System.IO.Hashing;
using MediaTool.Core.Util;

namespace MediaTool.Core.Hashing;

/// <summary>
/// Reads file bytes and reduces them to the two hashes the exact-duplicate cascade needs.
///
/// On the choice of xxHash over BLAKE3/SHA-256: this hash decides which files are reported
/// as identical, not which are deleted. Grouping is done on 128 bits plus an exact size
/// match, where an accidental collision across a few million files is far below the odds of
/// a silent disk error. Anything that actually removes a file byte-compares first, so the
/// hash never has to carry that weight alone — and xxHash runs several times faster than a
/// cryptographic digest on the same I/O budget.
/// </summary>
public static class ContentHasher
{
    /// <summary>Bytes read from each end during the probe stage.</summary>
    public const int ProbeBytes = 64 * 1024;

    /// <summary>
    /// Below this, probing is pointless: two 64KB reads already cover the file, and the
    /// second seek costs more than finishing it. Such files are hashed in full immediately.
    /// </summary>
    public const long ProbeIsPointlessBelow = 2 * ProbeBytes;

    private const int StreamBufferSize = 1 << 20;   // 1 MB

    public readonly record struct ProbeResult(ulong Partial, byte[]? Content);

    /// <summary>
    /// Stage 2 of the cascade. Reads the head and tail only — except for files small enough
    /// that reading everything is cheaper, where the full content hash comes back too and
    /// stage 3 can skip them entirely.
    /// </summary>
    public static ProbeResult Probe(string fullPath, long size)
    {
        using var stream = OpenSequential(fullPath);

        if (size <= ProbeIsPointlessBelow)
        {
            byte[] all = ReadExactly(stream, (int)size);
            return new ProbeResult(XxHash64.HashToUInt64(all), XxHash128.Hash(all));
        }

        var hash = new XxHash64();

        byte[] head = ReadExactly(stream, ProbeBytes);
        hash.Append(head);

        stream.Seek(-ProbeBytes, SeekOrigin.End);
        byte[] tail = ReadExactly(stream, ProbeBytes);
        hash.Append(tail);

        // The size goes into the probe as well. Without it, two files that share head and
        // tail but differ in length would collide, and the group would be read in full for
        // nothing.
        Span<byte> sizeBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(sizeBytes, size);
        hash.Append(sizeBytes);

        return new ProbeResult(hash.GetCurrentHashAsUInt64(), null);
    }

    /// <summary>Stage 3: the full content hash. Only ever called on files that survived the probe.</summary>
    public static byte[] Full(string fullPath)
    {
        using var stream = OpenSequential(fullPath);
        var hash = new XxHash128();

        byte[] buffer = new byte[StreamBufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.Append(buffer.AsSpan(0, read));

        return hash.GetCurrentHash();
    }

    /// <summary>
    /// Byte-for-byte comparison. The cascade never deletes on a hash alone; this is what a
    /// destructive action is gated on.
    /// </summary>
    public static bool ContentsEqual(string pathA, string pathB)
    {
        using var a = OpenSequential(pathA);
        using var b = OpenSequential(pathB);
        if (a.Length != b.Length) return false;

        byte[] bufA = new byte[StreamBufferSize];
        byte[] bufB = new byte[StreamBufferSize];

        while (true)
        {
            int readA = ReadBlock(a, bufA);
            int readB = ReadBlock(b, bufB);
            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readB))) return false;
        }
    }

    private static FileStream OpenSequential(string fullPath) => new(
        LongPath.Prefix(fullPath),
        FileMode.Open,
        FileAccess.Read,
        // Share everything: a photo library is often open in another app, and refusing to
        // hash a file because a viewer has it open would leave holes in the catalog.
        FileShare.ReadWrite | FileShare.Delete,
        StreamBufferSize,
        FileOptions.SequentialScan);

    private static byte[] ReadExactly(FileStream stream, int count)
    {
        byte[] buffer = new byte[count];
        stream.ReadExactly(buffer, 0, count);
        return buffer;
    }

    private static int ReadBlock(FileStream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}

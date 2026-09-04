using System.IO.Hashing;

namespace MediaTool.Core.Imaging;

/// <summary>Everything one decode produces. Nothing here re-reads the file.</summary>
public readonly struct ImageFingerprint
{
    /// <summary>
    /// Exact identity of the picture itself, independent of the container it sits in.
    /// Two files that hold the same image but differ in metadata land on the same value.
    /// </summary>
    public required byte[] PixelHash { get; init; }

    /// <summary>Difference hash: sensitive to resize and re-encoding.</summary>
    public required ulong DHash { get; init; }

    /// <summary>DCT hash: steadier under brightness and gamma shifts. Rotation-normalised.</summary>
    public required ulong PHash { get; init; }

    /// <summary>16x16 grayscale, 256 bytes. Kept so candidate pairs can be checked without touching the disk.</summary>
    public required byte[] Thumb16 { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>
    /// Standard deviation of the thumbnail. Near-blank images all hash alike, so this is the
    /// guard that keeps scans of white paper from collapsing into one giant false cluster.
    /// </summary>
    public required double Contrast { get; init; }
}

public static class PerceptualHash
{
    private const int N = ImageDecoder.NormalizedSize;   // 64

    /// <summary>
    /// Computes every fingerprint from a single decoded square.
    ///
    /// On EXIF orientation: it is deliberately not applied anywhere in this path. The
    /// problem being solved is duplicates whose metadata was stripped, and the orientation
    /// flag is metadata. Normalising by it would rotate the copy that still has the flag and
    /// leave the stripped copy alone, making the two mismatch — precisely backwards. The raw
    /// pixels are what the two copies share, so the raw pixels are what gets hashed, and
    /// rotation tolerance is instead handled inside PHash.
    /// </summary>
    public static ImageFingerprint Compute(in DecodedImage image)
    {
        byte[] gray = image.Gray;

        byte[] thumb16 = BoxReduce(gray, N, 16);
        double contrast = StandardDeviation(thumb16);

        // Dimensions go into the pixel hash so a thumbnail is never mistaken for its
        // original just because both reduce to a similar square.
        var pixel = new XxHash128();
        pixel.Append(gray);
        Span<byte> dims = stackalloc byte[8];
        BitConverter.TryWriteBytes(dims, image.Width);
        BitConverter.TryWriteBytes(dims[4..], image.Height);
        pixel.Append(dims);

        return new ImageFingerprint
        {
            PixelHash = pixel.GetCurrentHash(),
            DHash = ComputeDHash(gray),
            PHash = ComputePHashRotationInvariant(gray),
            Thumb16 = thumb16,
            Width = image.Width,
            Height = image.Height,
            Contrast = contrast,
        };
    }

    /// <summary>
    /// dHash: reduce to 9x8 and record whether each pixel is brighter than its right-hand
    /// neighbour. Encoding a gradient rather than absolute levels is what makes it survive
    /// re-compression and scaling.
    /// </summary>
    private static ulong ComputeDHash(byte[] gray)
    {
        byte[] small = BoxReduceTo(gray, N, 9, 8);

        ulong bits = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (small[y * 9 + x] > small[y * 9 + x + 1])
                    bits |= 1UL << bit;

        return bits;
    }

    /// <summary>
    /// Hashes all four right-angle rotations and keeps the smallest.
    ///
    /// Both copies of a rotated photo therefore reduce to the same value whether or not
    /// either still carries its orientation flag — which is the rotation tolerance the
    /// decode path deliberately left to this stage. Only 90-degree steps are covered;
    /// arbitrary rotation is a different problem and belongs to the embedding tier.
    /// </summary>
    private static ulong ComputePHashRotationInvariant(byte[] gray)
    {
        ulong best = ulong.MaxValue;
        byte[] current = gray;

        for (int i = 0; i < 4; i++)
        {
            ulong hash = ComputePHash(current);
            if (hash < best) best = hash;
            if (i < 3) current = RotateSquare90(current, N);
        }

        return best;
    }

    private static ulong ComputePHash(byte[] gray)
    {
        const int Size = 32;
        const int Keep = 8;

        byte[] small = BoxReduce(gray, N, Size);

        // Separable 2-D DCT-II. Only the top-left 8x8 is needed, but computing the full
        // transform separably is cheaper than eight partial passes.
        double[] rows = new double[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int u = 0; u < Keep; u++)
            {
                double sum = 0;
                for (int x = 0; x < Size; x++) sum += small[y * Size + x] * Cos[u * Size + x];
                rows[y * Size + u] = sum;
            }

        double[] coefficients = new double[Keep * Keep];
        for (int u = 0; u < Keep; u++)
            for (int v = 0; v < Keep; v++)
            {
                double sum = 0;
                for (int y = 0; y < Size; y++) sum += rows[y * Size + u] * Cos[v * Size + y];
                coefficients[v * Keep + u] = sum;
            }

        // The DC term carries overall brightness and would swamp the median, so it is
        // excluded from the threshold — that is what makes the hash brightness-insensitive.
        double[] sorted = new double[Keep * Keep - 1];
        Array.Copy(coefficients, 1, sorted, 0, sorted.Length);
        Array.Sort(sorted);
        double median = (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

        ulong bits = 0;
        for (int i = 0; i < Keep * Keep; i++)
            if (coefficients[i] > median)
                bits |= 1UL << i;

        return bits;
    }

    private static readonly double[] Cos = BuildCosineTable(32);

    private static double[] BuildCosineTable(int size)
    {
        var table = new double[size * size];
        for (int u = 0; u < size; u++)
            for (int x = 0; x < size; x++)
                table[u * size + x] = Math.Cos((2 * x + 1) * u * Math.PI / (2.0 * size));
        return table;
    }

    /// <summary>Area-average reduction of a square. Integral factors only.</summary>
    private static byte[] BoxReduce(byte[] source, int sourceSize, int targetSize)
    {
        int factor = sourceSize / targetSize;
        var result = new byte[targetSize * targetSize];

        for (int y = 0; y < targetSize; y++)
            for (int x = 0; x < targetSize; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < factor; dy++)
                    for (int dx = 0; dx < factor; dx++)
                        sum += source[(y * factor + dy) * sourceSize + x * factor + dx];
                result[y * targetSize + x] = (byte)(sum / (factor * factor));
            }

        return result;
    }

    /// <summary>Area-average reduction to an arbitrary rectangle, for dHash's 9x8 grid.</summary>
    private static byte[] BoxReduceTo(byte[] source, int sourceSize, int targetWidth, int targetHeight)
    {
        var result = new byte[targetWidth * targetHeight];

        for (int y = 0; y < targetHeight; y++)
        {
            int y0 = y * sourceSize / targetHeight;
            int y1 = Math.Max(y0 + 1, (y + 1) * sourceSize / targetHeight);

            for (int x = 0; x < targetWidth; x++)
            {
                int x0 = x * sourceSize / targetWidth;
                int x1 = Math.Max(x0 + 1, (x + 1) * sourceSize / targetWidth);

                int sum = 0, count = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++, count++)
                        sum += source[sy * sourceSize + sx];

                result[y * targetWidth + x] = (byte)(sum / count);
            }
        }

        return result;
    }

    private static byte[] RotateSquare90(byte[] source, int size)
    {
        var result = new byte[source.Length];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                result[x * size + (size - 1 - y)] = source[y * size + x];
        return result;
    }

    private static double StandardDeviation(byte[] values)
    {
        double mean = 0;
        foreach (byte v in values) mean += v;
        mean /= values.Length;

        double variance = 0;
        foreach (byte v in values) variance += (v - mean) * (v - mean);
        return Math.Sqrt(variance / values.Length);
    }

    /// <summary>
    /// Structural difference between two 16x16 thumbnails, in grey levels on the first
    /// image's scale.
    ///
    /// The second thumbnail is rescaled to the first one's mean and spread before the
    /// comparison. Without that step the check would be brightness-sensitive while pHash —
    /// which drops the DC coefficient precisely so that it is not — had already accepted the
    /// pair, and a photo that was only brightened would be nominated by the hash and then
    /// thrown out by its own verifier.
    /// </summary>
    public static double ThumbnailDistance(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return double.MaxValue;

        var (meanA, sdA) = MeanAndSpread(a);
        var (meanB, sdB) = MeanAndSpread(b);

        // A flat thumbnail carries no structure to rescale; fall back to raw levels rather
        // than amplifying its noise by dividing by a near-zero spread.
        double gain = sdB < 1e-6 ? 1.0 : sdA / sdB;

        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double adjusted = (b[i] - meanB) * gain + meanA;
            sum += Math.Abs(a[i] - adjusted);
        }
        return sum / a.Length;
    }

    private static (double Mean, double Spread) MeanAndSpread(byte[] values)
    {
        double mean = 0;
        foreach (byte v in values) mean += v;
        mean /= values.Length;

        double variance = 0;
        foreach (byte v in values) variance += (v - mean) * (v - mean);
        return (mean, Math.Sqrt(variance / values.Length));
    }

    public static int HammingDistance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}

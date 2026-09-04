namespace MediaTool.Core.Imaging;

/// <summary>What the shape of the difference between two images says about their relationship.</summary>
public enum DifferenceKind
{
    /// <summary>Nothing measurable separates them.</summary>
    Identical,

    /// <summary>Difference spread thinly across the whole frame — the signature of re-encoding or resizing.</summary>
    Recompressed,

    /// <summary>Difference gathered in one region — something in the scene moved.</summary>
    Moved,

    /// <summary>Difference around the edges, with the centre intact — one is a crop of the other.</summary>
    Cropped,

    /// <summary>Too much has changed for these to be versions of one photograph.</summary>
    Different,
}

public sealed class DifferenceResult
{
    /// <summary>BGRA heatmap, ready to display.</summary>
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Average absolute difference in grey levels, after matching brightness.</summary>
    public required double MeanDifference { get; init; }

    /// <summary>
    /// How much of the total difference sits in the busiest tenth of the frame.
    ///
    /// This is the number that separates the two cases a reviewer actually has to tell
    /// apart. Re-encoding sprinkles error evenly, so its busiest tenth holds barely more
    /// than a tenth of the total. A person shifting between two frames concentrates almost
    /// all of it into the few cells they occupy.
    /// </summary>
    public required double Concentration { get; init; }

    /// <summary>
    /// Share of the frame that differs enough to be seen, 0-1.
    ///
    /// Reported alongside the shape because the two answer different questions. A resave
    /// touches most of the frame by a hair; a person moving touches a little of it a lot.
    /// Area on its own would call the resave the bigger change, which is backwards.
    /// </summary>
    public required double ChangedArea { get; init; }

    public required DifferenceKind Kind { get; init; }

    public string Verdict => Kind switch
    {
        DifferenceKind.Identical => "no visible difference",
        DifferenceKind.Recompressed => "spread evenly — re-encoded or resized, same moment",
        DifferenceKind.Moved => "concentrated — something moved, likely a different frame",
        DifferenceKind.Cropped => "around the edges — one is a crop of the other",
        _ => "too much differs — probably not the same photograph",
    };

    /// <summary>
    /// The measurements behind the verdict, for when the words are not enough.
    ///
    /// The area is always stated when there is one, including under a verdict of no visible
    /// difference - a downscaled copy can leave speckles along hard edges while still being
    /// the same picture, and claiming otherwise would be contradicted by the map beside it.
    /// </summary>
    public string Measurements
    {
        get
        {
            if (ChangedArea <= 0) return $"{MeanDifference:F1}/255 average, nothing above the visible floor";

            string measured = $"{ChangedArea:P1} of the frame differs · {MeanDifference:F1}/255 average";
            return Kind == DifferenceKind.Identical
                ? measured
                : $"{measured} · {Concentration:P0} of it in the busiest tenth";
        }
    }

    /// <summary>True when the shape of the difference argues these are two separate exposures.</summary>
    public bool SuggestsSeparateExposure => Kind is DifferenceKind.Moved or DifferenceKind.Different;
}

/// <summary>
/// Shows where two near-identical images differ, and reads the shape of that difference.
///
/// The hardest call in reviewing near-duplicates is whether two frames are one photo saved
/// twice or two photos taken a second apart. Side by side they look the same; the answer is
/// in the small parts that changed, which is exactly what the eye skips over. Subtracting one
/// from the other puts that where it can be seen.
///
/// The shape carries the answer more reliably than the amount does. A re-encoded copy differs
/// slightly everywhere. A second exposure differs a lot in one place and nowhere else. The
/// same total difference means opposite things depending on how it is distributed.
/// </summary>
public static class DifferenceMap
{
    /// <summary>The heatmap and analysis are computed at this longest side.</summary>
    public const int WorkingSize = 192;

    /// <summary>Fraction of cells counted as "the busiest part" when measuring concentration.</summary>
    private const double BusiestFraction = 0.10;

    /// <summary>Grey levels of difference the heatmap treats as nothing, so it agrees with the verdict.</summary>
    private const int DeadZone = 2;

    /// <summary>Grey levels a cell must differ by before it counts towards the changed area.</summary>
    private const int VisibleThreshold = 6;

    public static DifferenceResult Compare(string referencePath, string otherPath)
    {
        var reference = PreviewDecoder.Decode(referencePath, WorkingSize);
        var other = PreviewDecoder.Decode(otherPath, WorkingSize);
        return Compare(reference, other);
    }

    public static DifferenceResult Compare(in PreviewImage reference, in PreviewImage other)
    {
        int width = reference.Width;
        int height = reference.Height;

        byte[] a = ToGray(reference);
        // The two decodes rarely land on identical dimensions — a resized copy has a
        // different aspect — so the second is resampled onto the first's grid.
        byte[] b = Resample(ToGray(other), other.Width, other.Height, width, height);

        // Brightness is matched first for the same reason the thumbnail check matches it:
        // otherwise a copy that was only brightened lights up the entire map and reads as a
        // completely different picture.
        MatchBrightness(a, b);

        var diff = new byte[a.Length];
        long total = 0;
        int changed = 0;

        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            diff[i] = (byte)d;
            total += d;
            if (d >= VisibleThreshold) changed++;
        }

        double mean = (double)total / a.Length;
        double concentration = Concentrate(diff, total);
        double edgeShare = EdgeShare(diff, width, height, total);

        var kind = Classify(mean, concentration, edgeShare);

        return new DifferenceResult
        {
            Bgra = Render(diff, width, height),
            Width = width,
            Height = height,
            MeanDifference = mean,
            Concentration = concentration,
            ChangedArea = (double)changed / a.Length,
            Kind = kind,
        };
    }

    private static DifferenceKind Classify(double mean, double concentration, double edgeShare)
    {
        if (mean < 1.0) return DifferenceKind.Identical;
        if (mean > 40) return DifferenceKind.Different;

        // A crop leaves the middle alone and changes the border, which no amount of
        // re-encoding does.
        if (edgeShare > 0.65 && concentration < 0.55) return DifferenceKind.Cropped;

        // A tenth of the cells holding half the difference is not something a codec does.
        if (concentration > 0.50) return DifferenceKind.Moved;
        if (concentration < 0.32) return DifferenceKind.Recompressed;

        return mean < 6 ? DifferenceKind.Recompressed : DifferenceKind.Moved;
    }

    /// <summary>Share of the total difference held by the busiest tenth of the cells.</summary>
    private static double Concentrate(byte[] diff, long total)
    {
        if (total == 0) return 0;

        // A 256-bucket histogram beats sorting 37k values and is exact for byte data.
        Span<int> histogram = stackalloc int[256];
        foreach (byte d in diff) histogram[d]++;

        int busiestCount = Math.Max(1, (int)(diff.Length * BusiestFraction));
        long busiestEnergy = 0;
        int taken = 0;

        for (int value = 255; value >= 0 && taken < busiestCount; value--)
        {
            int take = Math.Min(histogram[value], busiestCount - taken);
            busiestEnergy += (long)take * value;
            taken += take;
        }

        return (double)busiestEnergy / total;
    }

    /// <summary>Share of the difference sitting in the outer fifth of the frame.</summary>
    private static double EdgeShare(byte[] diff, int width, int height, long total)
    {
        if (total == 0) return 0;

        int marginX = Math.Max(1, width / 5);
        int marginY = Math.Max(1, height / 5);
        long edge = 0;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (x < marginX || x >= width - marginX || y < marginY || y >= height - marginY)
                    edge += diff[y * width + x];

        return (double)edge / total;
    }

    /// <summary>
    /// Paints the difference: dark where the images agree, amber then white where they do
    /// not. The scale is deliberately steep at the low end, because the differences that
    /// matter here are small ones that a linear ramp would leave invisible.
    /// </summary>
    private static byte[] Render(byte[] diff, int width, int height)
    {
        var bgra = new byte[width * height * 4];

        for (int i = 0; i < diff.Length; i++)
        {
            // Below the dead zone the map stays black. Without it the steep ramp makes a
            // difference of one or two grey levels glow, which reads as a warning beside a
            // verdict that says there is nothing to see.
            double t = Math.Clamp(Math.Sqrt(Math.Max(0, diff[i] - DeadZone) / 46.0), 0, 1);

            byte r = (byte)(20 + 235 * Math.Min(1, t * 1.6));
            byte g = (byte)(24 + 160 * Math.Max(0, t * 1.3 - 0.15));
            byte b = (byte)(30 + 200 * Math.Max(0, t * 2.0 - 1.05));

            int o = i * 4;
            bgra[o] = b;
            bgra[o + 1] = g;
            bgra[o + 2] = r;
            bgra[o + 3] = 255;
        }

        return bgra;
    }

    private static byte[] ToGray(in PreviewImage image)
    {
        var gray = new byte[image.Width * image.Height];

        for (int i = 0; i < gray.Length; i++)
        {
            int o = i * 4;
            // Rec. 601 luma, which is what the perceptual hashes use as well.
            gray[i] = (byte)((image.Bgra[o + 2] * 299 + image.Bgra[o + 1] * 587 + image.Bgra[o] * 114) / 1000);
        }

        return gray;
    }

    private static byte[] Resample(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height) return source;

        var result = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(sourceHeight - 1, y * sourceHeight / height);
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(sourceWidth - 1, x * sourceWidth / width);
                result[y * width + x] = source[sy * sourceWidth + sx];
            }
        }

        return result;
    }

    /// <summary>Rescales <paramref name="b"/> onto the mean and spread of <paramref name="a"/>.</summary>
    private static void MatchBrightness(byte[] a, byte[] b)
    {
        var (meanA, spreadA) = Statistics(a);
        var (meanB, spreadB) = Statistics(b);

        double gain = spreadB < 1e-6 ? 1.0 : spreadA / spreadB;

        for (int i = 0; i < b.Length; i++)
            b[i] = (byte)Math.Clamp((b[i] - meanB) * gain + meanA, 0, 255);
    }

    private static (double Mean, double Spread) Statistics(byte[] values)
    {
        double mean = 0;
        foreach (byte v in values) mean += v;
        mean /= values.Length;

        double variance = 0;
        foreach (byte v in values) variance += (v - mean) * (v - mean);

        return (mean, Math.Sqrt(variance / values.Length));
    }
}

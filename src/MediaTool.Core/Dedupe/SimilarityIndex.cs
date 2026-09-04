using MediaTool.Core.Imaging;
using MediaTool.Core.Storage;

namespace MediaTool.Core.Dedupe;

public sealed class SimilarityOptions
{
    /// <summary>Bits that may differ out of 64 before two images stop being candidates.</summary>
    public int MaxHamming { get; set; } = 4;

    /// <summary>Mean absolute grey-level difference between 16x16 thumbnails, 0-255.</summary>
    public double MaxThumbnailDistance { get; set; } = 8.0;

    /// <summary>
    /// Thumbnails flatter than this are held out of clustering. Blank scans, black frames and
    /// solid colours all produce the same perceptual hash by construction, so without this
    /// guard they collapse into one enormous false cluster that swamps every real result.
    /// </summary>
    public double MinContrast { get; set; } = 6.0;

    /// <summary>
    /// Cap on how many distinct hashes one band bucket may hold before it is skipped.
    /// A pathological bucket is quadratic; skipping it and saying so beats stalling.
    /// </summary>
    public int MaxBucketSize { get; set; } = 4000;

    /// <summary>Restricts which catalogued files take part. Empty means the whole catalog.</summary>
    public Storage.CatalogScope Scope { get; set; } = new();
}

public sealed class SimilarEntry
{
    public required long FileKey { get; init; }
    public required string VolumeName { get; init; }
    public required string RelativePath { get; init; }
    public required long Size { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string PixelHash { get; init; }
    public required bool HasExactByteTwin { get; init; }

    public string FullPath => VolumeName.EndsWith('\\') ? VolumeName + RelativePath : VolumeName + '\\' + RelativePath;
}

public sealed class SimilarCluster
{
    public required List<SimilarEntry> Entries { get; init; }

    /// <summary>True when every member decodes to the identical picture — tier 2, no threshold involved.</summary>
    public bool PixelIdentical => Entries.Select(e => e.PixelHash).Distinct().Count() == 1;

    /// <summary>
    /// Members grouped by the exact picture they decode to, largest group first.
    ///
    /// A cluster usually mixes both tiers: a few files that are the same picture with
    /// different metadata, plus resized or recompressed versions of it. Reporting only the
    /// cluster-wide verdict would hide the distinction that matters most when deciding what
    /// to keep — within one of these groups the choice is free, between them it is not.
    /// </summary>
    public List<List<SimilarEntry>> PixelGroups => Entries
        .GroupBy(e => e.PixelHash, StringComparer.Ordinal)
        .Select(g => g.ToList())
        .OrderByDescending(g => g.Count)
        .ThenByDescending(g => g[0].Size)
        .ToList();

    /// <summary>Files that are byte-different copies of an identical picture — pure metadata noise.</summary>
    public int MetadataOnlyDuplicates => PixelGroups.Sum(g => g.Count - 1);

    public long LargestSize => Entries.Max(e => e.Size);
    public long ReclaimableBytes => Entries.Sum(e => e.Size) - LargestSize;
    public int MaxPixels => Entries.Max(e => e.Width * e.Height);
}

public sealed class SimilarityResult
{
    public List<SimilarCluster> Clusters { get; init; } = [];
    public long DecodedImages;
    public long DistinctFingerprints;
    public long LowContrastHeldBack;
    public long OversizedBucketsSkipped;
    public long CandidatePairs;
    public long ConfirmedPairs;

    /// <summary>Pairs refused because the camera recorded two different capture times.</summary>
    public long SeparateMomentsRejected;

    /// <summary>Clusters that had merged more than one exposure and were broken apart again.</summary>
    public long ClustersSplitByExposure;

    /// <summary>How many of the clustered images have a capture time available to check against.</summary>
    public long WithCaptureTime;

    /// <summary>Count of candidate pairs by Hamming distance, index 0..64. For threshold calibration.</summary>
    public long[] HammingHistogram = new long[65];

    /// <summary>Count of candidate pairs by thumbnail distance, one bucket per grey level, 0..63.</summary>
    public long[] ThumbnailHistogram = new long[64];
}

/// <summary>
/// Tiers 2 and 3 of the matching stage.
///
/// Tier 2 is a plain equality group on the pixel hash — same picture, whatever the file said
/// about itself. Tier 3 finds near matches without ever comparing all pairs, using the
/// pigeonhole property of a banded hash: if two 64-bit hashes differ in at most T bits and
/// the hash is cut into T+1 bands, at least one band must be identical. So only files that
/// share a band exactly are ever compared, and 87k images cost thousands of lookups instead
/// of billions of comparisons.
/// </summary>
public sealed class SimilarityIndex
{
    private readonly CatalogDatabase _db;
    private readonly SimilarityOptions _options;

    public SimilarityIndex(CatalogDatabase db, SimilarityOptions options)
    {
        _db = db;
        _options = options;
    }

    private sealed class Fingerprints
    {
        public long[] FileKeys = [];
        public ulong[] DHash = [];
        public ulong[] PHash = [];
        public byte[][] Thumbs = [];
        public string[] PixelHash = [];
        public double[] Contrast = [];
        public long[] DateTaken = [];
        public int[] SubSecond = [];
        public string[] Name = [];
        public int Count;
    }

    public SimilarityResult Build(Action<string>? log = null)
    {
        var result = new SimilarityResult();

        log?.Invoke("loading fingerprints");
        var data = LoadFingerprints();
        result.DecodedImages = data.Count;
        result.WithCaptureTime = data.DateTaken.Count(t => t != 0);
        if (data.Count == 0) return result;

        var union = new UnionFind(data.Count);

        // Collapse identical fingerprints first. Every exact copy of one photo shares a hash,
        // so without this the biggest real duplicate groups become the biggest band buckets
        // and dominate the quadratic term for no new information.
        log?.Invoke("collapsing identical fingerprints");
        var representatives = CollapseIdentical(data, union);
        result.DistinctFingerprints = representatives.Count;

        // Tier 2: identical decoded picture. No threshold, no verification needed.
        log?.Invoke("grouping by pixel hash");
        GroupByPixelHash(data, union);

        // Tier 3, over representatives only.
        log?.Invoke($"matching (hamming <= {_options.MaxHamming}, thumbnail <= {_options.MaxThumbnailDistance:F1})");
        var eligible = representatives
            .Where(i => data.Contrast[i] >= _options.MinContrast)
            .ToList();
        result.LowContrastHeldBack = representatives.Count - eligible.Count;

        MatchWithBandedIndex(data, eligible, union, result);

        log?.Invoke("building clusters");
        // Union-find is transitive, and that is a liability here: a copy with no capture
        // time links to two frames the guards had explicitly kept apart, and the three end
        // up in one cluster. The guards can only ever refuse an edge; nothing stops a
        // cluster forming around one. So every finished cluster is checked as a whole.
        SplitClustersByExposure(data, union, result);
        result.Clusters.AddRange(BuildClusters(data, union));
        result.Clusters.Sort((a, b) => b.ReclaimableBytes.CompareTo(a.ReclaimableBytes));

        return result;
    }

    private Fingerprints LoadFingerprints()
    {
        var keys = new List<long>();
        var dhash = new List<ulong>();
        var phash = new List<ulong>();
        var thumbs = new List<byte[]>();
        var pixel = new List<string>();
        var contrast = new List<double>();
        var taken = new List<long>();
        var subsec = new List<int>();
        var names = new List<string>();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT file_key, dhash, phash, thumb16, pixel_hash, contrast, date_taken, sub_sec, name
            FROM files
            WHERE present=1 AND decode_state=1 AND thumb16 IS NOT NULL
            """ + _options.Scope.ToSqlPredicate("files") + " ORDER BY file_key";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(reader.GetInt64(0));
            dhash.Add(unchecked((ulong)reader.GetInt64(1)));
            phash.Add(unchecked((ulong)reader.GetInt64(2)));
            thumbs.Add((byte[])reader[3]);
            pixel.Add(Convert.ToHexString((byte[])reader[4]));
            contrast.Add(reader.IsDBNull(5) ? 0 : reader.GetDouble(5));
            taken.Add(reader.IsDBNull(6) ? 0 : reader.GetInt64(6));
            subsec.Add(reader.IsDBNull(7) ? -1 : reader.GetInt32(7));
            names.Add(reader.GetString(8));
        }

        return new Fingerprints
        {
            FileKeys = [.. keys],
            DHash = [.. dhash],
            PHash = [.. phash],
            Thumbs = [.. thumbs],
            PixelHash = [.. pixel],
            Contrast = [.. contrast],
            DateTaken = [.. taken],
            SubSecond = [.. subsec],
            Name = [.. names],
            Count = keys.Count,
        };
    }

    /// <summary>
    /// Merges files whose fingerprints are bit-for-bit equal, so the banded index below only
    /// ever sees one member of each set.
    ///
    /// Equal hashes are NOT sufficient on their own: consecutive frames of a burst routinely
    /// produce the identical 64-bit hash, and merging on that alone would slip them past
    /// every later check — the guards in Evaluate would never see the pair at all. So the
    /// same non-visual tests run here, splitting an equal-hash set into one sub-group per
    /// exposure.
    /// </summary>
    private static List<int> CollapseIdentical(Fingerprints data, UnionFind union)
    {
        var seen = new Dictionary<(ulong, ulong), List<int>>(data.Count);
        var representatives = new List<int>();

        for (int i = 0; i < data.Count; i++)
        {
            var key = (data.DHash[i], data.PHash[i]);
            if (!seen.TryGetValue(key, out var subGroups)) seen[key] = subGroups = [];

            int match = -1;
            foreach (int candidate in subGroups)
                if (!AreSeparateExposures(data, candidate, i)) { match = candidate; break; }

            if (match >= 0)
            {
                union.Union(match, i);
            }
            else
            {
                subGroups.Add(i);
                representatives.Add(i);
            }
        }

        return representatives;
    }

    private static void GroupByPixelHash(Fingerprints data, UnionFind union)
    {
        var seen = new Dictionary<string, int>(data.Count, StringComparer.Ordinal);
        for (int i = 0; i < data.Count; i++)
        {
            if (seen.TryGetValue(data.PixelHash[i], out int first)) union.Union(first, i);
            else seen[data.PixelHash[i]] = i;
        }
    }

    private void MatchWithBandedIndex(
        Fingerprints data, List<int> eligible, UnionFind union, SimilarityResult result)
    {
        int bandCount = _options.MaxHamming + 1;
        var (offsets, widths) = SplitBands(bandCount);

        var evaluated = new HashSet<long>();

        // Two independent hashes, each with its own band index. Union of their candidates
        // raises recall more cheaply than loosening the threshold on either one alone -
        // a wider threshold multiplies bucket sizes, a second hash does not.
        foreach (var selector in new Func<int, ulong>[] { i => data.DHash[i], i => data.PHash[i] })
        {
            for (int band = 0; band < bandCount; band++)
            {
                var buckets = new Dictionary<ulong, List<int>>();
                ulong mask = widths[band] == 64 ? ulong.MaxValue : (1UL << widths[band]) - 1;

                foreach (int i in eligible)
                {
                    ulong key = (selector(i) >> offsets[band]) & mask;
                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                    list.Add(i);
                }

                foreach (var bucket in buckets.Values)
                {
                    if (bucket.Count < 2) continue;
                    if (bucket.Count > _options.MaxBucketSize)
                    {
                        result.OversizedBucketsSkipped++;
                        continue;
                    }

                    for (int a = 0; a < bucket.Count; a++)
                        for (int b = a + 1; b < bucket.Count; b++)
                            Evaluate(data, bucket[a], bucket[b], union, result, evaluated);
                }
            }
        }
    }

    private void Evaluate(
        Fingerprints data, int i, int j, UnionFind union, SimilarityResult result, HashSet<long> evaluated)
    {
        // Already in the same cluster - a third comparison cannot change that.
        if (union.Find(i) == union.Find(j)) return;

        long pairKey = ((long)Math.Min(i, j) << 32) | (uint)Math.Max(i, j);
        if (!evaluated.Add(pairKey)) return;

        if (AreSeparateExposures(data, i, j))
        {
            result.SeparateMomentsRejected++;
            return;
        }

        int hamming = Math.Min(
            PerceptualHash.HammingDistance(data.DHash[i], data.DHash[j]),
            PerceptualHash.HammingDistance(data.PHash[i], data.PHash[j]));

        if (hamming > _options.MaxHamming) return;

        result.CandidatePairs++;
        result.HammingHistogram[hamming]++;

        // The hash only nominates; the thumbnails decide. Both are already in memory, so
        // confirming a candidate costs 256 byte comparisons and never touches the disk.
        double distance = PerceptualHash.ThumbnailDistance(data.Thumbs[i], data.Thumbs[j]);
        result.ThumbnailHistogram[Math.Min((int)distance, result.ThumbnailHistogram.Length - 1)]++;

        if (distance > _options.MaxThumbnailDistance) return;

        result.ConfirmedPairs++;
        union.Union(i, j);
    }

    /// <summary>
    /// True when two files are two photographs rather than two copies of one.
    ///
    /// Every test here is non-visual on purpose. Two frames of a burst are, to any
    /// perceptual hash, the same image: same scene, same framing, same light, a fraction of
    /// a second apart. Nothing in the pixels separates them at thumbnail scale, so the
    /// evidence has to come from what the camera recorded around them.
    /// </summary>
    private static bool AreSeparateExposures(Fingerprints data, int i, int j)
    {
        if (data.DateTaken[i] != 0 && data.DateTaken[j] != 0)
        {
            // A copy, a re-encode and a resize all preserve the capture time or lose it.
            // None of them invents a different one.
            if (data.DateTaken[i] != data.DateTaken[j]) return true;

            // The same second is not the same instant: DateTimeOriginal resolves to whole
            // seconds while the shutter fires several times inside one.
            if (data.SubSecond[i] >= 0 && data.SubSecond[j] >= 0 &&
                data.SubSecond[i] != data.SubSecond[j]) return true;
        }

        // Falls back to the frame number for anything an editor exported without a capture
        // time. A camera never reuses a number, and copying a file never renumbers it.
        return IsSameSeriesDifferentFrame(data.Name[i], data.Name[j]);
    }

    private static readonly System.Text.RegularExpressions.Regex SequenceName =
        new(@"^(?<stem>.*?)(?<number>\d{2,6})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// True when two names look like consecutive frames from one camera rather than two
    /// copies of one file: same prefix, both ending in a number, numbers different.
    /// </summary>
    internal static bool IsSameSeriesDifferentFrame(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;

        string stemA = Path.GetFileNameWithoutExtension(a);
        string stemB = Path.GetFileNameWithoutExtension(b);

        var matchA = SequenceName.Match(stemA);
        var matchB = SequenceName.Match(stemB);
        if (!matchA.Success || !matchB.Success) return false;

        string prefixA = matchA.Groups["stem"].Value;
        string prefixB = matchB.Groups["stem"].Value;

        // A bare number carries no series, and treating "1.jpg" and "2.jpg" as one sequence
        // would reject unrelated files that merely happen to be numbered.
        if (prefixA.Length == 0 || !string.Equals(prefixA, prefixB, StringComparison.OrdinalIgnoreCase))
            return false;

        return matchA.Groups["number"].Value != matchB.Groups["number"].Value;
    }

    private static (int[] Offsets, int[] Widths) SplitBands(int bandCount)
    {
        var offsets = new int[bandCount];
        var widths = new int[bandCount];

        int baseWidth = 64 / bandCount;
        int remainder = 64 % bandCount;
        int offset = 0;

        for (int b = 0; b < bandCount; b++)
        {
            widths[b] = baseWidth + (b < remainder ? 1 : 0);
            offsets[b] = offset;
            offset += widths[b];
        }

        return (offsets, widths);
    }

    /// <summary>
    /// Re-splits any cluster that ended up holding more than one exposure.
    ///
    /// Members are regrouped by capture time. A file with no capture time follows whichever
    /// group already holds its exact picture — that is the metadata-stripped copy, and it
    /// belongs with the original it was made from — and otherwise stands on its own rather
    /// than being allowed to bridge two exposures again.
    /// </summary>
    private static void SplitClustersByExposure(Fingerprints data, UnionFind union, SimilarityResult result)
    {
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < data.Count; i++)
        {
            int root = union.Find(i);
            if (!members.TryGetValue(root, out var list)) members[root] = list = [];
            list.Add(i);
        }

        var rebuilt = new UnionFind(data.Count);

        foreach (var cluster in members.Values)
        {
            if (cluster.Count < 2) continue;

            var known = cluster.Where(i => data.DateTaken[i] != 0).ToList();
            var distinctMoments = known
                .Select(i => (data.DateTaken[i], data.SubSecond[i]))
                .Distinct()
                .Count();

            if (distinctMoments <= 1)
            {
                for (int k = 1; k < cluster.Count; k++) rebuilt.Union(cluster[0], cluster[k]);
                continue;
            }

            result.ClustersSplitByExposure++;

            var byMoment = new Dictionary<(long, int), int>();
            var byPicture = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (int i in cluster.OrderByDescending(i => data.DateTaken[i] != 0))
            {
                if (data.DateTaken[i] != 0)
                {
                    var key = (data.DateTaken[i], data.SubSecond[i]);
                    if (byMoment.TryGetValue(key, out int lead)) rebuilt.Union(lead, i);
                    else byMoment[key] = i;
                    byPicture.TryAdd(data.PixelHash[i], i);
                }
                else if (byPicture.TryGetValue(data.PixelHash[i], out int owner))
                {
                    rebuilt.Union(owner, i);
                }
            }
        }

        // Adopt the rebuilt partition wholesale.
        for (int i = 0; i < data.Count; i++) union.Reset(i);
        for (int i = 0; i < data.Count; i++)
        {
            int root = rebuilt.Find(i);
            if (root != i) union.Union(root, i);
        }
    }

    private List<SimilarCluster> BuildClusters(Fingerprints data, UnionFind union)
    {
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < data.Count; i++)
        {
            int root = union.Find(i);
            if (!members.TryGetValue(root, out var list)) members[root] = list = [];
            list.Add(i);
        }

        var wanted = members.Values.Where(m => m.Count > 1).ToList();
        if (wanted.Count == 0) return [];

        var details = LoadDetails(wanted.SelectMany(m => m).Select(i => data.FileKeys[i]));

        var clusters = new List<SimilarCluster>(wanted.Count);
        foreach (var group in wanted)
        {
            var entries = group
                .Select(i => details.GetValueOrDefault(data.FileKeys[i]))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            if (entries.Count > 1) clusters.Add(new SimilarCluster { Entries = entries });
        }

        return clusters;
    }

    private Dictionary<long, SimilarEntry> LoadDetails(IEnumerable<long> fileKeys)
    {
        var result = new Dictionary<long, SimilarEntry>();
        var keys = fileKeys.ToList();

        // Chunked so the IN list never approaches SQLite's parameter limit.
        const int chunk = 500;
        for (int start = 0; start < keys.Count; start += chunk)
        {
            var slice = keys.Skip(start).Take(chunk).ToList();

            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.file_key, COALESCE(v.last_mount_point, v.volume_guid), f.rel_path,
                       f.size, f.img_width, f.img_height, f.pixel_hash, f.content_hash
                FROM files f JOIN volumes v ON v.volume_id = f.volume_id
                WHERE f.file_key IN ({string.Join(',', slice)})
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long key = reader.GetInt64(0);
                result[key] = new SimilarEntry
                {
                    FileKey = key,
                    VolumeName = reader.GetString(1),
                    RelativePath = reader.GetString(2),
                    Size = reader.GetInt64(3),
                    Width = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    Height = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    PixelHash = Convert.ToHexString((byte[])reader[6]),
                    HasExactByteTwin = !reader.IsDBNull(7),
                };
            }
        }

        return result;
    }

    /// <summary>Disjoint set with path compression and union by size.</summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int count)
        {
            _parent = new int[count];
            _size = new int[count];
            for (int i = 0; i < count; i++) { _parent[i] = i; _size[i] = 1; }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        /// <summary>Returns an element to being its own singleton, for rebuilding a partition.</summary>
        public void Reset(int x)
        {
            _parent[x] = x;
            _size[x] = 1;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
        }
    }
}

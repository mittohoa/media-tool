namespace MediaTool.Core.Metadata;

/// <summary>
/// What a file says about itself. This is the evidence the keeper policy runs on: between
/// two copies of one photo, the one that still knows when and how it was taken is the one
/// worth keeping.
/// </summary>
public sealed record ImageMetadata
{
    public bool HasExif { get; init; }

    /// <summary>How many EXIF tags survived. A stripped copy keeps none; a camera original has dozens.</summary>
    public int TagCount { get; init; }

    public DateTime? DateTaken { get; init; }

    /// <summary>
    /// EXIF SubSecTimeOriginal, the fraction of a second the shutter fired at.
    ///
    /// DateTimeOriginal only resolves to whole seconds, and a camera shooting 6 frames a
    /// second puts several distinct photographs inside one of them. Without this field a
    /// burst is indistinguishable from a set of copies by timestamp alone.
    /// </summary>
    public int? SubSecond { get; init; }
    public string? Camera { get; init; }
    public bool HasGps { get; init; }

    /// <summary>1-8 per EXIF, 0 when absent. Its absence is itself a sign of a stripped file.</summary>
    public int Orientation { get; init; }

    /// <summary>
    /// Estimated JPEG quality, 1-100, from the quantization tables. Null for other formats.
    /// The point is not the exact number but the ordering: a re-saved copy quantises harder
    /// than the original it came from.
    /// </summary>
    public int? JpegQuality { get; init; }

    /// <summary>Total bytes of APPn/COM segments — a blunt but effective measure of how much was kept.</summary>
    public int MetadataBytes { get; init; }

    /// <summary>Where the capture date came from, when it did not come from the image itself.</summary>
    public SidecarMetadata.Source SidecarSource { get; init; }

    public string? SidecarPath { get; init; }

    /// <summary>True when the only record of when this was taken is a separate file beside it.</summary>
    public bool DependsOnSidecar => SidecarSource != SidecarMetadata.Source.None && DateTaken is not null;

    /// <summary>
    /// A capture time read out of the filename, kept apart from <see cref="DateTaken"/> on
    /// purpose.
    ///
    /// DateTaken is used to decide that two look-alike files are separate exposures, and
    /// that decision has to rest on something the camera wrote. A filename timestamp does
    /// not qualify: a messaging app stamps the name with when the message was sent, so the
    /// same photograph arriving through two apps would carry two different times and be
    /// wrongly split apart. This value informs ranking and reporting only.
    /// </summary>
    public DateTime? FilenameDate { get; init; }

    public string? FilenamePattern { get; init; }

    public MediaSource Source { get; init; }

    /// <summary>The best capture time available, whatever its provenance.</summary>
    public DateTime? BestDate => DateTaken ?? FilenameDate;

    public static readonly ImageMetadata None = new();
}

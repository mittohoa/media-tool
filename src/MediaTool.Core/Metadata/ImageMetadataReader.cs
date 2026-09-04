namespace MediaTool.Core.Metadata;

/// <summary>
/// The single entry point for "what does this file know about itself", combining what is
/// embedded in the image with whatever sits beside it.
///
/// The order matters and is deliberate. Embedded EXIF is preferred because it travels with
/// the file: copy the photo elsewhere and it comes along. A sidecar does not — it is a
/// separate file that a copy, a move or an upload can leave behind — so it is consulted only
/// where the image itself is silent.
///
/// Without this, a Google Photos export reads as a library with no history at all, and every
/// decision downstream is made on that false premise: the burst guard has no timestamps to
/// separate frames with, and the keeper policy ranks the exported copies below any copy that
/// happens to have kept its EXIF.
/// </summary>
public static class ImageMetadataReader
{
    public static ImageMetadata Read(string fullPath)
    {
        var embedded = JpegMetadata.Read(fullPath);

        // The filename is read either way: it also says which app the file came from, and a
        // copy that arrived through a messaging app is a re-compressed one whatever metadata
        // it happens to carry.
        var fromName = FilenameDate.Read(fullPath);

        if (embedded.DateTaken is not null)
            return embedded with { FilenameDate = fromName.Taken, FilenamePattern = fromName.Pattern, Source = fromName.Source };

        var sidecar = SidecarMetadata.Read(fullPath);

        if (sidecar.Source == SidecarMetadata.Source.None)
            return embedded with { FilenameDate = fromName.Taken, FilenamePattern = fromName.Pattern, Source = fromName.Source };

        return new ImageMetadata
        {
            // HasExif stays a statement about the image file itself. A photo whose date came
            // from a sidecar has still lost its embedded metadata, and the keeper policy is
            // entitled to know that when choosing between two copies.
            HasExif = embedded.HasExif,
            TagCount = embedded.TagCount,
            DateTaken = sidecar.DateTaken ?? embedded.DateTaken,
            SubSecond = embedded.SubSecond,
            Camera = embedded.Camera,
            HasGps = embedded.HasGps || sidecar.HasGps,
            Orientation = embedded.Orientation,
            JpegQuality = embedded.JpegQuality,
            MetadataBytes = embedded.MetadataBytes,
            SidecarSource = sidecar.Source,
            SidecarPath = sidecar.Path,
            FilenameDate = fromName.Taken,
            FilenamePattern = fromName.Pattern,
            Source = fromName.Source,
        };
    }
}

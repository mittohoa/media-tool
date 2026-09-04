using System.Runtime.InteropServices;
using MediaTool.Core.Native;
using MediaTool.Core.Util;

namespace MediaTool.Core.Imaging;

/// <summary>A colour preview sized for display, plus the dimensions it came from.</summary>
public readonly struct PreviewImage
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }

    public int Stride => Width * 4;
}

/// <summary>
/// Decodes a display-sized colour preview.
///
/// Separate from <see cref="ImageDecoder"/> on purpose: that one produces the fixed
/// grayscale square the hashes are computed from, and its output must never change or every
/// stored fingerprint would be invalidated. This one exists purely so a person can look at
/// the picture, and is free to change whenever the UI wants a different size.
///
/// Previews are generated on demand rather than during the decode pass. Reviewing a library
/// means opening a few hundred clusters, not eighty thousand images, so building them all up
/// front would spend hours producing thumbnails nobody will ever look at.
/// </summary>
public static class PreviewDecoder
{
    public static PreviewImage Decode(string fullPath, int maxSide)
    {
        var factory = Wic.Factory;

        Check(factory.CreateDecoderFromFilename(
            LongPath.Prefix(fullPath), IntPtr.Zero, Wic.GenericRead,
            Wic.WICDecodeMetadataCacheOnDemand, out var decoder), "open");

        try
        {
            Check(decoder.GetFrame(0, out var frame), "frame");
            try
            {
                Check(frame.GetSize(out uint sourceWidth, out uint sourceHeight), "size");
                if (sourceWidth == 0 || sourceHeight == 0)
                    throw new InvalidDataException("Image reports zero size.");

                // Aspect is preserved here, unlike in the hashing path: a squashed preview
                // would make it impossible to judge which copy is the better crop.
                double scale = Math.Min((double)maxSide / sourceWidth, (double)maxSide / sourceHeight);
                if (scale > 1) scale = 1;

                uint width = Math.Max(1, (uint)Math.Round(sourceWidth * scale));
                uint height = Math.Max(1, (uint)Math.Round(sourceHeight * scale));

                Check(factory.CreateBitmapScaler(out var scaler), "scaler");
                try
                {
                    Check(scaler.Initialize(frame, width, height, Wic.WICBitmapInterpolationModeFant), "scale");

                    Check(factory.CreateFormatConverter(out var converter), "converter");
                    try
                    {
                        var bgra = Wic.GUID_WICPixelFormat32bppBGRA;
                        Check(converter.Initialize((Wic.IWICBitmapSource)scaler, ref bgra,
                            Wic.WICBitmapDitherTypeNone, IntPtr.Zero, 0.0, Wic.WICBitmapPaletteTypeCustom), "convert");

                        int stride = (int)width * 4;
                        byte[] buffer = new byte[stride * (int)height];

                        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                        try
                        {
                            Check(converter.CopyPixels(IntPtr.Zero, (uint)stride,
                                (uint)buffer.Length, handle.AddrOfPinnedObject()), "copy");
                        }
                        finally { handle.Free(); }

                        return new PreviewImage
                        {
                            Bgra = buffer,
                            Width = (int)width,
                            Height = (int)height,
                            SourceWidth = (int)sourceWidth,
                            SourceHeight = (int)sourceHeight,
                        };
                    }
                    finally { Marshal.ReleaseComObject(converter); }
                }
                finally { Marshal.ReleaseComObject(scaler); }
            }
            finally { Marshal.ReleaseComObject(frame); }
        }
        finally { Marshal.ReleaseComObject(decoder); }
    }

    private static void Check(int hr, string stage)
    {
        if (hr < 0) throw new WicException(stage, hr);
    }
}

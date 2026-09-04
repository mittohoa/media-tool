using System.Runtime.InteropServices;
using MediaTool.Core.Native;
using MediaTool.Core.Util;

namespace MediaTool.Core.Imaging;

/// <summary>A WIC call that failed, tagged with which step it was.</summary>
public sealed class WicException(string stage, int hresult)
    : Exception($"WIC {stage} failed: 0x{hresult:X8}")
{
    public string Stage { get; } = stage;
    public int HResult2 { get; } = hresult;
}

/// <summary>A normalised decode: one small grayscale square plus the original dimensions.</summary>
public readonly struct DecodedImage
{
    /// <summary>NormalizedSize x NormalizedSize, 8-bit grayscale, row-major.</summary>
    public required byte[] Gray { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public static class ImageDecoder
{
    /// <summary>
    /// Everything downstream is derived from this one square. 64 is large enough to make an
    /// accidental collision between two genuinely different photos impossible in practice,
    /// and small enough that the 16x16 thumbnail, the 32x32 DCT input and the 9x8 difference
    /// grid can all be box-reduced from it without another decode.
    /// </summary>
    public const int NormalizedSize = 64;

    /// <summary>
    /// Decodes to a fixed grayscale square.
    ///
    /// Aspect ratio is deliberately squashed rather than letter-boxed: it is what the
    /// standard perceptual hashes assume, and padding would inject constant borders that
    /// drag unrelated images toward each other.
    ///
    /// EXIF orientation is deliberately NOT applied — see PerceptualHash for why that is the
    /// right call for this particular problem rather than an oversight.
    /// </summary>
    public static DecodedImage Decode(string fullPath)
    {
        var factory = Wic.Factory;

        int hr = factory.CreateDecoderFromFilename(
            LongPath.Prefix(fullPath), IntPtr.Zero, Wic.GenericRead,
            Wic.WICDecodeMetadataCacheOnDemand, out var decoder);
        Check(hr, "open");

        try
        {
            Check(decoder.GetFrame(0, out var frame), "frame");
            try
            {
                Check(frame.GetSize(out uint width, out uint height), "size");
                if (width == 0 || height == 0) throw new InvalidDataException("Image reports zero size.");

                Check(factory.CreateBitmapScaler(out var scaler), "scaler");
                try
                {
                    Check(scaler.Initialize(frame, NormalizedSize, NormalizedSize,
                                            Wic.WICBitmapInterpolationModeFant), "scale");

                    Check(factory.CreateFormatConverter(out var converter), "converter");
                    try
                    {
                        var gray = Wic.GUID_WICPixelFormat8bppGray;
                        // The cast is a QueryInterface: COM interop interfaces are declared
                        // flat rather than inherited, so the base view has to be asked for.
                        var scalerAsSource = (Wic.IWICBitmapSource)scaler;
                        Check(converter.Initialize(scalerAsSource, ref gray, Wic.WICBitmapDitherTypeNone,
                                                   IntPtr.Zero, 0.0, Wic.WICBitmapPaletteTypeCustom), "convert");

                        byte[] buffer = new byte[NormalizedSize * NormalizedSize];
                        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                        try
                        {
                            Check(converter.CopyPixels(IntPtr.Zero, NormalizedSize,
                                                       (uint)buffer.Length, handle.AddrOfPinnedObject()), "copy");
                        }
                        finally
                        {
                            handle.Free();
                        }

                        return new DecodedImage { Gray = buffer, Width = (int)width, Height = (int)height };
                    }
                    finally { Marshal.ReleaseComObject(converter); }
                }
                finally { Marshal.ReleaseComObject(scaler); }
            }
            finally { Marshal.ReleaseComObject(frame); }
        }
        finally
        {
            // COM objects here hold the file handle open. Releasing eagerly rather than
            // waiting for the GC keeps a pass over hundreds of thousands of files from
            // accumulating handles faster than finalisation frees them.
            Marshal.ReleaseComObject(decoder);
        }
    }

    private static void Check(int hr, string stage)
    {
        if (hr >= 0) return;

        // Naming the failing step matters: "no codec for this format" and "the interop is
        // wrong" both surface as a bare HRESULT otherwise, and they need opposite responses.
        throw new WicException(stage, hr);
    }
}

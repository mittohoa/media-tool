using System.Runtime.InteropServices;

namespace MediaTool.Core.Native;

/// <summary>
/// Windows Imaging Component interop.
///
/// WIC is the decoder Explorer itself uses, so whatever the shell can show, this can read —
/// including HEIC and camera RAW once the matching Store extensions are installed — with no
/// third-party native dependency to ship. It is also free-threaded, which matters when the
/// decode pass is the bottleneck and has to run across every core.
///
/// Only the handful of methods this tool calls are given real signatures; the rest are
/// declared as placeholders purely to keep the vtable offsets correct.
/// </summary>
internal static class Wic
{
    public static readonly Guid CLSID_WICImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");

    public static readonly Guid GUID_WICPixelFormat8bppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc908");
    public static readonly Guid GUID_WICPixelFormat32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    public const int WICDecodeMetadataCacheOnDemand = 0;
    public const uint GenericRead = 0x80000000;

    // Fant is WIC's area-averaging filter. For heavy downscaling it is the one that keeps a
    // stable result: nearest-neighbour would make the hash depend on which pixels happened
    // to be sampled, which is exactly the instability a perceptual hash must not have.
    public const int WICBitmapInterpolationModeFant = 3;

    public const int WICBitmapDitherTypeNone = 0;
    public const int WICBitmapPaletteTypeCustom = 0;

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename(
            [MarshalAs(UnmanagedType.LPWStr)] string filename,
            IntPtr guidVendor, uint desiredAccess, int metadataOptions,
            out IWICBitmapDecoder decoder);

        [PreserveSig] int CreateDecoderFromStream(IntPtr a, IntPtr b, int c, out IntPtr d);
        [PreserveSig] int CreateDecoderFromFileHandle(IntPtr a, IntPtr b, int c, out IntPtr d);
        [PreserveSig] int CreateComponentInfo(ref Guid a, out IntPtr b);
        [PreserveSig] int CreateDecoder(ref Guid a, IntPtr b, out IntPtr c);
        [PreserveSig] int CreateEncoder(ref Guid a, IntPtr b, out IntPtr c);
        [PreserveSig] int CreatePalette(out IntPtr a);

        [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
        [PreserveSig] int CreateBitmapScaler(out IWICBitmapScaler scaler);

        [PreserveSig] int CreateBitmapClipper(out IntPtr a);
        [PreserveSig] int CreateBitmapFlipRotator(out IntPtr a);
        [PreserveSig] int CreateStream(out IntPtr a);
        [PreserveSig] int CreateColorContext(out IntPtr a);
        [PreserveSig] int CreateColorTransformer(out IntPtr a);
        [PreserveSig] int CreateBitmap(uint a, uint b, ref Guid c, int d, out IntPtr e);
        [PreserveSig] int CreateBitmapFromSource(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int CreateBitmapFromSourceRect(IntPtr a, uint b, uint c, uint d, uint e, out IntPtr f);
        [PreserveSig] int CreateBitmapFromMemory(uint a, uint b, ref Guid c, uint d, uint e, IntPtr f, out IntPtr g);
        [PreserveSig] int CreateBitmapFromHBITMAP(IntPtr a, IntPtr b, int c, out IntPtr d);
        [PreserveSig] int CreateBitmapFromHICON(IntPtr a, out IntPtr b);
        [PreserveSig] int CreateComponentEnumerator(uint a, uint b, out IntPtr c);
        [PreserveSig] int CreateFastMetadataEncoderFromDecoder(IntPtr a, out IntPtr b);
        [PreserveSig] int CreateFastMetadataEncoderFromFrameDecode(IntPtr a, out IntPtr b);
        [PreserveSig] int CreateQueryWriter(ref Guid a, IntPtr b, out IntPtr c);
        [PreserveSig] int CreateQueryWriterFromReader(IntPtr a, IntPtr b, out IntPtr c);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICBitmapDecoder
    {
        [PreserveSig] int QueryCapability(IntPtr stream, out uint capability);
        [PreserveSig] int Initialize(IntPtr stream, int cacheOptions);
        [PreserveSig] int GetContainerFormat(out Guid containerFormat);
        [PreserveSig] int GetDecoderInfo(out IntPtr info);
        [PreserveSig] int CopyPalette(IntPtr palette);
        [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
        [PreserveSig] int GetPreview(out IntPtr source);
        [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
        [PreserveSig] int GetThumbnail(out IntPtr thumbnail);
        [PreserveSig] int GetFrameCount(out uint count);

        /// <summary>
        /// Declared as IWICBitmapSource rather than IWICBitmapFrameDecode on purpose. The
        /// runtime QIs for whatever type is named here, and IWICBitmapSource is the base
        /// every frame implements — so the frame-decode IID, and the extra vtable slots that
        /// come with it, never have to be got right for this tool to work.
        /// </summary>
        [PreserveSig] int GetFrame(uint index, out IWICBitmapSource frame);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICBitmapSource
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid format);
        [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
        [PreserveSig] int CopyPalette(IntPtr palette);
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
    }

    [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICBitmapScaler
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid format);
        [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
        [PreserveSig] int CopyPalette(IntPtr palette);
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);

        /// <summary>
        /// Initialising a scaler directly on the frame is what lets WIC take the decoder's
        /// IWICBitmapSourceTransform path — for JPEG that is a DCT-domain 1/2, 1/4 or 1/8
        /// decode, which never materialises the full-size image at all.
        /// </summary>
        [PreserveSig] int Initialize(IWICBitmapSource source, uint width, uint height, int mode);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWICFormatConverter
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid format);
        [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
        [PreserveSig] int CopyPalette(IntPtr palette);
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);

        [PreserveSig] int Initialize(IWICBitmapSource source, ref Guid destFormat, int dither,
                                     IntPtr palette, double alphaThreshold, int paletteType);
        [PreserveSig] int CanConvert(ref Guid src, ref Guid dst, out bool canConvert);
    }

    private static readonly Lazy<IWICImagingFactory> FactoryInstance = new(() =>
    {
        var type = Type.GetTypeFromCLSID(CLSID_WICImagingFactory)
            ?? throw new InvalidOperationException("WIC is not available on this system.");
        return (IWICImagingFactory)Activator.CreateInstance(type)!;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>WIC's factory is free-threaded, so one instance serves every decode thread.</summary>
    public static IWICImagingFactory Factory => FactoryInstance.Value;
}

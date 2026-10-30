using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ocean.Core.Media;

/// <summary>
/// Thumbnails and media properties through the Windows Shell (the same providers Explorer uses).
/// Lightweight: no bundled codecs, uses whatever thumbnail/property handlers the OS has.
/// </summary>
public static class ShellMedia
{
    public readonly record struct Probe(long? DurationMs, int? Width, int? Height);

    /// <summary>Returns a JPEG thumbnail or null if the shell has no provider for this file.</summary>
    public static byte[]? Thumbnail(string path, int width = 320, int height = 180, int quality = 78)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj);
            if (obj is not IShellItemImageFactory factory) return null;
            var flags = SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK;
            var hr = factory.GetImage(new SIZE { cx = width, cy = height }, flags, out hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var scale = Math.Min((double)width / source.PixelWidth, (double)height / source.PixelHeight);
            if (Math.Abs(scale - 1) > 0.01)
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            if (source.Format.BitsPerPixel == 32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);

            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
        }
    }

    public static Probe ProbeFile(string path)
    {
        try
        {
            var iid = typeof(IShellItem2).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj);
            if (obj is not IShellItem2 item) return default;
            long? duration = null; int? w = null; int? h = null;
            var kDur = PKEY_Media_Duration;
            if (item.GetUInt64(ref kDur, out var d) == 0 && d > 0) duration = (long)(d / 10_000UL);
            var kW = PKEY_Video_FrameWidth;
            if (item.GetUInt32(ref kW, out var fw) == 0 && fw > 0) w = (int)fw;
            var kH = PKEY_Video_FrameHeight;
            if (item.GetUInt32(ref kH, out var fh) == 0 && fh > 0) h = (int)fh;
            return new Probe(duration, w, h);
        }
        catch
        {
            return default;
        }
    }

    // --- interop -----------------------------------------------------------------

    private const int SIIGBF_THUMBNAILONLY = 0x8;
    private const int SIIGBF_BIGGERSIZEOK = 0x1;

    private static PROPERTYKEY PKEY_Media_Duration = new() { fmtid = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), pid = 3 };
    private static PROPERTYKEY PKEY_Video_FrameWidth = new() { fmtid = new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), pid = 3 };
    private static PROPERTYKEY PKEY_Video_FrameHeight = new() { fmtid = new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), pid = 4 };

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
        // IShellItem2
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out System.Runtime.InteropServices.ComTypes.FILETIME pft);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr ppsz);
        [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
        [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
        [PreserveSig] int GetBool(ref PROPERTYKEY key, out int pf);
    }
}

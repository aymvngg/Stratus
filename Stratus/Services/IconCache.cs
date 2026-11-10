using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Stratus.Services;

/// <summary>Extracts executable icons as raw BGRA pixels without loading System.Drawing or
/// Windows Forms. Extraction runs on any thread; CreateImage must run on the UI thread.</summary>
internal static class IconCache
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors_1;
        public uint bmiColors_2;
        public uint bmiColors_3;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint startScan, uint scanLines, byte[]? bits, ref BITMAPINFO bmi, uint usage);

    /// <summary>Reads the large icon of an executable as straight-alpha BGRA pixels.
    /// Returns null when the file has no icon or any GDI step fails.</summary>
    public static (byte[] Pixels, int Width, int Height)? ExtractIconPixels(string path)
    {
        var info = new SHFILEINFOW();
        uint size = (uint)Marshal.SizeOf<SHFILEINFOW>();
        IntPtr shResult = SHGetFileInfoW(path, FileAttributeNormal, ref info, size, ShgfiIcon | ShgfiLargeIcon);
        if (shResult == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            if (!GetIconInfo(info.hIcon, out ICONINFO iconInfo))
                return null;

            IntPtr hdc = IntPtr.Zero;
            try
            {
                if (iconInfo.hbmColor == IntPtr.Zero)
                    return null;

                var bitmap = new BITMAP();
                if (GetObjectW(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), ref bitmap) == 0)
                    return null;

                int width = bitmap.bmWidth;
                int height = Math.Abs(bitmap.bmHeight);
                if (width <= 0 || height <= 0)
                    return null;

                var pixels = new byte[width * height * 4];
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = width;
                // Negative height: top-down rows, same order the bitmap writer expects.
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0; // BI_RGB

                hdc = CreateCompatibleDC(IntPtr.Zero);
                int lines = GetDIBits(hdc, iconInfo.hbmColor, 0, (uint)height, pixels, ref bmi, 0);
                if (lines != height)
                    return null;

                // Older icons carry no alpha channel; treat every pixel as opaque then.
                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                if (!hasAlpha)
                {
                    for (int i = 3; i < pixels.Length; i += 4)
                        pixels[i] = 255;
                }

                return (pixels, width, height);
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                    DeleteDC(hdc);
                if (iconInfo.hbmColor != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmMask);
            }
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Builds the WinUI image from extracted pixels. UI thread only. WinUI 3 has no
    /// SoftwareBitmapSource, so the pixels are wrapped in an in-memory 32-bit BMP with an
    /// alpha mask. Icon alpha is straight; the renderer expects premultiplied, so channels
    /// are scaled here.</summary>
    public static async Task<ImageSource?> CreateImageAsync((byte[] Pixels, int Width, int Height)? extracted)
    {
        if (extracted is not { } data)
            return null;

        var pixels = data.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255 || alpha == 0)
                continue;

            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }

        const int headerSize = 122; // BITMAPFILEHEADER (14) + BITMAPV4HEADER (108)
        var pixelSize = pixels.Length;
        var file = new byte[headerSize + pixelSize];

        // BITMAPFILEHEADER
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        WriteLE32(file, 2, (uint)(headerSize + pixelSize));
        WriteLE32(file, 10, headerSize);

        // BITMAPV4HEADER: 32bpp, BI_BITFIELDS with an alpha mask. Positive height = bottom-up rows.
        WriteLE32(file, 14, 108);
        WriteLE32(file, 18, (uint)data.Width);
        WriteLE32(file, 22, (uint)data.Height);
        WriteLE16(file, 26, 1);
        WriteLE16(file, 28, 32);
        WriteLE32(file, 30, 3); // BI_BITFIELDS
        WriteLE32(file, 34, (uint)pixelSize);
        WriteLE32(file, 54, 0x00FF0000); // red mask
        WriteLE32(file, 58, 0x0000FF00); // green mask
        WriteLE32(file, 62, 0x000000FF); // blue mask
        WriteLE32(file, 66, 0xFF000000); // alpha mask
        WriteLE32(file, 70, 0x57696E20); // LCS_WINDOWS_COLOR_SPACE

        for (var y = 0; y < data.Height; y++)
        {
            var sourceRow = (data.Height - 1 - y) * data.Width * 4;
            Array.Copy(pixels, sourceRow, file, headerSize + y * data.Width * 4, data.Width * 4);
        }

        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(file);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private static void WriteLE32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteLE16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}

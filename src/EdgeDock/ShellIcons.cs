using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace EdgeDock;

/// <summary>Raw icon pixels (premultiplied BGRA, top-down), safe to pass between threads.</summary>
internal sealed record IconPixels(int Width, int Height, byte[] Bgra);

/// <summary>
/// Gets the icon Explorer shows for a file or folder. Extraction can be slow (uncached
/// icons, shell extensions, network or removable drives), so it runs on a dedicated
/// background thread; only the final image is built on the UI thread. That thread is
/// single-threaded COM (STA) because some icon handlers fail on thread-pool (MTA) threads.
/// </summary>
internal static class ShellIcons
{
    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const int IconOnly = 0x4;
    private const int BiggerSizeOk = 0x1;
    private const int CacheLimit = 128;
    private static readonly ConcurrentDictionary<string, IconPixels?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        var thread = new Thread(() => { foreach (var job in Work.GetConsumingEnumerable()) job(); })
        {
            IsBackground = true,
            Name = "EdgeDock icon loader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    /// <summary>
    /// Call from the UI thread; extraction happens on the icon thread and the image is built
    /// when it returns. Null when Windows has no icon for the path.
    /// </summary>
    public static async Task<WriteableBitmap?> LoadAsync(string path, int size)
    {
        var key = $"{size}|{path}";
        if (!Cache.TryGetValue(key, out var pixels))
        {
            pixels = await OnIconThread(() => Extract(path, size));
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[key] = pixels;
        }
        return pixels is null ? null : ToImage(pixels);
    }

    private static Task<T> OnIconThread<T>(Func<T> job)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Worker.Value;
        Work.Add(() =>
        {
            try { result.SetResult(job()); }
            catch (Exception exception) { result.SetException(exception); }
        });
        return result.Task;
    }

    /// <summary>Reads the icon's pixels. Never call on the UI thread.</summary>
    public static IconPixels? Extract(string path, int size)
    {
        IntPtr bitmap = IntPtr.Zero;
        object? item = null;
        try
        {
            var interfaceId = ImageFactoryId;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out item) != 0 || item is not IShellItemImageFactory factory)
                return null;
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, IconOnly | BiggerSizeOk, out bitmap) != 0 || bitmap == IntPtr.Zero)
                return null;
            return ReadPixels(bitmap);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Builds the XAML image. UI thread only.</summary>
    public static WriteableBitmap ToImage(IconPixels pixels)
    {
        var image = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = image.PixelBuffer.AsStream()) stream.Write(pixels.Bgra, 0, pixels.Bgra.Length);
        image.Invalidate();
        return image;
    }

    private static IconPixels? ReadPixels(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.Width <= 0 || info.Height == 0) return null;
        var width = info.Width;
        var height = Math.Abs(info.Height);
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down rows
            Planes = 1,
            BitCount = 32
        };
        var pixels = new byte[width * height * 4];
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }

        // Older icons carry no alpha at all (every byte zero); treat those as opaque.
        var hasAlpha = false;
        for (var index = 3; index < pixels.Length && !hasAlpha; index += 4) hasAlpha = pixels[index] != 0;
        if (!hasAlpha)
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;

        // The shell supplies straight alpha; XAML expects premultiplied BGRA.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            if (alpha == 255) continue;
            pixels[index] = (byte)(pixels[index] * alpha / 255);
            pixels[index + 1] = (byte)(pixels[index + 1] * alpha / 255);
            pixels[index + 2] = (byte)(pixels[index + 2] * alpha / 255);
        }
        return new IconPixels(width, height, pixels);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPerPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}

using System.Runtime.InteropServices;

namespace EdgeDock;

/// <summary>
/// Reads and sets a monitor's own backlight over DDC/CI (VCP code 0x10), the same channel as
/// its on-screen menu. Not every monitor or cable supports it; callers fall back to dimming
/// EdgeDock's window. DDC is slow (tens of milliseconds per call), so calls run off the UI
/// thread and one at a time.
/// </summary>
internal static class MonitorBrightness
{
    private const byte BrightnessCode = 0x10;
    private const uint DefaultToNearest = 2;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Brightness 0-100 of the monitor showing <paramref name="window"/>, or null if unsupported.</summary>
    public static Task<int?> GetAsync(IntPtr window) => RunAsync(window, monitor =>
        GetVCPFeatureAndVCPFeatureReply(monitor, BrightnessCode, IntPtr.Zero, out var current, out var maximum) && maximum > 0
            ? (int?)Math.Round(current * 100.0 / maximum)
            : null);

    public static async Task<bool> SetAsync(IntPtr window, int percent) =>
        await RunAsync(window, monitor =>
        {
            if (!GetVCPFeatureAndVCPFeatureReply(monitor, BrightnessCode, IntPtr.Zero, out _, out var maximum) || maximum == 0)
                return (int?)null;
            return SetVCPFeature(monitor, BrightnessCode, (uint)Math.Round(Math.Clamp(percent, 0, 100) * maximum / 100.0)) ? 1 : null;
        }) is not null;

    private static async Task<int?> RunAsync(IntPtr window, Func<IntPtr, int?> action)
    {
        var display = MonitorFromWindow(window, DefaultToNearest);
        if (display == IntPtr.Zero) return null;
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(display, out var count) || count == 0) return null;
                var monitors = new PhysicalMonitor[count];
                if (!GetPhysicalMonitorsFromHMONITOR(display, count, monitors)) return null;
                try { return action(monitors[0].Handle); }
                finally { DestroyPhysicalMonitors(count, monitors); }
            });
        }
        finally { Gate.Release(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}

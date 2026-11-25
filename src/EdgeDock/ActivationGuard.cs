using System.Runtime.InteropServices;

namespace EdgeDock;

/// <summary>
/// Lets taps and clicks reach chosen parts of the window without activating it, so the app
/// the user is typing in on another screen keeps keyboard focus. Windows asks a window
/// whether a click should activate it (WM_MOUSEACTIVATE for mouse, WM_POINTERACTIVATE for
/// touch and pen); answering "no" still delivers the input.
/// </summary>
internal sealed class ActivationGuard : IDisposable
{
    private const int WindowProcedureIndex = -4;
    private const uint MouseActivate = 0x0021;
    private const uint PointerActivate = 0x024B;
    private const nint MouseNoActivate = 3;
    private const nint PointerNoActivate = 3;

    private readonly IntPtr _window;
    private readonly Func<NativePoint, bool> _shouldSkipActivation;
    private readonly WindowProcedure _procedure;
    private IntPtr _previousProcedure;

    /// <param name="shouldSkipActivation">Receives the input position in physical screen pixels.</param>
    public ActivationGuard(IntPtr window, Func<NativePoint, bool> shouldSkipActivation)
    {
        _window = window;
        _shouldSkipActivation = shouldSkipActivation;
        _procedure = Procedure; // Keep the delegate alive for as long as Windows can call it.
        _previousProcedure = SetWindowLongPtr(window, WindowProcedureIndex, Marshal.GetFunctionPointerForDelegate(_procedure));
    }

    private IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == MouseActivate && GetCursorPos(out var cursor) && _shouldSkipActivation(cursor))
                return MouseNoActivate;
            if (message == PointerActivate &&
                GetPointerInfo((uint)(wParam.ToInt64() & 0xFFFF), out var pointer) &&
                _shouldSkipActivation(pointer.PixelLocation))
                return PointerNoActivate;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            // Never let a layout query break window activation; fall through to the default.
        }
        return CallWindowProc(_previousProcedure, window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_previousProcedure == IntPtr.Zero) return;
        SetWindowLongPtr(_window, WindowProcedureIndex, _previousProcedure);
        _previousProcedure = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public int PointerType;
        public uint PointerId;
        public uint FrameId;
        public int PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr TargetWindow;
        public NativePoint PixelLocation;
        public NativePoint HimetricLocation;
        public NativePoint PixelLocationRaw;
        public NativePoint HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetPointerInfo(uint pointerId, out PointerInfo info);
}

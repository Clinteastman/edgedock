using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace EdgeDock;

/// <summary>One reading of PC activity. Null means that measurement is unavailable on this PC.</summary>
internal sealed record SystemStatsSnapshot(
    double? CpuPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    double? GpuPercent,
    double? GpuMemoryBytes,
    double? DiskBusyPercent,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond,
    double? NetworkDownBytesPerSecond,
    double? NetworkUpBytesPerSecond);

/// <summary>
/// Reads CPU, memory, GPU, disk and network activity from Windows itself: system times,
/// memory status, performance counters and interface statistics. No admin rights, drivers
/// or extra software. Not thread-safe; sample from one thread at a time.
/// </summary>
internal sealed class SystemStatsService : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private const uint ValidData = 0;
    private const uint NewData = 1;

    private IntPtr _query;
    private IntPtr _gpuEngines;
    private IntPtr _gpuMemory;
    private IntPtr _diskIdle;
    private IntPtr _diskRead;
    private IntPtr _diskWrite;
    private (ulong Idle, ulong Kernel, ulong User)? _lastTimes;
    private (Dictionary<string, (long Down, long Up)> Adapters, DateTime At)? _lastNetwork;

    public SystemStatsService()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }
        // English names work on every Windows display language.
        _gpuEngines = AddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _gpuMemory = AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _diskIdle = AddCounter(@"\PhysicalDisk(_Total)\% Idle Time");
        _diskRead = AddCounter(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = AddCounter(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        // Rate counters need a first reading before they report a value.
        PdhCollectQueryData(_query);
    }

    /// <summary>
    /// Starts fresh baselines. After a gap (widget hidden), the next reading would otherwise
    /// average the whole gap instead of the last second.
    /// </summary>
    public void Prime()
    {
        _lastTimes = null;
        _lastNetwork = null;
        CpuPercent();
        NetworkRates();
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    public SystemStatsSnapshot Sample()
    {
        var counters = _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var hasMemory = GlobalMemoryStatusEx(ref memory);
        var diskIdle = counters ? SingleValue(_diskIdle) : null;
        var (down, up) = NetworkRates();

        return new SystemStatsSnapshot(
            CpuPercent(),
            hasMemory ? memory.TotalPhysical - memory.AvailablePhysical : 0,
            hasMemory ? memory.TotalPhysical : 0,
            counters ? StatsMath.GpuPercent(ArrayValues(_gpuEngines)) : null,
            counters ? BusiestOrNull(ArrayValues(_gpuMemory)) : null,
            diskIdle is { } idle ? Math.Clamp(100 - idle, 0, 100) : null,
            counters ? SingleValue(_diskRead) : null,
            counters ? SingleValue(_diskWrite) : null,
            down,
            up);
    }

    private double? CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var current = (idle, kernel, user);
        var previous = _lastTimes;
        _lastTimes = current;
        return previous is { } last
            ? StatsMath.CpuPercent(last.Idle, last.Kernel, last.User, current.idle, current.kernel, current.user)
            : null;
    }

    private (double? Down, double? Up) NetworkRates()
    {
        var adapters = new Dictionary<string, (long Down, long Up)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var statistics = adapter.GetIPStatistics();
                adapters[adapter.Id] = (statistics.BytesReceived, statistics.BytesSent);
            }
        }
        catch (NetworkInformationException)
        {
            _lastNetwork = null;
            return (null, null);
        }

        var now = DateTime.UtcNow;
        var previous = _lastNetwork;
        _lastNetwork = (adapters, now);
        if (previous is not { } last) return (null, null);
        var (down, up) = StatsMath.NetworkRates(last.Adapters, adapters, (now - last.At).TotalSeconds);
        return (down, up);
    }

    private IntPtr AddCounter(string path) =>
        PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    /// <summary>
    /// One instance per graphics adapter (for example integrated and discrete). Show the
    /// adapter using the most dedicated memory rather than adding unrelated adapters together.
    /// </summary>
    private static double? BusiestOrNull(IReadOnlyList<(string Instance, double Value)> values) =>
        values.Count == 0 ? null : values.Max(item => item.Value);

    // PDH documents both "valid" and "new data" as successful readings.
    private static bool IsValid(uint status) => status is ValidData or NewData;

    private static double? SingleValue(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap, out _, out var value) == 0 && IsValid(value.Status)
            ? value.Value
            : null;
    }

    /// <summary>Reads every instance of a wildcard counter; instances come and go with processes.</summary>
    private static IReadOnlyList<(string Instance, double Value)> ArrayValues(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return [];
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap, ref size, out _, IntPtr.Zero);
        if (status != MoreData || size == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FormatDouble | FormatNoCap, ref size, out var count, buffer) != 0) return [];
            var items = new List<(string, double)>((int)count);
            var itemSize = Marshal.SizeOf<CounterItem>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + index * itemSize);
                if (!IsValid(item.Value.Status)) continue;
                items.Add((Marshal.PtrToStringUni(item.Name) ?? string.Empty, item.Value.Value));
            }
            return items;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

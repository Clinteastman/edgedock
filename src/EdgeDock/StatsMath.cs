namespace EdgeDock;

/// <summary>Arithmetic behind the PC stats widget, free of Windows and WinUI types for testing.</summary>
internal static class StatsMath
{
    /// <summary>
    /// CPU busy percentage from two GetSystemTimes readings. Kernel time includes idle time,
    /// so busy = (kernel + user - idle) / (kernel + user) over the interval.
    /// </summary>
    public static double? CpuPercent(ulong idle0, ulong kernel0, ulong user0, ulong idle1, ulong kernel1, ulong user1)
    {
        if (idle1 < idle0 || kernel1 < kernel0 || user1 < user0) return null;
        var idle = idle1 - idle0;
        var total = (kernel1 - kernel0) + (user1 - user0);
        if (total == 0) return null;
        return Math.Clamp((total - Math.Min(idle, total)) * 100.0 / total, 0, 100);
    }

    /// <summary>Bytes per second between two counter readings; a reset counter reads as zero.</summary>
    public static double Rate(long previous, long current, double seconds) =>
        seconds <= 0 || current < previous ? 0 : (current - previous) / seconds;

    /// <summary>
    /// GPU load the way Task Manager shows it: add up every process's share of each physical
    /// engine, then report the busiest engine. Two engines of the same type stay separate.
    /// Instance names look like <c>pid_1234_luid_0x0_0x0_phys_0_eng_0_engtype_3D</c>.
    /// </summary>
    public static double? GpuPercent(IEnumerable<(string Instance, double Value)> engines)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in engines)
        {
            if (!double.IsFinite(value) || value < 0) continue;
            var engine = Segment(instance, "luid_", "_engtype");
            if (engine is null) continue;
            totals[engine] = totals.GetValueOrDefault(engine) + value;
        }
        return totals.Count == 0 ? null : Math.Clamp(totals.Values.Max(), 0, 100);
    }

    private static string? Segment(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (from < 0) return null;
        from += start.Length;
        var to = end is null ? text.Length : text.IndexOf(end, from, StringComparison.OrdinalIgnoreCase);
        return to <= from ? null : text[from..to];
    }

    public static string FormatRate(double bytesPerSecond) => FormatBytes(bytesPerSecond) + "/s";

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} B" : $"{value:0.0} {units[unit]}";
    }

    /// <summary>
    /// Maps samples to sparkline points, oldest on the left, newest at the right edge.
    /// Values are scaled against <paramref name="maximum"/>; zero or less scales against the
    /// largest sample so quiet traffic is still visible.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Sparkline(IReadOnlyList<double> samples, int capacity, double width, double height, double maximum)
    {
        if (samples.Count == 0 || width <= 0 || height <= 0 || capacity < 2) return [];
        var top = maximum > 0 ? maximum : Math.Max(samples.Max(), 1);
        var step = width / (capacity - 1);
        var offset = capacity - samples.Count;
        var points = new (double, double)[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            var ratio = Math.Clamp(samples[index] / top, 0, 1);
            points[index] = ((offset + index) * step, height - ratio * height);
        }
        return points;
    }
}

/// <summary>A fixed-size window of recent samples, oldest first.</summary>
internal sealed class SampleHistory(int capacity)
{
    private readonly Queue<double> _values = new();

    public int Capacity => capacity;

    public IReadOnlyList<double> Values => _values.ToArray();

    public void Add(double value)
    {
        _values.Enqueue(double.IsFinite(value) ? value : 0);
        while (_values.Count > capacity) _values.Dequeue();
    }
}

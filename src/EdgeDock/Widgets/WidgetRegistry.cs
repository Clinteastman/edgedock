using Microsoft.UI.Xaml.Controls;
using EdgeDock.Controls;

namespace EdgeDock.Widgets;

public interface IWidgetDescriptor
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    UserControl Create();
}

public sealed record WidgetDescriptor(string Id, string DisplayName, string Description, Func<UserControl> Factory) : IWidgetDescriptor
{
    public UserControl Create() => Factory();
}

public sealed class WidgetRegistry
{
    private readonly Dictionary<string, IWidgetDescriptor> _items;
    public WidgetRegistry(IEnumerable<IWidgetDescriptor> items) => _items = items.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<IWidgetDescriptor> Items => _items.Values;
    public bool TryGet(string id, out IWidgetDescriptor? descriptor) => _items.TryGetValue(id, out descriptor);
    /// <param name="clockOptions">Reads the saved weather place and units when a clock is created.</param>
    internal static WidgetRegistry CreateBuiltIns(Func<ClockOptions> clockOptions) => new([
        new WidgetDescriptor("media", "Media", "Current Windows media and playback controls.", () => new MediaWidget()),
        new WidgetDescriptor("audio", "Audio", "Windows volume and audio output controls.", () => new AudioWidget()),
        new WidgetDescriptor("pc", "PC shortcuts", "Open selected Windows Settings pages.", () => new PcShortcutsWidget()),
        new WidgetDescriptor("stats", "PC activity", "Live processor, memory, graphics, disk and network use.", () => new PcStatsWidget()),
        new WidgetDescriptor("clock", "Clock and weather", "Large time and date, with weather for a place you choose.", () => new ClockWidget(clockOptions))
    ]);
}

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
    /// <param name="launcherItems">Reads the saved launcher tiles each time a launcher is created.</param>
    internal static WidgetRegistry CreateBuiltIns(Func<IReadOnlyList<LauncherItem>> launcherItems) => new([
        new WidgetDescriptor("media", "Media", "Current Windows media and playback controls.", () => new MediaWidget()),
        new WidgetDescriptor("audio", "Audio", "Windows volume and audio output controls.", () => new AudioWidget()),
        new WidgetDescriptor("launcher", "Launcher", "Tiles that open your chosen apps, files, folders and web pages.", () => new LauncherWidget(launcherItems)),
        new WidgetDescriptor("pc", "PC shortcuts", "Open selected Windows Settings pages.", () => new PcShortcutsWidget())
    ]);
}

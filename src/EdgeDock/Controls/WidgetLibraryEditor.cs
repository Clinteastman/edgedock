using EdgeDock.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EdgeDock.Controls;

internal sealed class WidgetLibraryEditor : UserControl
{
    private readonly StackPanel _slotsPanel = new() { Spacing = 16 };
    private readonly ComboBox _slotCount = new() { MinHeight = 52 };
    private readonly List<SlotEditor> _editors = [];
    private IReadOnlyList<WidgetSlotSettings> _source = [];
    private WidgetRegistry? _registry;

    public WidgetLibraryEditor()
    {
        AutomationProperties.SetName(_slotCount, "Number of widget slots");
        _slotCount.Items.Add("1"); _slotCount.Items.Add("2"); _slotCount.Items.Add("3");
        _slotCount.SelectionChanged += (_, _) => Rebuild();
        var root = new StackPanel { Spacing = 10 };
        _slotCount.Header = "Number of panels";
        root.Children.Add(_slotCount); root.Children.Add(_slotsPanel); Content = root;
    }

    internal void LoadSlots(IReadOnlyList<WidgetSlotSettings> slots, WidgetRegistry registry)
    {
        _registry = registry;
        _source = slots.Count == 0 ? [new WidgetSlotSettings(["media"], "media")] : slots.Take(3).ToArray();
        _editors.Clear();
        _slotsPanel.Children.Clear();
        _slotCount.SelectedIndex = _source.Count - 1;
        Rebuild();
    }

    internal IReadOnlyList<WidgetSlotSettings> GetSlots() => _editors.Select(x => x.Get()).ToArray();

    private void Rebuild()
    {
        if (_registry is null || _slotCount.SelectedIndex < 0) return;
        var previous = _editors.Select(x => x.Get()).ToArray();
        _editors.Clear(); _slotsPanel.Children.Clear();
        var count = _slotCount.SelectedIndex + 1;
        for (var index = 0; index < count; index++)
        {
            var state = index < previous.Length ? previous[index] : index < _source.Count ? _source[index] : new WidgetSlotSettings([], null);
            var editor = new SlotEditor(index + 1, state, _registry); _editors.Add(editor); _slotsPanel.Children.Add(editor.Root);
        }
    }

    private sealed class SlotEditor
    {
        private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly ComboBox _selected = new() { MinHeight = 52 };
        internal StackPanel Root { get; } = new() { Spacing = 6 };

        internal SlotEditor(int number, WidgetSlotSettings state, WidgetRegistry registry)
        {
            AutomationProperties.SetName(_selected, $"Selected widget page for slot {number}");
            Root.Children.Add(new TextBlock { Text = $"Slot {number}", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var descriptor in registry.Items.OrderBy(x => x.DisplayName)) AddChoice(descriptor.Id, descriptor.DisplayName, descriptor.Description, state);
            foreach (var unknown in state.EnabledWidgetIds.Where(id => !registry.TryGet(id, out _))) AddChoice(unknown, $"Unavailable: {unknown}", "Saved by a newer or missing widget. Keep or remove it.", state);
            Root.Children.Add(new TextBlock { Text = "Selected page" }); Root.Children.Add(_selected);
            RefreshSelected(state.SelectedWidgetId);
        }

        private void AddChoice(string id, string title, string description, WidgetSlotSettings state)
        {
            var box = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, IsChecked = state.EnabledWidgetIds.Contains(id, StringComparer.OrdinalIgnoreCase), MinHeight = 52, Tag = id };
            ToolTipService.SetToolTip(box, description);
            _names[id] = title;
            AutomationProperties.SetName(box, title);
            box.Checked += (_, _) => RefreshSelected((_selected.SelectedItem as ComboBoxItem)?.Tag as string);
            box.Unchecked += (_, _) => RefreshSelected((_selected.SelectedItem as ComboBoxItem)?.Tag as string);
            _choices[id] = box; Root.Children.Add(box);
        }

        private void RefreshSelected(string? preferred)
        {
            var enabled = _choices.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToArray();
            _selected.Items.Clear();
            foreach (var id in enabled) _selected.Items.Add(new ComboBoxItem { Content = _names[id], Tag = id });
            var chosen = enabled.FirstOrDefault(id => string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase)) ?? enabled.FirstOrDefault();
            _selected.SelectedItem = _selected.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == chosen);
            _selected.IsEnabled = enabled.Length > 1;
        }

        internal WidgetSlotSettings Get()
        {
            var enabled = _choices.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToArray();
            var selected = (_selected.SelectedItem as ComboBoxItem)?.Tag as string;
            return new WidgetSlotSettings(enabled, enabled.Contains(selected ?? "", StringComparer.OrdinalIgnoreCase) ? selected : enabled.FirstOrDefault());
        }
    }
}

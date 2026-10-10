using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EdgeDock.Controls;

/// <summary>
/// Settings rows for saved layouts: rename, choose a program that switches the layout on
/// while it runs, or remove. Layouts themselves are saved from the controls drawer.
/// </summary>
internal sealed class ProfileEditor : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 14 };
    private readonly TextBlock _empty = new()
    {
        Text = "No saved layouts yet. Arrange the dashboard, then use \"Save as layout\" in EdgeDock controls.",
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"]
    };
    private readonly List<Row> _editors = [];
    private IReadOnlyList<LayoutProfile> _source = [];

    public ProfileEditor()
    {
        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(_empty);
        root.Children.Add(_rows);
        Content = root;
    }

    internal void LoadProfiles(IReadOnlyList<LayoutProfile> profiles)
    {
        _source = profiles;
        _editors.Clear();
        _rows.Children.Clear();
        foreach (var profile in profiles) AddRow(profile);
        _empty.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The edited list; layouts are kept as saved, names and triggers come from the rows.</summary>
    internal bool TryGetProfiles(out IReadOnlyList<LayoutProfile> profiles, out string? error)
    {
        var result = new List<LayoutProfile>();
        foreach (var editor in _editors)
        {
            var original = _source.FirstOrDefault(profile => profile.Id == editor.Id);
            if (original is null) continue;
            var name = editor.Name.Text.Trim();
            if (name.Length == 0)
            {
                profiles = [];
                error = "Give each saved layout a name, or remove it.";
                editor.Name.Focus(FocusState.Programmatic);
                return false;
            }
            if (result.Any(profile => string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                profiles = [];
                error = $"Two saved layouts are called {name}. Give each a different name.";
                editor.Name.Focus(FocusState.Programmatic);
                return false;
            }
            var trigger = editor.WhenRunning.Text.Trim();
            var program = LayoutProfiles.NormalizeProcessName(trigger);
            if (trigger.Length > 0 && program is null)
            {
                profiles = [];
                error = $"For {name}, enter just the program's name as Task Manager shows it, such as cs2. No folders or options.";
                editor.WhenRunning.Focus(FocusState.Programmatic);
                return false;
            }
            result.Add(original with { Name = name, WhenRunning = program });
        }
        profiles = result;
        error = null;
        return true;
    }

    private void AddRow(LayoutProfile profile)
    {
        var editor = new Row(profile);
        editor.Remove.Click += (_, _) =>
        {
            _editors.Remove(editor);
            _rows.Children.Remove(editor.Root);
            _empty.Visibility = _editors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        _editors.Add(editor);
        _rows.Children.Add(editor.Root);
    }

    private sealed class Row
    {
        internal string Id { get; }
        internal StackPanel Root { get; } = new() { Spacing = 6 };
        internal TextBox Name { get; } = new() { Header = "Layout name", MinHeight = 52 };
        internal TextBox WhenRunning { get; } = new()
        {
            Header = "Use automatically while this program runs (optional)",
            PlaceholderText = "Program name, for example cs2",
            MinHeight = 52
        };
        internal Button Remove { get; } = new() { Content = "Remove layout", HorizontalAlignment = HorizontalAlignment.Left };

        internal Row(LayoutProfile profile)
        {
            Id = profile.Id;
            Name.Text = profile.Name;
            WhenRunning.Text = profile.WhenRunning ?? string.Empty;
            UpdateNames();
            Name.TextChanged += (_, _) => UpdateNames();
            Root.Children.Add(Name);
            Root.Children.Add(WhenRunning);
            Root.Children.Add(Remove);
        }

        private void UpdateNames()
        {
            var label = string.IsNullOrWhiteSpace(Name.Text) ? "this layout" : Name.Text.Trim();
            AutomationProperties.SetName(Name, "Layout name");
            AutomationProperties.SetName(WhenRunning, $"Program that switches on {label}");
            AutomationProperties.SetName(Remove, $"Remove {label}");
        }
    }
}

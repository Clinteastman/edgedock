using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EdgeDock.Controls;

internal sealed class WebCardEditor : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 14 };
    private readonly List<CardRow> _editors = [];

    internal WebCardEditor()
    {
        var add = new Button
        {
            Content = "Add web card",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(add, "Add web card");
        add.Click += (_, _) => AddRow(new WebCardSettings(Guid.NewGuid().ToString("N"), string.Empty, string.Empty));

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(_rows);
        root.Children.Add(add);
        Content = root;
    }

    internal void LoadCards(IReadOnlyList<WebCardSettings> cards)
    {
        _editors.Clear();
        _rows.Children.Clear();
        foreach (var card in cards) AddRow(card);
    }

    internal bool TryGetCards(out IReadOnlyList<WebCardSettings> cards, out string? error)
    {
        var result = new List<WebCardSettings>();
        foreach (var editor in _editors)
        {
            var name = editor.Name.Text.Trim();
            var address = editor.Address.Text.Trim();
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(address)) continue;
            if (string.IsNullOrEmpty(name))
            {
                cards = [];
                error = "Give each web card a short name.";
                editor.Name.Focus(FocusState.Programmatic);
                return false;
            }
            if (!SettingsStore.IsAllowedUrl(address, out var uri) || uri is null)
            {
                cards = [];
                error = $"Enter a complete http or https address for {name}.";
                editor.Address.Focus(FocusState.Programmatic);
                return false;
            }
            result.Add(new WebCardSettings(editor.Id, name, uri.AbsoluteUri));
        }

        cards = result;
        error = null;
        return true;
    }

    private void AddRow(WebCardSettings card)
    {
        var editor = new CardRow(card);
        editor.Remove.Click += (_, _) =>
        {
            _editors.Remove(editor);
            _rows.Children.Remove(editor.Root);
        };
        _editors.Add(editor);
        _rows.Children.Add(editor.Root);
    }

    private sealed class CardRow
    {
        internal string Id { get; }
        internal Grid Root { get; } = new() { ColumnSpacing = 10, RowSpacing = 6 };
        internal TextBox Name { get; } = new() { Header = "Name", MinHeight = 52 };
        internal TextBox Address { get; } = new() { Header = "Web address", MinHeight = 52 };
        internal Button Remove { get; } = new() { Content = "Remove", VerticalAlignment = VerticalAlignment.Bottom };

        internal CardRow(WebCardSettings card)
        {
            Id = string.IsNullOrWhiteSpace(card.Id) ? Guid.NewGuid().ToString("N") : card.Id;
            Name.Text = card.Name;
            Address.Text = card.Url;
            AutomationProperties.SetName(Name, "Web card name");
            AutomationProperties.SetName(Address, $"Web address for {card.Name}");
            AutomationProperties.SetName(Remove, $"Remove {card.Name} web card");

            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.Children.Add(Name);
            Grid.SetColumn(Address, 1);
            Root.Children.Add(Address);
            Grid.SetColumn(Remove, 2);
            Root.Children.Add(Remove);
        }
    }
}

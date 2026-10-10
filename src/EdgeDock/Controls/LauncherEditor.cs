using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace EdgeDock.Controls;

/// <summary>Settings rows for launcher tiles: a name and an app, file, folder or web address.</summary>
internal sealed class LauncherEditor : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 14 };
    private readonly List<ItemRow> _editors = [];
    private ItemRow? _invalidRow;
    private readonly Button _add = new() { Content = "Add launcher item", HorizontalAlignment = HorizontalAlignment.Left };

    internal LauncherEditor()
    {
        AutomationProperties.SetName(_add, "Add launcher item");
        _add.Click += (_, _) =>
        {
            AddRow(new LauncherItem(Guid.NewGuid().ToString("N"), string.Empty, string.Empty));
            UpdateAddButton();
        };
        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(_rows);
        root.Children.Add(_add);
        Content = root;
    }

    /// <summary>The window that owns the file pickers.</summary>
    internal IntPtr WindowHandle { get; set; }

    internal void LoadItems(IReadOnlyList<LauncherItem> items)
    {
        _editors.Clear();
        _rows.Children.Clear();
        foreach (var item in items) AddRow(item);
        UpdateAddButton();
    }

    /// <summary>
    /// Validates every row. Existence checks can wait on slow or disconnected drives, so they
    /// run on the thread pool; the Settings panel stays responsive meanwhile.
    /// </summary>
    internal async Task<(bool Valid, IReadOnlyList<LauncherItem> Items, string? Error)> TryGetItemsAsync()
    {
        var rows = _editors
            .Select(editor => (Editor: editor, Name: editor.Name.Text.Trim(), Target: editor.Target.Text.Trim().Trim('"')))
            .Where(row => row.Name.Length > 0 || row.Target.Length > 0)
            .ToArray();
        var kinds = await Task.Run(() => rows.Select(row => LauncherTargets.Classify(row.Target)).ToArray());
        var result = new List<LauncherItem>();
        for (var index = 0; index < rows.Length; index++)
        {
            var (editor, name, target) = rows[index];
            if (kinds[index] == LauncherTargetKind.Invalid)
            {
                // Focused by FocusInvalidRow once the caller has re-enabled the editor; a
                // disabled control cannot take focus.
                _invalidRow = editor;
                return (false, [], string.IsNullOrEmpty(target)
                    ? $"Choose what {(string.IsNullOrEmpty(name) ? "the new launcher item" : name)} should open."
                    : $"{target} is not an existing file or folder, or an http or https address. Use Browse to pick one.");
            }
            result.Add(new LauncherItem(editor.Id, string.IsNullOrEmpty(name) ? LauncherTargets.DefaultName(target, kinds[index]) : name, target));
        }
        return (true, result, null);
    }

    /// <summary>Moves focus to, and scrolls to, the row the last check rejected.</summary>
    internal void FocusInvalidRow()
    {
        if (_invalidRow is not { } row || !_editors.Contains(row)) return;
        _invalidRow = null;
        row.Target.Focus(FocusState.Programmatic);
        row.Target.StartBringIntoView();
    }

    private void UpdateAddButton() => _add.IsEnabled = _editors.Count < SettingsStore.MaximumLaunchers;

    private void AddRow(LauncherItem item)
    {
        var editor = new ItemRow(item);
        editor.Remove.Click += (_, _) =>
        {
            _editors.Remove(editor);
            _rows.Children.Remove(editor.Root);
            UpdateAddButton();
        };
        editor.BrowseFile.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFileAsync() is { } file) editor.Use(file.Path, LauncherTargetKind.File);
        };
        editor.BrowseFolder.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFolderAsync() is { } folder) editor.Use(folder.Path, LauncherTargetKind.Folder);
        };
        _editors.Add(editor);
        _rows.Children.Add(editor.Root);
    }

    private sealed class ItemRow
    {
        internal string Id { get; }
        internal StackPanel Root { get; } = new() { Spacing = 6 };
        internal TextBox Name { get; } = new() { Header = "Name", MinHeight = 52 };
        internal TextBox Target { get; } = new() { Header = "App, file, folder or web address", MinHeight = 52 };
        internal Button BrowseFile { get; } = new() { Content = "Browse file" };
        internal Button BrowseFolder { get; } = new() { Content = "Browse folder" };
        internal Button Remove { get; } = new() { Content = "Remove" };

        internal ItemRow(LauncherItem item)
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
            Name.Text = item.Name;
            Target.Text = item.Target;
            AutomationProperties.SetName(Name, "Launcher item name");
            UpdateNames();
            Name.TextChanged += (_, _) => UpdateNames();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(BrowseFile);
            buttons.Children.Add(BrowseFolder);
            buttons.Children.Add(Remove);
            Root.Children.Add(Name);
            Root.Children.Add(Target);
            Root.Children.Add(buttons);
        }

        /// <summary>Screen readers name each row's controls after the item, as it is typed.</summary>
        private void UpdateNames()
        {
            var label = string.IsNullOrWhiteSpace(Name.Text) ? "the new launcher item" : Name.Text.Trim();
            AutomationProperties.SetName(Target, $"What {label} opens");
            AutomationProperties.SetName(BrowseFile, $"Browse for a file for {label}");
            AutomationProperties.SetName(BrowseFolder, $"Browse for a folder for {label}");
            AutomationProperties.SetName(Remove, $"Remove {label} from the launcher");
        }

        internal void Use(string path, LauncherTargetKind kind)
        {
            Target.Text = path;
            if (string.IsNullOrWhiteSpace(Name.Text)) Name.Text = LauncherTargets.DefaultName(path, kind);
        }
    }
}

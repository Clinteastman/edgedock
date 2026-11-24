using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EdgeDock.Controls;

/// <summary>
/// The Waveform widget's choices in Settings: sound source, view, look, colour cycle, speed,
/// auto level, and which views to cycle through and how often.
/// </summary>
internal sealed class WaveformEditor : StackPanel
{
    private readonly ComboBox _source = Picker("Sound", "Waveform sound source");
    private readonly ComboBox _view = Picker("View", "Waveform view");
    private readonly ComboBox _look = Picker("Look", "Waveform look");
    private readonly ComboBox _colourCycle = Picker("Colour cycle", "Waveform colour cycle");
    private readonly ComboBox _cycle = Picker("Change view automatically", "Change the waveform view automatically");
    private readonly Slider _speed = new()
    {
        Header = "Speed",
        Minimum = WaveformSettings.MinimumSpeed,
        Maximum = WaveformSettings.MaximumSpeed,
        StepFrequency = 10
    };
    private readonly ToggleSwitch _autoLevel = new() { Header = "Auto level" };
    private readonly Dictionary<WaveformView, CheckBox> _cycleViews = [];

    public WaveformEditor()
    {
        Spacing = 16;
        AutomationProperties.SetName(_speed, "Waveform speed");
        AutomationProperties.SetName(_autoLevel, "Waveform auto level");
        foreach (var (group, views) in WaveformNames.Groups)
            foreach (var view in views)
                _view.Items.Add(new ComboBoxItem { Content = $"{group}: {WaveformNames.View(view)}", Tag = view });
        foreach (var look in Enum.GetValues<WaveformLook>())
            _look.Items.Add(new ComboBoxItem { Content = WaveformNames.Look(look), Tag = look });
        foreach (var cycle in Enum.GetValues<WaveformColourCycle>())
            _colourCycle.Items.Add(new ComboBoxItem { Content = WaveformNames.ColourCycle(cycle), Tag = cycle });
        foreach (var seconds in WaveformSettings.CycleChoices)
            _cycle.Items.Add(new ComboBoxItem { Content = WaveformNames.Cycle(seconds), Tag = seconds });

        Children.Add(_source);
        Children.Add(_view);
        Children.Add(_look);
        Children.Add(_colourCycle);
        Children.Add(Note("Tap the waveform itself to change view or look on the screen; those choices are saved too."));
        Children.Add(_speed);
        Children.Add(Note("Speed applies to the views that show time: Scrolling wave, Centre out, Spectrogram and Ridges."));
        Children.Add(_autoLevel);
        Children.Add(Note("Auto level scales the picture to the recent loudest sound, so quiet music still fills the panel."));
        Children.Add(_cycle);
        Children.Add(Note("Views to cycle through (none ticked means all of them):"));
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var index = 0;
        foreach (var (_, views) in WaveformNames.Groups)
        {
            foreach (var view in views)
            {
                var box = new CheckBox { Content = WaveformNames.View(view), MinHeight = 44 };
                AutomationProperties.SetName(box, "Include " + WaveformNames.View(view) + " in the cycle");
                Grid.SetColumn(box, index % 2);
                Grid.SetRow(box, index / 2);
                if (index % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.Children.Add(box);
                _cycleViews[view] = box;
                index++;
            }
        }
        Children.Add(grid);
    }

    /// <summary>Fills the form, listing connected inputs. A saved input that is unplugged stays listed.</summary>
    public void Load(WaveformSettings settings)
    {
        settings = WaveformSettings.Normalize(settings);
        _source.Items.Clear();
        _source.Items.Add(new ComboBoxItem { Content = "What the PC is playing", Tag = null });
        var inputs = WaveformCapture.ListInputs();
        foreach (var input in inputs)
            _source.Items.Add(new ComboBoxItem { Content = "Input: " + input.Name, Tag = input.Id });
        if (settings.InputDeviceId is { } saved && !inputs.Any(input => string.Equals(input.Id, saved, StringComparison.OrdinalIgnoreCase)))
            _source.Items.Add(new ComboBoxItem { Content = "Saved input (not connected)", Tag = saved });
        Select(_source, item => string.Equals(item as string, settings.InputDeviceId, StringComparison.OrdinalIgnoreCase));
        Select(_view, item => item is WaveformView view && view == settings.View);
        Select(_look, item => item is WaveformLook look && look == settings.Look);
        Select(_colourCycle, item => item is WaveformColourCycle cycle && cycle == settings.ColourCycle);
        Select(_cycle, item => item is int seconds && seconds == settings.CycleSeconds);
        _speed.Value = settings.Speed;
        _autoLevel.IsOn = settings.AutoLevel;
        foreach (var (view, box) in _cycleViews) box.IsChecked = settings.CycleViews?.Contains(view) == true;
    }

    public WaveformSettings Read() => WaveformSettings.Normalize(new WaveformSettings(
        (_source.SelectedItem as ComboBoxItem)?.Tag as string,
        (_view.SelectedItem as ComboBoxItem)?.Tag is WaveformView view ? view : WaveformSettings.Default.View,
        (_look.SelectedItem as ComboBoxItem)?.Tag is WaveformLook look ? look : WaveformSettings.Default.Look,
        _speed.Value,
        _autoLevel.IsOn,
        (_colourCycle.SelectedItem as ComboBoxItem)?.Tag is WaveformColourCycle cycle ? cycle : WaveformColourCycle.Off,
        _cycleViews.Where(entry => entry.Value.IsChecked == true).Select(entry => entry.Key).ToArray(),
        (_cycle.SelectedItem as ComboBoxItem)?.Tag is int seconds ? seconds : 0));

    private static ComboBox Picker(string header, string name)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 52 };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"]
    };

    private static void Select(ComboBox box, Func<object?, bool> matches)
    {
        var index = 0;
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is ComboBoxItem item && matches(item.Tag)) index = i;
        box.SelectedIndex = index;
    }
}

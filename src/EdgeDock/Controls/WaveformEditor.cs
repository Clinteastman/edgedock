using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EdgeDock.Controls;

/// <summary>The Waveform widget's choices in Settings: sound source, view, look, speed, auto level.</summary>
internal sealed class WaveformEditor : StackPanel
{
    private readonly ComboBox _source = new() { Header = "Sound", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 52 };
    private readonly ComboBox _view = new() { Header = "View", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 52 };
    private readonly ComboBox _look = new() { Header = "Look", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 52 };
    private readonly Slider _speed = new()
    {
        Header = "Speed",
        Minimum = WaveformSettings.MinimumSpeed,
        Maximum = WaveformSettings.MaximumSpeed,
        StepFrequency = 10
    };
    private readonly ToggleSwitch _autoLevel = new() { Header = "Auto level" };

    public WaveformEditor()
    {
        Spacing = 16;
        AutomationProperties.SetName(_source, "Waveform sound source");
        AutomationProperties.SetName(_view, "Waveform view");
        AutomationProperties.SetName(_look, "Waveform look");
        AutomationProperties.SetName(_speed, "Waveform speed");
        AutomationProperties.SetName(_autoLevel, "Waveform auto level");
        foreach (var (view, name) in ViewNames)
            _view.Items.Add(new ComboBoxItem { Content = name, Tag = view });
        foreach (var look in Enum.GetValues<WaveformLook>())
            _look.Items.Add(new ComboBoxItem { Content = look.ToString(), Tag = look });
        Children.Add(_source);
        Children.Add(_view);
        Children.Add(_look);
        Children.Add(_speed);
        Children.Add(new TextBlock
        {
            Text = "Speed applies to the scrolling views: Scrolling wave, Centre out and Spectrogram.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"]
        });
        Children.Add(new TextBlock
        {
            Text = "Auto level scales the picture to the recent loudest sound, so quiet music still fills the panel.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"]
        });
        Children.Add(_autoLevel);
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
        _source.SelectedIndex = Math.Max(0, IndexOf(_source, item => string.Equals(item as string, settings.InputDeviceId, StringComparison.OrdinalIgnoreCase)));
        _view.SelectedIndex = Math.Max(0, IndexOf(_view, item => item is WaveformView view && view == settings.View));
        _look.SelectedIndex = Math.Max(0, IndexOf(_look, item => item is WaveformLook look && look == settings.Look));
        _speed.Value = settings.Speed;
        _autoLevel.IsOn = settings.AutoLevel;
    }

    public WaveformSettings Read() => WaveformSettings.Normalize(new WaveformSettings(
        (_source.SelectedItem as ComboBoxItem)?.Tag as string,
        (_view.SelectedItem as ComboBoxItem)?.Tag is WaveformView view ? view : WaveformSettings.Default.View,
        (_look.SelectedItem as ComboBoxItem)?.Tag is WaveformLook look ? look : WaveformSettings.Default.Look,
        _speed.Value,
        _autoLevel.IsOn));

    private static readonly (WaveformView View, string Name)[] ViewNames =
    [
        (WaveformView.Scroll, "Scrolling wave: new sound on the right"),
        (WaveformView.CentreOut, "Centre out: new sound in the middle"),
        (WaveformView.Spectrogram, "Spectrogram: frequencies over time"),
        (WaveformView.SpectrumBars, "Spectrum bars"),
        (WaveformView.MirrorSpectrum, "Mirror spectrum: bass in the middle"),
        (WaveformView.SpectrumLine, "Spectrum line"),
        (WaveformView.Oscilloscope, "Oscilloscope: the live wave shape"),
        (WaveformView.Pulse, "Pulse: live loudness bars")
    ];

    private static int IndexOf(ComboBox box, Func<object?, bool> matches)
    {
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is ComboBoxItem item && matches(item.Tag)) return i;
        return -1;
    }
}

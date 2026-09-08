using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace EdgeDock.Controls;

public sealed partial class AudioWidget : UserControl
{
    private DispatcherTimer? _refreshTimer;
    private SystemAudioService? _audio;
    private bool _updating;
    private bool _isManipulatingVolume;

    public AudioWidget()
    {
        InitializeComponent();
        Loaded += AudioWidget_Loaded;
        Unloaded += AudioWidget_Unloaded;
    }

    private void AudioWidget_Loaded(object sender, RoutedEventArgs args)
    {
        _audio ??= new SystemAudioService();
        _refreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        Refresh();
    }

    private void AudioWidget_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
        }

        _audio?.Dispose();
        _audio = null;
    }

    private void RefreshTimer_Tick(object? sender, object args) => Refresh();

    private void Refresh()
    {
        if (!_isManipulatingVolume && _audio is not null)
        {
            ApplySnapshot(_audio.GetSnapshot());
        }
    }

    private void VolumeSlider_PointerPressed(object sender, PointerRoutedEventArgs args) => _isManipulatingVolume = true;

    private void VolumeSlider_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _isManipulatingVolume = false;
        Refresh();
    }

    private void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        _isManipulatingVolume = false;
        Refresh();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_updating && _audio is not null)
        {
            ApplySnapshot(_audio.SetVolume(args.NewValue));
        }
    }

    private void MuteToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_updating && _audio is not null)
        {
            ApplySnapshot(_audio.SetMute(MuteToggle.IsOn));
        }
    }

    private void ApplySnapshot(SystemAudioSnapshot snapshot)
    {
        _updating = true;
        EndpointName.Text = snapshot.EndpointName;
        VolumeSlider.Value = snapshot.VolumePercent;
        VolumeSlider.IsEnabled = snapshot.IsAvailable;
        VolumeText.Text = snapshot.IsAvailable ? $"{snapshot.VolumePercent:0}%" : "--%";
        MuteToggle.IsOn = snapshot.IsMuted;
        MuteToggle.IsEnabled = snapshot.IsAvailable;
        AudioStatus.Text = snapshot.StatusMessage ?? string.Empty;
        AudioStatus.Visibility = string.IsNullOrWhiteSpace(snapshot.StatusMessage) ? Visibility.Collapsed : Visibility.Visible;
        _updating = false;
    }
}

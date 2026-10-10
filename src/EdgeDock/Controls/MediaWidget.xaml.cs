using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;

namespace EdgeDock.Controls;

public sealed partial class MediaWidget : UserControl
{
    private MediaSessionService? _service;
    private MediaSnapshot? _lastSnapshot;
    private long _displayedMediaVersion = -1;
    private bool _isSubscribed;
    private bool _showArtwork = true;

    public MediaWidget()
    {
        InitializeComponent();
        Loaded += MediaWidget_Loaded;
        Unloaded += MediaWidget_Unloaded;
    }

    internal bool ShowArtwork
    {
        get => _showArtwork;
        set
        {
            if (_showArtwork == value)
            {
                return;
            }

            _showArtwork = value;
            if (_lastSnapshot is not null)
            {
                _ = ApplyMediaSnapshotAsync(_lastSnapshot);
            }
            else
            {
                ArtworkContainer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    internal void Bind(MediaSessionService service)
    {
        if (ReferenceEquals(_service, service))
        {
            return;
        }

        Unsubscribe();
        _service = service;

        if (IsLoaded)
        {
            SubscribeAndRender();
        }
    }

    private void MediaWidget_Loaded(object sender, RoutedEventArgs args) => SubscribeAndRender();

    private void MediaWidget_Unloaded(object sender, RoutedEventArgs args) => Unsubscribe();

    private void SubscribeAndRender()
    {
        if (_service is null)
        {
            return;
        }

        if (!_isSubscribed)
        {
            _service.SnapshotChanged += Media_SnapshotChanged;
            _isSubscribed = true;
        }

        _ = ApplyMediaSnapshotAsync(_service.CurrentSnapshot);
    }

    private void Unsubscribe()
    {
        if (_isSubscribed && _service is not null)
        {
            _service.SnapshotChanged -= Media_SnapshotChanged;
        }

        _isSubscribed = false;
    }

    private void Media_SnapshotChanged(object? sender, MediaSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isSubscribed && ReferenceEquals(sender, _service))
            {
                _ = ApplyMediaSnapshotAsync(snapshot);
            }
        });
    }

    private async Task ApplyMediaSnapshotAsync(MediaSnapshot snapshot)
    {
        if (snapshot.Version < _displayedMediaVersion)
        {
            return;
        }

        _displayedMediaVersion = snapshot.Version;
        _lastSnapshot = snapshot;
        ShowSource(snapshot);
        TrackTitle.Text = snapshot.Title;
        TrackArtist.Text = snapshot.Artist;
        PreviousButton.IsEnabled = snapshot.CanGoPrevious;
        PlayPauseButton.IsEnabled = snapshot.CanTogglePlayPause;
        NextButton.IsEnabled = snapshot.CanGoNext;
        PlayPauseIcon.Symbol = snapshot.IsPlaying ? Symbol.Pause : Symbol.Play;
        var playPauseLabel = snapshot.IsPlaying ? "Pause" : "Play";
        AutomationProperties.SetName(PlayPauseButton, playPauseLabel);
        ToolTipService.SetToolTip(PlayPauseButton, playPauseLabel);
        MediaStatus.Text = snapshot.StatusMessage ?? string.Empty;
        MediaStatus.Visibility = string.IsNullOrEmpty(snapshot.StatusMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ArtworkContainer.Visibility = _showArtwork ? Visibility.Visible : Visibility.Collapsed;
        if (!_showArtwork || snapshot.Artwork is null)
        {
            ShowArtworkFallback();
            return;
        }

        await ShowArtworkAsync(snapshot.Artwork, snapshot.Version);
    }

    private async Task ShowArtworkAsync(byte[] artwork, long version)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(artwork);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!_isSubscribed || version != _displayedMediaVersion || !_showArtwork)
            {
                return;
            }

            AlbumArtwork.Source = bitmap;
            AlbumArtwork.Visibility = Visibility.Visible;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is ArgumentException or COMException)
        {
            if (_isSubscribed && version == _displayedMediaVersion)
            {
                ShowArtworkFallback();
            }
        }
    }

    /// <summary>Names the controlled app; offers a choice once two or more apps have media.</summary>
    private void ShowSource(MediaSnapshot snapshot)
    {
        SourceText.Text = string.IsNullOrWhiteSpace(snapshot.SourceName) ? "Now playing" : $"Now playing · {snapshot.SourceName}";
        var canChoose = snapshot.SourceCount > 1 || _service?.IsPinned == true;
        SourceChevron.Visibility = canChoose ? Visibility.Visible : Visibility.Collapsed;
        SourceButton.IsHitTestVisible = canChoose;
        SourceButton.IsTabStop = canChoose;
    }

    private void SourceButton_Click(object sender, RoutedEventArgs args)
    {
        if (_service is null) return;
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var automatic = new ToggleMenuFlyoutItem { Text = "Automatic (what Windows is playing)", IsChecked = !_service.IsPinned, MinHeight = 48 };
        automatic.Click += async (_, _) => await _service.ChooseSourceAsync(null);
        menu.Items.Add(automatic);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var source in _service.GetSources())
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = source.IsPlaying ? $"{source.Name}  ·  playing" : source.Name,
                IsChecked = _service.IsPinned && source.IsSelected,
                MinHeight = 48
            };
            var id = source.Id;
            item.Click += async (_, _) => await _service.ChooseSourceAsync(id);
            menu.Items.Add(item);
        }
        menu.ShowAt(SourceButton);
    }

    private void ShowArtworkFallback()
    {
        AlbumArtwork.Source = null;
        AlbumArtwork.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
    }

    private async void Previous_Click(object sender, RoutedEventArgs args)
    {
        if (_service is not null)
        {
            await _service.PreviousAsync();
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs args)
    {
        if (_service is not null)
        {
            await _service.TogglePlayPauseAsync();
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs args)
    {
        if (_service is not null)
        {
            await _service.NextAsync();
        }
    }
}

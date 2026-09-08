using Windows.Media.Control;

namespace EdgeDock;

internal sealed record MediaSnapshot(
    string Title,
    string Artist,
    bool HasSession,
    bool CanGoPrevious,
    bool CanTogglePlayPause,
    bool CanGoNext,
    bool IsPlaying,
    string? StatusMessage = null);

internal sealed class MediaSessionService : IDisposable
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;
    private int _sessionGeneration;

    public event EventHandler<MediaSnapshot>? SnapshotChanged;

    public async Task InitializeAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _manager = manager;
                _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                _manager.SessionsChanged += Manager_SessionsChanged;
            }

            AttachSession(manager.GetCurrentSession());
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Raise(new("Nothing playing", "Media controls are unavailable", false, false, false, false, false,
                "Windows did not grant access to current media."));
        }
    }

    public async Task PreviousAsync() => await RunCommandAsync(session => session.TrySkipPreviousAsync());
    public async Task NextAsync() => await RunCommandAsync(session => session.TrySkipNextAsync());

    public async Task TogglePlayPauseAsync()
    {
        var session = GetCurrentSession();
        if (session is null)
        {
            return;
        }

        try
        {
            var info = session.GetPlaybackInfo();
            if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                if (!await session.TryPauseAsync())
                {
                    await RefreshAsync("The media app did not accept that command.");
                    return;
                }
            }
            else
            {
                if (!await session.TryPlayAsync())
                {
                    await RefreshAsync("The media app did not accept that command.");
                    return;
                }
            }

            await RefreshAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            await RefreshAsync("The media app did not accept that command.");
        }
    }

    private async Task RunCommandAsync(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        var session = GetCurrentSession();
        if (session is null)
        {
            return;
        }

        try
        {
            if (!await command(session))
            {
                await RefreshAsync("The media app did not accept that command.");
                return;
            }
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            await RefreshAsync("The media app did not accept that command.");
        }
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        AttachSession(sender.GetCurrentSession());
        _ = RefreshAsync();
    }

    private void Manager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        AttachSession(sender.GetCurrentSession());
        _ = RefreshAsync();
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (ReferenceEquals(_session, session))
            {
                return;
            }

            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= Session_Changed;
                _session.PlaybackInfoChanged -= Session_Changed;
            }

            _session = session;
            _sessionGeneration++;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += Session_Changed;
                _session.PlaybackInfoChanged += Session_Changed;
            }
        }
    }

    private void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    private async Task RefreshAsync(string? statusMessage = null)
    {
        GlobalSystemMediaTransportControlsSession? session;
        int generation;
        lock (_gate)
        {
            session = _session;
            generation = _sessionGeneration;
        }

        if (session is null)
        {
            Raise(new("Nothing playing", "Start audio in a Windows media app", false, false, false, false, false, statusMessage), generation, session);
            return;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;
            var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            Raise(new(
                string.IsNullOrWhiteSpace(properties?.Title) ? "Unknown track" : properties.Title,
                string.IsNullOrWhiteSpace(properties?.Artist) ? "Unknown artist" : properties.Artist,
                true,
                controls?.IsPreviousEnabled == true,
                isPlaying ? controls?.IsPauseEnabled == true : controls?.IsPlayEnabled == true,
                controls?.IsNextEnabled == true,
                isPlaying,
                statusMessage), generation, session);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Raise(new("Nothing playing", "The previous media session ended", false, false, false, false, false,
                statusMessage ?? "Waiting for a media app."), generation, session);
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetCurrentSession()
    {
        lock (_gate)
        {
            return _disposed ? null : _session;
        }
    }

    private void Raise(MediaSnapshot snapshot, int? expectedGeneration = null, GlobalSystemMediaTransportControlsSession? expectedSession = null)
    {
        EventHandler<MediaSnapshot>? handler;
        lock (_gate)
        {
            if (_disposed ||
                (expectedGeneration.HasValue && expectedGeneration.Value != _sessionGeneration) ||
                (expectedGeneration.HasValue && !ReferenceEquals(expectedSession, _session)))
            {
                return;
            }

            handler = SnapshotChanged;
        }

        handler?.Invoke(this, snapshot);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sessionGeneration++;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= Session_Changed;
                _session.PlaybackInfoChanged -= Session_Changed;
                _session = null;
            }

            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                _manager.SessionsChanged -= Manager_SessionsChanged;
                _manager = null;
            }

            SnapshotChanged = null;
        }
    }
}

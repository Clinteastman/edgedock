using Windows.Media.Control;
using Windows.Storage.Streams;

namespace EdgeDock;

internal sealed record MediaSnapshot(
    string Title,
    string Artist,
    bool HasSession,
    bool CanGoPrevious,
    bool CanTogglePlayPause,
    bool CanGoNext,
    bool IsPlaying,
    string? StatusMessage = null,
    byte[]? Artwork = null,
    long Version = 0,
    string? SourceName = null,
    int SourceCount = 0);

internal sealed class MediaSessionService : IDisposable
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;
    private int _sessionGeneration;
    private long _refreshGeneration;
    private CancellationTokenSource? _artworkCancellation;
    // The app the user chose to control; null follows Windows' current media session.
    private string? _pinnedSourceId;
    private readonly Dictionary<string, string> _sourceNames = new(StringComparer.OrdinalIgnoreCase);
    private MediaSnapshot _currentSnapshot = new(
        "Nothing playing",
        "Start audio in a Windows media app",
        false,
        false,
        false,
        false,
        false);

    public event EventHandler<MediaSnapshot>? SnapshotChanged;

    internal MediaSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _currentSnapshot;
            }
        }
    }

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

            AttachSession(SelectSession(manager));
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Raise(new("Nothing playing", "Media controls are unavailable", false, false, false, false, false,
                "Windows did not grant access to current media."));
        }
    }

    /// <summary>Every app with a media session, with which one is controlled now.</summary>
    public IReadOnlyList<MediaSource> GetSources()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        GlobalSystemMediaTransportControlsSession? attached;
        lock (_gate)
        {
            manager = _manager;
            attached = _session;
        }
        if (manager is null) return [];
        try
        {
            return manager.GetSessions()
                .Select(session => new MediaSource(
                    session.SourceAppUserModelId,
                    NameFor(session.SourceAppUserModelId),
                    IsPlaying(session),
                    ReferenceEquals(session, attached) || session.SourceAppUserModelId == attached?.SourceAppUserModelId))
                .OrderByDescending(source => source.IsPlaying)
                .ThenBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>Whether the user pinned an app rather than following Windows.</summary>
    public bool IsPinned
    {
        get { lock (_gate) return _pinnedSourceId is not null; }
    }

    /// <summary>
    /// Controls a chosen app until its session ends (then Windows' choice again), or follows
    /// Windows' current session when <paramref name="sourceId"/> is null.
    /// </summary>
    public async Task ChooseSourceAsync(string? sourceId)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
        {
            if (_disposed) return;
            _pinnedSourceId = sourceId;
            manager = _manager;
        }
        if (manager is null) return;
        AttachSession(SelectSession(manager));
        await RefreshAsync();
    }

    private GlobalSystemMediaTransportControlsSession? SelectSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        string? pinned;
        lock (_gate) pinned = _pinnedSourceId;
        if (pinned is not null)
        {
            var match = manager.GetSessions().FirstOrDefault(session =>
                string.Equals(session.SourceAppUserModelId, pinned, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            // The chosen app's session ended; go back to following Windows.
            lock (_gate) _pinnedSourceId = null;
        }
        return manager.GetCurrentSession();
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    /// <summary>Windows' own name for packaged apps, otherwise a cleaned-up app ID.</summary>
    private string NameFor(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return MediaNaming.FallbackName(appId);
        lock (_gate)
            if (_sourceNames.TryGetValue(appId, out var known)) return known;
        string name;
        try { name = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appId).DisplayInfo.DisplayName; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            name = MediaNaming.FallbackName(appId);
        }
        if (string.IsNullOrWhiteSpace(name)) name = MediaNaming.FallbackName(appId);
        lock (_gate) _sourceNames[appId] = name;
        return name;
    }

    private int SourceCount()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate) manager = _manager;
        try { return manager?.GetSessions().Count ?? 0; }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { return 0; }
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
        // A pinned app stays in control while its session exists.
        AttachSession(SelectSession(sender));
        _ = RefreshAsync();
    }

    private void Manager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        AttachSession(SelectSession(sender));
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
        long refreshGeneration;
        CancellationToken artworkCancellation;
        lock (_gate)
        {
            session = _session;
            generation = _sessionGeneration;
            refreshGeneration = ++_refreshGeneration;
            _artworkCancellation?.Cancel();
            _artworkCancellation?.Dispose();
            _artworkCancellation = new CancellationTokenSource();
            artworkCancellation = _artworkCancellation.Token;
        }

        var sourceCount = SourceCount();
        if (session is null)
        {
            Raise(
                new("Nothing playing", "Start audio in a Windows media app", false, false, false, false, false, statusMessage,
                    Version: refreshGeneration, SourceCount: sourceCount),
                generation,
                session,
                refreshGeneration);
            return;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;
            var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var artwork = await LoadArtworkAsync(properties?.Thumbnail, artworkCancellation);

            Raise(new(
                string.IsNullOrWhiteSpace(properties?.Title) ? "Unknown track" : properties.Title,
                string.IsNullOrWhiteSpace(properties?.Artist) ? "Unknown artist" : properties.Artist,
                true,
                controls?.IsPreviousEnabled == true,
                isPlaying ? controls?.IsPauseEnabled == true : controls?.IsPlayEnabled == true,
                controls?.IsNextEnabled == true,
                isPlaying,
                statusMessage,
                artwork,
                refreshGeneration,
                NameFor(session.SourceAppUserModelId),
                sourceCount), generation, session, refreshGeneration);
        }
        catch (OperationCanceledException)
        {
            // A newer track/session refresh superseded this result.
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Raise(new("Nothing playing", "The previous media session ended", false, false, false, false, false,
                statusMessage ?? "Waiting for a media app.", Version: refreshGeneration), generation, session, refreshGeneration);
        }
    }

    private static async Task<byte[]?> LoadArtworkAsync(IRandomAccessStreamReference? reference, CancellationToken cancellationToken)
    {
        if (reference is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask(timeout.Token);
            if (stream.Size is 0 or > 4 * 1024 * 1024)
            {
                return null;
            }

            using var input = stream.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            var requested = (uint)stream.Size;
            var loaded = await reader.LoadAsync(requested).AsTask(timeout.Token);
            if (loaded == 0)
            {
                return null;
            }

            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetCurrentSession()
    {
        lock (_gate)
        {
            return _disposed ? null : _session;
        }
    }

    private void Raise(
        MediaSnapshot snapshot,
        int? expectedGeneration = null,
        GlobalSystemMediaTransportControlsSession? expectedSession = null,
        long? expectedRefreshGeneration = null)
    {
        EventHandler<MediaSnapshot>? handler;
        lock (_gate)
        {
            if (_disposed ||
                (expectedGeneration.HasValue && expectedGeneration.Value != _sessionGeneration) ||
                (expectedGeneration.HasValue && !ReferenceEquals(expectedSession, _session)) ||
                (expectedRefreshGeneration.HasValue && expectedRefreshGeneration.Value != _refreshGeneration))
            {
                return;
            }

            _currentSnapshot = snapshot;
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
            _refreshGeneration++;
            _artworkCancellation?.Cancel();
            _artworkCancellation?.Dispose();
            _artworkCancellation = null;
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

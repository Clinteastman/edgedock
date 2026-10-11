using EdgeDock.Controls;
using EdgeDock.Widgets;
using Microsoft.UI;
using Microsoft.UI.Composition;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.System.Power;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;

namespace EdgeDock;

public sealed partial class MainWindow : Window
{
    private const double MinimumWidth = 900;
    private const double MinimumHeight = 420;
    private const double MinimumPanelWidth = 240;
    private const double MaximumPanelWidth = 440;
    private readonly SettingsStore _settings = new();
    private readonly MediaSessionService _media = new();
    private readonly GetMessageHookProc _messageHookCallback;
    private CoreWebView2Environment? _webEnvironment;
    private IReadOnlyList<WebCardSettings> _webCards = [];
    private int _webPanelCount = 1;
    private IReadOnlyList<string> _webPanelCardIds = [string.Empty];
    private readonly List<WebPanelView> _webPanels = [];
    private IntPtr _windowHandle;
    private IntPtr _messageHook;
    private MediaPanelPlacement _mediaPlacement = MediaPanelPlacement.Right;
    private MediaPanelPlacement _lastVisiblePlacement = MediaPanelPlacement.Right;
    private double _configuredPanelWidth = 340;
    private double _configuredWebSplitRatio = 0.5;
    private bool _showArtwork = true;
    private bool _isFullScreen;
    private bool _enforcingMinimumSize;
    private bool _keyTransitionPending;
    private bool _isWebVisible = true;
    private bool _isWidgetView;
    private BackdropMaterial _material = BackdropMaterial.Mica;
    private double _backdropTransparency = 50;
    private IReadOnlyList<WidgetSlotSettings> _widgetSlots = [new(["media"], "media")];
    private readonly WidgetRegistry _registry;
    private WeatherLocation? _weatherLocation;
    private bool _fahrenheit;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _webPanelGate = new(1, 1);
    private bool _closing;
    private Control? _controlsFocusReturn;
    private bool _updatingPanelCount;
    private bool _updatingWebPanelCount;
    private CompositionRoundedRectangleGeometry? _webClipGeometry;
    private PanelDivider? _activeDivider;
    private uint _activePointerId;
    private double _resizeStartX;
    private double _resizeStartWebSplitRatio;
    private double _resizeStartPanelWidth;
    private DisplayPreference? _displayPreference;
    private ScreenScheduleSettings _screenSettings = ScreenScheduleSettings.Default;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _screenTimer;
    private bool _screenBlank;
    // Level in use just before blanking, and the monitor it belongs to.
    private int? _brightnessBeforeBlank;
    private DisplayPreference? _blankedMonitor;
    // Brightness from before EdgeDock first changed a monitor; saved with the layout.
    private BrightnessRestore? _brightnessRestore;
    // The scheduled brightness last applied; re-applied only when it changes, so EdgeDock
    // never fights adjustments made with the monitor's own buttons in between.
    private int? _appliedScheduleBrightness;
    private bool _screenBusy;
    // The screen update in progress, awaited in full before closing restores brightness.
    private Task _screenWork = Task.CompletedTask;
    // Where the scheduled level was last applied: full screen on which monitor. A change (such
    // as leaving full screen, moving and returning before the next tick) means it must be
    // applied again, to the monitor EdgeDock is on now.
    private (bool FullScreen, IntPtr Monitor) _scheduleTarget;
    // A restore that reached the monitor but whose removal from settings.json is not yet saved.
    private bool _restoreClearUnsaved;
    // The settings as last written (or loaded) successfully. Display-only saves merge onto
    // this rather than re-reading settings.json, which yields defaults if the file is locked
    // or damaged and would then overwrite the whole layout.
    private EdgeDockSettings? _savedSettings;
    private bool _closeAfterPendingSave;
    private bool _closeReady;
    private DateTimeOffset _lastDisplayChange = DateTimeOffset.MinValue;
    private bool _wasMinimized;
    // Every screen save started, so closing can wait for one already past its debounce.
    private Task _displaySaves = Task.CompletedTask;
    private bool _openFullScreen;
    private bool _awaitingPreferredDisplay;
    private bool _minimizedForMissingDisplay;
    private DisplayAreaWatcher? _displayWatcher;
    private DispatcherQueueTimer? _displayChangeTimer;
    private DispatcherQueueTimer? _windowMoveTimer;
    private readonly RecoveryBudget _browserRecoveries = new(3, TimeSpan.FromMinutes(10));
    private bool _recreatingWebPanels;
    private DateTimeOffset? _suspendedAt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resumeReloadTimer;

    public MainWindow()
    {
        _messageHookCallback = MessageHookCallback;
        _registry = WidgetRegistry.CreateBuiltIns(() => new ClockOptions(_weatherLocation, _fahrenheit));
        InitializeComponent();
        RegisterDividerHandlers(WebDivider);
        RegisterDividerHandlers(GroupDivider);
        ApplyBackdrop();
        ConfigureWindow();
        InstallMessageHook();
        PowerManager.SystemSuspendStatusChanged += PowerManager_SystemSuspendStatusChanged;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;

    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        if (int.TryParse(Environment.GetEnvironmentVariable("EDGEDOCK_PREVIEW_WIDTH"), out var width) &&
            int.TryParse(Environment.GetEnvironmentVariable("EDGEDOCK_PREVIEW_HEIGHT"), out var height))
            AppWindow.ResizeClient(new SizeInt32((int)(Math.Clamp(width, 900, 4000) * scale), (int)(Math.Clamp(height, 420, 2200) * scale)));
        await InitializeAsync();
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "EdgeDock.ico");
        AppWindow.SetIcon(iconPath);
        AppTitleBarIcon.Source = new BitmapImage(new Uri(iconPath));
        AppWindow.ResizeClient(new SizeInt32(1600, 720));
        AppWindow.TitleBar.BackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ForegroundColor = Colors.White;
        AppWindow.TitleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 200, 194, 212);
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 52, 43, 72);
    }

    private async Task InitializeAsync()
    {
        _ = _media.InitializeAsync();
        var settings = await _settings.LoadAsync();
        _savedSettings = settings;
        _material = settings.Material;
        _backdropTransparency = settings.BackdropTransparency;
        ApplyBackdrop();
        _mediaPlacement = settings.MediaPanelPlacement;
        _lastVisiblePlacement = settings.LastVisibleMediaPanelPlacement;
        _configuredPanelWidth = settings.MediaPanelWidth;
        _showArtwork = settings.ShowArtwork;
        _isWebVisible = settings.IsWebVisible;
        _widgetSlots = settings.WidgetSlots;
        _webCards = settings.WebCards!;
        _webPanelCount = settings.WebPanelCount;
        _webPanelCardIds = settings.WebPanelCardIds!;
        _configuredWebSplitRatio = settings.WebSplitRatio;
        _displayPreference = settings.Display;
        _openFullScreen = settings.OpenFullScreen;
        _screenSettings = settings.Screen ?? ScreenScheduleSettings.Default;
        _brightnessRestore = settings.PendingBrightness;
        _weatherLocation = settings.Weather;
        _fahrenheit = settings.Fahrenheit;
        // Move to the saved screen before WebView2 creates its child HWNDs, so pages
        // render once at the right size and scale.
        RestorePreferredDisplay();
        StartDisplayTracking();
        StartScreenSchedule();
        BuildWidgetSlots();
        // Establish the final native/web bounds before WebView2 creates its child HWNDs.
        ApplyMediaLayout();
        await BuildWebPanelsAsync();
        ApplyMediaLayout();
        UpdateSettingsControls();
    }

    private void RegisterDividerHandlers(PanelDivider divider)
    {
        // Button handles pointer input internally. Listen to handled events so a
        // drag still reaches the resize logic, without registering a second XAML handler.
        divider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Divider_PointerPressed), true);
        divider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Divider_PointerMoved), true);
        divider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Divider_PointerReleased), true);
        divider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Divider_PointerCanceled), true);
        divider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Divider_PointerCaptureLost), true);
    }

    private async Task<CoreWebView2Environment> GetWebEnvironmentAsync()
    {
        _webEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null, _settings.WebViewProfilePath, null);
        return _webEnvironment;
    }

    private async Task BuildWebPanelsAsync()
    {
        await _webPanelGate.WaitAsync();
        try
        {
            if (_closing) return;
            var visibleCount = _isWebVisible ? _webPanelCount : 0;
            while (_webPanels.Count > visibleCount)
            {
                var last = _webPanels[^1];
                last.BrowserProcessFailed -= WebPanel_BrowserProcessFailed;
                WebHost.Children.Remove(last);
                _webPanels.RemoveAt(_webPanels.Count - 1);
                last.Dispose();
            }
            while (_webPanels.Count < visibleCount)
            {
                var panel = new WebPanelView(BuildWebPanelsAsync);
                panel.BrowserProcessFailed += WebPanel_BrowserProcessFailed;
                _webPanels.Add(panel);
                WebHost.Children.Add(panel);
            }
            ConfigureWebPanelColumns();
            ReloadButton.IsEnabled = visibleCount > 0 && _webCards.Count > 0;
            if (visibleCount == 0) return;

            if (_webCards.Count == 0)
            {
                foreach (var panel in _webPanels) panel.ShowSetup();
                return;
            }

            CoreWebView2Environment environment;
            try { environment = await GetWebEnvironmentAsync(); }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or COMException)
            {
                foreach (var panel in _webPanels) panel.ShowUnavailable();
                return;
            }
            for (var index = 0; index < _webPanels.Count; index++)
            {
                var selectedId = _webPanelCardIds.ElementAtOrDefault(index);
                var card = _webCards.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    ?? _webCards.ElementAtOrDefault(index)
                    ?? _webCards[0];
                await _webPanels[index].ShowCardAsync(card, environment);
            }
        }
        finally { _webPanelGate.Release(); }
    }

    /// <summary>
    /// All cards share one browser process. When it ends, each card reports it; recreate
    /// every card and the environment once, within a limit so a broken runtime cannot loop.
    /// </summary>
    private async void WebPanel_BrowserProcessFailed(object? sender, bool userRequested)
    {
        if (_closing || _recreatingWebPanels || sender is not WebPanelView panel || !_webPanels.Contains(panel)) return;
        if (!userRequested && !_browserRecoveries.TryConsume(DateTimeOffset.Now))
        {
            foreach (var each in _webPanels) each.ShowBrowserFailed();
            return;
        }

        _recreatingWebPanels = true;
        try
        {
            await _webPanelGate.WaitAsync();
            try
            {
                foreach (var each in _webPanels)
                {
                    each.BrowserProcessFailed -= WebPanel_BrowserProcessFailed;
                    WebHost.Children.Remove(each);
                    each.Dispose();
                }
                _webPanels.Clear();
                _webEnvironment = null;
            }
            finally { _webPanelGate.Release(); }
            await BuildWebPanelsAsync();
            ApplyMediaLayout();
        }
        finally { _recreatingWebPanels = false; }
    }

    private void PowerManager_SystemSuspendStatusChanged(object? sender, object args)
    {
        // Raised on a background thread; read the status there, act on the window's thread.
        var status = PowerManager.SystemSuspendStatus;
        var now = DateTimeOffset.Now;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            if (status == SystemSuspendStatus.Entering)
            {
                _suspendedAt = now;
                _resumeReloadTimer?.Stop();
                return;
            }
            if (status is not (SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)) return;
            var reload = WebRecovery.ShouldReloadAfterResume(_suspendedAt, now);
            _suspendedAt = null;
            if (!reload) return;
            if (_resumeReloadTimer is null)
            {
                _resumeReloadTimer = DispatcherQueue.CreateTimer();
                _resumeReloadTimer.IsRepeating = false;
                _resumeReloadTimer.Interval = WebRecovery.ResumeReloadDelay;
                _resumeReloadTimer.Tick += (_, _) =>
                {
                    if (_closing) return;
                    foreach (var panel in _webPanels) panel.Reload();
                };
            }
            _resumeReloadTimer.Start();
        });
    }

    private void ConfigureWebPanelColumns()
    {
        WebHost.ColumnDefinitions.Clear();
        if (_webPanels.Count < 2)
        {
            WebHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WebDivider.Visibility = Visibility.Collapsed;
            if (_webPanels.Count == 1) Grid.SetColumn(_webPanels[0], 0);
            return;
        }

        WebHost.ColumnDefinitions.Add(new ColumnDefinition());
        WebHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        WebHost.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_webPanels[0], 0);
        Grid.SetColumn(WebDivider, 1);
        Grid.SetColumn(_webPanels[1], 2);
        WebDivider.Visibility = Visibility.Visible;
        ApplyWebSplitLayout();
    }

    private void ApplyWebSplitLayout()
    {
        if (_webPanels.Count != 2 || WebHost.ColumnDefinitions.Count != 3) return;
        var available = Math.Max(0, WebHost.ActualWidth - 10);
        var ratio = PanelLayout.EffectiveWebSplitRatio(_configuredWebSplitRatio, available);
        WebHost.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
        WebHost.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void InstallMessageHook()
    {
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _messageHook = SetWindowsHookEx(GetMessageHook, _messageHookCallback, IntPtr.Zero, GetCurrentThreadId());
    }

    private IntPtr MessageHookCallback(int code, IntPtr removeMessage, IntPtr messagePointer)
    {
        if (code >= 0 && removeMessage == new IntPtr(RemoveMessage) && messagePointer != IntPtr.Zero)
        {
            var message = Marshal.PtrToStructure<NativeMessage>(messagePointer);
            var isFromThisWindow = message.Window == _windowHandle || IsChild(_windowHandle, message.Window);
            var isKeyDown = message.Message is WindowKeyDown or WindowSystemKeyDown;
            var isRepeat = (message.KeyStatus.ToInt64() & (1L << 30)) != 0;

            if (isFromThisWindow && isKeyDown && !isRepeat && !_keyTransitionPending &&
                message.VirtualKey.ToUInt64() == (uint)VirtualKey.F11)
            {
                _keyTransitionPending = true;
                message.Message = NullMessage;
                Marshal.StructureToPtr(message, messagePointer, false);
                DispatcherQueue.TryEnqueue(() =>
                {
                    ToggleFullScreen();
                    _keyTransitionPending = false;
                });
            }
        }

        return CallNextHookEx(_messageHook, code, removeMessage, messagePointer);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.F11)
        {
            args.Handled = true;
            ToggleFullScreen();
        }
    }

    private void WebSurface_LayoutChanged(object sender, RoutedEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(WebSurface);
        if (_webClipGeometry is null)
        {
            _webClipGeometry = visual.Compositor.CreateRoundedRectangleGeometry();
            _webClipGeometry.CornerRadius = new Vector2(14);
            visual.Clip = visual.Compositor.CreateGeometricClip(_webClipGeometry);
        }

        _webClipGeometry.Size = new Vector2((float)WebSurface.ActualWidth, (float)WebSurface.ActualHeight);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs args)
    {
        if (_isWidgetView) ExitWidgetView();
        CloseControlsDrawer(restoreFocus: false);
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        UrlErrorText.Visibility = Visibility.Collapsed;
        UpdateSettingsControls();
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs args)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        UrlErrorText.Visibility = Visibility.Collapsed;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs args) => await SaveAndNavigateAsync();

    private async Task SaveAndNavigateAsync()
    {
        if (!WebCardEditor.TryGetCards(out var cards, out var cardError))
        {
            UrlErrorText.Text = cardError;
            UrlErrorText.Visibility = Visibility.Visible;
            return;
        }
        // Saving with the toggle on also repairs an entry left pointing at a moved or older copy.
        var startupNeedsChange = StartupToggle.IsOn ? !StartupRegistration.PointsHere() : StartupRegistration.IsEnabled();
        if (StartupToggle.IsEnabled && startupNeedsChange && !StartupRegistration.SetEnabled(StartupToggle.IsOn))
        {
            UrlErrorText.Text = "Windows did not allow EdgeDock to change its sign-in setting. Your layout was not saved.";
            UrlErrorText.Visibility = Visibility.Visible;
            return;
        }
        var placement = PlacementComboBox.SelectedIndex switch
        {
            1 => MediaPanelPlacement.Left,
            2 => MediaPanelPlacement.Hidden,
            _ => MediaPanelPlacement.Right
        };
        var settings = SettingsStore.Normalize(new EdgeDockSettings(cards.FirstOrDefault()?.Url, placement,
            placement == MediaPanelPlacement.Hidden ? _lastVisiblePlacement : placement,
            PanelWidthSlider.Value, ArtworkToggle.IsOn, WebVisibleToggle.IsOn, LibraryEditor.GetSlots(),
            MaterialSelector.SelectedIndex == 1 ? BackdropMaterial.Acrylic : BackdropMaterial.Mica,
            BackdropTransparencySlider.Value, cards, _webPanelCount, _webPanelCardIds,
            _configuredWebSplitRatio, _displayPreference, _openFullScreen,
            Screen: ReadScreenSettings(),
            PendingBrightness: _brightnessRestore,
            Weather: WeatherPlaceSelector.SelectedItem is ComboBoxItem { Tag: WeatherLocation place } ? place : null,
            Fahrenheit: FahrenheitToggle.IsOn));
        if (!await PersistAsync(settings)) return;
        _material = settings.Material;
        _backdropTransparency = settings.BackdropTransparency;
        ApplyBackdrop();
        _mediaPlacement = settings.MediaPanelPlacement;
        _lastVisiblePlacement = settings.LastVisibleMediaPanelPlacement;
        _configuredPanelWidth = settings.MediaPanelWidth;
        _showArtwork = settings.ShowArtwork;
        _isWebVisible = settings.IsWebVisible;
        _widgetSlots = settings.WidgetSlots;
        _webCards = settings.WebCards!;
        _webPanelCount = settings.WebPanelCount;
        _webPanelCardIds = settings.WebPanelCardIds!;
        _configuredWebSplitRatio = settings.WebSplitRatio;
        var newSchedule = settings.Screen ?? ScreenScheduleSettings.Default;
        if (newSchedule != _screenSettings)
        {
            // Rewrite the level only when the brightness part of the schedule changed: saving
            // anything else (the idle timeout included) must not undo a change made on the
            // monitor's own buttons.
            if (newSchedule with { BlankAfterMinutes = 0 } != _screenSettings with { BlankAfterMinutes = 0 })
                _appliedScheduleBrightness = null;
            _screenSettings = newSchedule;
            _ = UpdateScreenAsync();
        }
        _weatherLocation = settings.Weather;
        _fahrenheit = settings.Fahrenheit;
        BuildWidgetSlots();
        ApplyMediaLayout();
        await BuildWebPanelsAsync();
        ApplyMediaLayout();
        SettingsPanel.Visibility = Visibility.Collapsed;
        UrlErrorText.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> PersistAsync(EdgeDockSettings settings, bool alreadyApplied = false)
    {
        await _saveGate.WaitAsync();
        try
        {
            await _settings.SaveAsync(settings);
            _savedSettings = SettingsStore.Normalize(settings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowSaveFailure(alreadyApplied);
            return false;
        }
        finally { _saveGate.Release(); }
    }

    private void ShowSaveFailure(bool alreadyApplied)
    {
        if (_closing) return;
        SettingsPanel.Visibility = Visibility.Visible;
        UrlErrorText.Text = alreadyApplied
            ? "Your change is active but was not saved. It may reset when EdgeDock closes. Check access to local app data."
            : "Could not save your layout. Check access to the local app data folder.";
        UrlErrorText.Visibility = Visibility.Visible;
    }

    private async void ToggleMediaPanel_Click(object sender, RoutedEventArgs args)
    {
        if (!_isWebVisible) return;
        if (_mediaPlacement == MediaPanelPlacement.Hidden) _mediaPlacement = _lastVisiblePlacement;
        else { _lastVisiblePlacement = _mediaPlacement; _mediaPlacement = MediaPanelPlacement.Hidden; }
        ApplyMediaLayout();
        await PersistAsync(CurrentSettings(), alreadyApplied: true);
        CloseControlsDrawer();
    }

    private EdgeDockSettings CurrentSettings() => new(
        _webCards.FirstOrDefault()?.Url,
        _mediaPlacement,
        _lastVisiblePlacement,
        _configuredPanelWidth,
        _showArtwork,
        _isWebVisible,
        _widgetSlots,
        _material,
        _backdropTransparency,
        _webCards,
        _webPanelCount,
        _webPanelCardIds,
        _configuredWebSplitRatio,
        _displayPreference,
        _openFullScreen,
        Screen: _screenSettings,
        PendingBrightness: _brightnessRestore,
        Weather: _weatherLocation,
        Fahrenheit: _fahrenheit);

    private void ApplyBackdrop()
    {
        SystemBackdrop = AdjustableBackdrop.IsSupported(_material)
            ? new AdjustableBackdrop(_material, _backdropTransparency)
            : _material == BackdropMaterial.Acrylic
                ? new DesktopAcrylicBackdrop()
                : new MicaBackdrop();
    }

    private void UpdateSettingsControls()
    {
        MaterialSelector.SelectedIndex = _material == BackdropMaterial.Acrylic ? 1 : 0;
        BackdropTransparencySlider.Value = _backdropTransparency;
        UpdateBackdropSettingsText();
        PlacementComboBox.SelectedIndex = _mediaPlacement switch
        {
            MediaPanelPlacement.Left => 1,
            MediaPanelPlacement.Hidden => 2,
            _ => 0
        };
        PanelWidthSlider.Value = _configuredPanelWidth;
        PanelWidthText.Text = $"{_configuredPanelWidth:0} px";
        ArtworkToggle.IsOn = _showArtwork;
        WebVisibleToggle.IsOn = _isWebVisible;
        WebCardEditor.LoadCards(_webCards);
        LibraryEditor.LoadSlots(_widgetSlots, _registry);
        // An isolated test profile must not rewrite the real sign-in entry.
        StartupToggle.IsEnabled = !SettingsStore.IsIsolatedProfile;
        StartupToggle.IsOn = StartupRegistration.IsEnabled();
        DimAtNightToggle.IsOn = _screenSettings.DimAtNight;
        DayBrightnessSlider.Value = _screenSettings.DayBrightness;
        NightBrightnessSlider.Value = _screenSettings.NightBrightness;
        NightStartPicker.Time = _screenSettings.NightStart;
        NightEndPicker.Time = _screenSettings.NightEnd;
        BlankAfterSelector.SelectedIndex = Math.Max(0, Array.IndexOf(ScreenSchedule.BlankChoices, _screenSettings.BlankAfterMinutes));
        LoadWeatherPlaces(_weatherLocation, []);
        FahrenheitToggle.IsOn = _fahrenheit;
        WeatherSearchStatus.Visibility = Visibility.Collapsed;
    }

    /// <summary>"No weather", the current place, then any search results.</summary>
    private void LoadWeatherPlaces(WeatherLocation? selected, IReadOnlyList<WeatherLocation> found)
    {
        WeatherPlaceSelector.Items.Clear();
        WeatherPlaceSelector.Items.Add(new ComboBoxItem { Content = "No weather" });
        var places = (selected is null ? found : found.Prepend(selected)).DistinctBy(place => (place.Latitude, place.Longitude));
        foreach (var place in places)
            WeatherPlaceSelector.Items.Add(new ComboBoxItem { Content = place.Name, Tag = place });
        WeatherPlaceSelector.SelectedIndex = selected is null ? (found.Count > 0 ? 1 : 0) : 1;
    }

    private async void FindWeatherPlace_Click(object sender, RoutedEventArgs args)
    {
        var query = WeatherSearchBox.Text.Trim();
        if (query.Length == 0) return;
        FindWeatherPlaceButton.IsEnabled = false;
        WeatherSearchStatus.Text = "Searching…";
        WeatherSearchStatus.Visibility = Visibility.Visible;
        try
        {
            var found = await WeatherService.SearchAsync(query);
            var current = WeatherPlaceSelector.SelectedItem is ComboBoxItem { Tag: WeatherLocation place } ? place : null;
            // Put the first match forward so Save uses it unless another is picked.
            LoadWeatherPlaces(found.FirstOrDefault() ?? current, found.Count > 0 ? found : (current is null ? [] : [current]));
            WeatherSearchStatus.Text = found.Count == 0
                ? $"No places found for \"{query}\". Check the spelling or try a nearby town."
                : $"{found.Count} found. Choose one below, then Save layout.";
        }
        finally { FindWeatherPlaceButton.IsEnabled = true; }
    }

    private void PanelWidthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (PanelWidthText is not null)
        {
            PanelWidthText.Text = $"{args.NewValue:0} px";
        }
    }

    private void BuildWidgetSlots()
    {
        if (_isWidgetView) return;
        WidgetHost.Children.Clear();
        WidgetHost.ColumnDefinitions.Clear();
        for (var index = 0; index < _widgetSlots.Count; index++)
        {
            var slotIndex = index;
            WidgetHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var slot = new WidgetSlotView(_registry, _media, _widgetSlots[index], _showArtwork);
            // Full screen can already be active here, e.g. when reopening on the saved screen.
            slot.SetEdgeToEdge(_isFullScreen);
            slot.SelectionChanged += async (_, selectedId) =>
            {
                var updated = _widgetSlots.ToArray();
                updated[slotIndex] = updated[slotIndex] with { SelectedWidgetId = selectedId };
                _widgetSlots = updated;
                await PersistAsync(CurrentSettings(), alreadyApplied: true);
            };
            Grid.SetColumn(slot, index);
            WidgetHost.Children.Add(slot);
        }
    }

    private void ApplyMediaLayout()
    {
        if (WidgetHost is null) return;
        if (_isWidgetView)
        {
            WebSurface.Visibility = Visibility.Collapsed;
            WidgetHost.Visibility = Visibility.Collapsed;
            WidgetGallery.Visibility = Visibility.Visible;
            Workspace.ColumnSpacing = 0;
            WebColumn.Width = new GridLength(1, GridUnitType.Star);
            GroupDividerColumn.Width = new GridLength(0);
            MediaColumn.Width = new GridLength(0);
            GroupDivider.Visibility = Visibility.Collapsed;
            return;
        }

        WidgetGallery.Visibility = Visibility.Collapsed;
        var widgetsVisible = _mediaPlacement != MediaPanelPlacement.Hidden || !_isWebVisible;
        WidgetHost.Visibility = widgetsVisible ? Visibility.Visible : Visibility.Collapsed;
        WebSurface.Visibility = _isWebVisible ? Visibility.Visible : Visibility.Collapsed;
        var available = Math.Max(800, Root.ActualWidth - Workspace.Padding.Left - Workspace.Padding.Right);
        var dividerVisible = _isWebVisible && widgetsVisible;
        var width = dividerVisible
            ? PanelLayout.EffectiveWidgetGroupWidth(available, _configuredPanelWidth, _widgetSlots.Count, _webPanelCount)
            : 0;
        var widgetWidth = !_isWebVisible ? new GridLength(1, GridUnitType.Star) : widgetsVisible ? new GridLength(width) : new GridLength(0);
        var webWidth = _isWebVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        var left = _mediaPlacement == MediaPanelPlacement.Left;
        Grid.SetColumn(WebSurface, left ? 2 : 0);
        Grid.SetColumn(WidgetHost, left ? 0 : 2);
        Grid.SetColumn(GroupDivider, 1);
        WebColumn.Width = left ? widgetWidth : webWidth;
        MediaColumn.Width = left ? webWidth : widgetWidth;
        GroupDividerColumn.Width = dividerVisible ? new GridLength(10) : new GridLength(0);
        GroupDivider.Visibility = dividerVisible ? Visibility.Visible : Visibility.Collapsed;
        MediaVisibilityButton.IsEnabled = _isWebVisible;
        var label = widgetsVisible ? "Hide widgets" : "Show widgets";
        AutomationProperties.SetName(MediaVisibilityButton, label);
        ToolTipService.SetToolTip(MediaVisibilityButton, _isWebVisible ? label : "Enable the web dashboard before hiding widgets");
        WidgetVisibilityText.Text = label;
    }

    private bool CanResize(PanelDivider divider) =>
        !_isWidgetView &&
        ((divider == WebDivider && _isWebVisible && _webPanels.Count == 2) ||
         (divider == GroupDivider && _isWebVisible && _mediaPlacement != MediaPanelPlacement.Hidden));

    private void Divider_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not PanelDivider divider || !CanResize(divider) || _activeDivider is not null) return;
        var point = args.GetCurrentPoint(Root);
        var captured = divider.CapturePointer(args.Pointer);
        if (!captured) return;

        _activeDivider = divider;
        _activePointerId = args.Pointer.PointerId;
        _resizeStartX = point.Position.X;
        _resizeStartWebSplitRatio = _configuredWebSplitRatio;
        _resizeStartPanelWidth = _configuredPanelWidth;
        foreach (var panel in _webPanels) panel.SetInteractive(false);
        args.Handled = true;
    }

    private void Divider_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not PanelDivider divider || divider != _activeDivider || args.Pointer.PointerId != _activePointerId) return;
        var delta = args.GetCurrentPoint(Root).Position.X - _resizeStartX;
        ApplyResizeDelta(divider, delta);
        args.Handled = true;
    }

    private async void Divider_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not PanelDivider divider || divider != _activeDivider || args.Pointer.PointerId != _activePointerId) return;
        ApplyResizeDelta(divider, args.GetCurrentPoint(Root).Position.X - _resizeStartX);
        CompleteResize(divider);
        args.Handled = true;
        await PersistAsync(CurrentSettings(), alreadyApplied: true);
    }

    private void Divider_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (sender is PanelDivider divider && divider == _activeDivider && args.Pointer.PointerId == _activePointerId)
        {
            RestoreResize(divider);
            _activeDivider = null;
            foreach (var panel in _webPanels) panel.SetInteractive(true);
            args.Handled = true;
        }
    }

    private void Divider_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (sender is PanelDivider divider && divider == _activeDivider)
        {
            RestoreResize(divider);
            _activeDivider = null;
            foreach (var panel in _webPanels) panel.SetInteractive(true);
        }
    }

    private async void Divider_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not PanelDivider divider || !CanResize(divider) ||
            args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;

        var delta = args.Key == VirtualKey.Right ? 10 : -10;
        if (divider == WebDivider)
        {
            var available = Math.Max(1, WebHost.ActualWidth - 10);
            _configuredWebSplitRatio = PanelLayout.NormalizeWebSplitRatio(_configuredWebSplitRatio + delta / available);
            ApplyWebSplitLayout();
        }
        else
        {
            _configuredPanelWidth = PanelLayout.ResizeSharedWidgetWidth(
                _configuredPanelWidth, delta, _widgetSlots.Count, _mediaPlacement);
            ApplyMediaLayout();
            UpdateSettingsControls();
        }

        args.Handled = true;
        await PersistAsync(CurrentSettings(), alreadyApplied: true);
    }

    private void ApplyResizeDelta(PanelDivider divider, double delta)
    {
        if (divider == WebDivider)
        {
            var available = Math.Max(1, WebHost.ActualWidth - 10);
            _configuredWebSplitRatio = PanelLayout.NormalizeWebSplitRatio(_resizeStartWebSplitRatio + delta / available);
            ApplyWebSplitLayout();
        }
        else
        {
            _configuredPanelWidth = PanelLayout.ResizeSharedWidgetWidth(
                _resizeStartPanelWidth, delta, _widgetSlots.Count, _mediaPlacement);
            ApplyMediaLayout();
            UpdateSettingsControls();
        }
    }

    private void CompleteResize(PanelDivider divider)
    {
        _activeDivider = null;
        divider.ReleasePointerCaptures();
        foreach (var panel in _webPanels) panel.SetInteractive(true);
    }

    private void RestoreResize(PanelDivider divider)
    {
        if (divider == WebDivider)
        {
            _configuredWebSplitRatio = _resizeStartWebSplitRatio;
            ApplyWebSplitLayout();
        }
        else
        {
            _configuredPanelWidth = _resizeStartPanelWidth;
            ApplyMediaLayout();
            UpdateSettingsControls();
        }
    }

    private void MaterialSelector_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateBackdropSettingsText();

    private void BackdropTransparencySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (BackdropTransparencyText is not null)
        {
            BackdropTransparencyText.Text = $"{args.NewValue:0}%";
        }
    }

    private void UpdateBackdropSettingsText()
    {
        if (BackdropHelpText is null) return;
        BackdropTransparencyText.Text = $"{BackdropTransparencySlider.Value:0}%";
        BackdropHelpText.Text = MaterialSelector.SelectedIndex == 1
            ? "Higher values show more of the blurred window behind EdgeDock."
            : "Mica stays opaque. Higher values show more of the wallpaper colour in its soft tint.";
    }

    private void ControlHandle_Click(object sender, RoutedEventArgs args) => OpenControlsDrawer();

    private void ControlHandle_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs args)
    {
        if (args.Cumulative.Translation.Y >= 28 &&
            Math.Abs(args.Cumulative.Translation.Y) > Math.Abs(args.Cumulative.Translation.X))
        {
            OpenControlsDrawer();
        }
    }

    private void OpenControlsDrawer()
    {
        if (ControlsOverlay.Visibility == Visibility.Visible) return;
        _controlsFocusReturn = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        if (!_isWidgetView)
        {
            BuildWebPanelSelectors();
            BuildPanelSelectors();
        }
        DashboardControls.Visibility = _isWidgetView ? Visibility.Collapsed : Visibility.Visible;
        WidgetViewText.Text = _isWidgetView ? "Back to dashboard" : "Widget view";
        AutomationProperties.SetName(WidgetViewButton, WidgetViewText.Text);
        ControlsOverlay.Visibility = Visibility.Visible;
        Workspace.IsHitTestVisible = false;
        ControlHandle.IsEnabled = false;
        foreach (var panel in _webPanels) panel.SetInteractive(false);
        CloseControlsButton.Focus(FocusState.Programmatic);
    }

    private void BuildWebPanelSelectors()
    {
        _updatingWebPanelCount = true;
        WebPanelCountSelector.SelectedIndex = _webPanelCount - 1;
        _updatingWebPanelCount = false;
        WebPanelCountSelector.IsEnabled = _isWebVisible && _webCards.Count >= 2;
        WebPanelSelectors.Children.Clear();
        for (var index = 0; index < _webPanelCount; index++)
        {
            var selector = new ComboBox
            {
                Header = $"Web panel {index + 1}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 52,
                Tag = index,
                IsEnabled = _isWebVisible && _webCards.Count > 1
            };
            AutomationProperties.SetName(selector, $"Web card shown in panel {index + 1}");
            foreach (var card in _webCards)
                selector.Items.Add(new ComboBoxItem { Content = card.Name, Tag = card.Id });
            selector.SelectedItem = selector.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag as string, _webPanelCardIds.ElementAtOrDefault(index), StringComparison.OrdinalIgnoreCase));
            selector.SelectionChanged += WebPanelSelector_SelectionChanged;
            WebPanelSelectors.Children.Add(selector);
        }
    }

    private async void WebPanelSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item, Tag: int index } || item.Tag is not string id) return;
        var selections = _webPanelCardIds.ToList();
        while (selections.Count < 2) selections.Add(_webCards.ElementAtOrDefault(selections.Count)?.Id ?? id);
        if (string.Equals(selections[index], id, StringComparison.OrdinalIgnoreCase)) return;
        selections[index] = id;
        var candidate = SettingsStore.Normalize(CurrentSettings() with { WebPanelCardIds = selections });
        if (!await PersistAsync(candidate))
        {
            BuildWebPanelSelectors();
            CloseControlsDrawer(restoreFocus: false);
            return;
        }
        _webPanelCardIds = candidate.WebPanelCardIds!;
        await BuildWebPanelsAsync();
    }

    private async void WebPanelCountSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingWebPanelCount || WebPanelCountSelector.SelectedIndex < 0) return;
        var count = WebPanelCountSelector.SelectedIndex + 1;
        if (count == _webPanelCount || count > _webCards.Count) return;
        var candidate = SettingsStore.Normalize(CurrentSettings() with { WebPanelCount = count });
        if (!await PersistAsync(candidate))
        {
            BuildWebPanelSelectors();
            CloseControlsDrawer(restoreFocus: false);
            return;
        }
        _webPanelCount = candidate.WebPanelCount;
        _webPanelCardIds = candidate.WebPanelCardIds!;
        await BuildWebPanelsAsync();
        BuildWebPanelSelectors();
        ApplyMediaLayout();
    }

    private void BuildPanelSelectors()
    {
        _updatingPanelCount = true;
        PanelCountSelector.SelectedIndex = _widgetSlots.Count - 1;
        _updatingPanelCount = false;
        PanelSelectors.Children.Clear();
        var slots = WidgetHost.Children.OfType<WidgetSlotView>().ToArray();
        for (var index = 0; index < _widgetSlots.Count; index++)
        {
            var selector = new ComboBox
            {
                Header = $"Panel {index + 1}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 52,
                Tag = index
            };
            AutomationProperties.SetName(selector, $"Widget shown in panel {index + 1}");
            foreach (var id in _widgetSlots[index].EnabledWidgetIds)
            {
                var name = _registry.TryGet(id, out var descriptor) && descriptor is not null
                    ? descriptor.DisplayName
                    : $"Unavailable: {id}";
                selector.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            }

            selector.SelectedItem = selector.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag as string, slots.ElementAtOrDefault(index)?.SelectedWidgetId, StringComparison.OrdinalIgnoreCase));
            selector.IsEnabled = selector.Items.Count > 1;
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedItem is ComboBoxItem item && item.Tag is string id)
                    slots.ElementAtOrDefault((int)selector.Tag)?.SelectWidget(id);
            };
            PanelSelectors.Children.Add(selector);
        }
    }

    private async void PanelCountSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingPanelCount || PanelCountSelector.SelectedIndex < 0) return;
        var count = PanelCountSelector.SelectedIndex + 1;
        if (count == _widgetSlots.Count) return;
        var slots = _widgetSlots.Take(count).ToList();
        while (slots.Count < count)
            slots.Add(new WidgetSlotSettings(["media", "clock", "audio", "stats", "pc"], "media"));
        _widgetSlots = slots;
        BuildWidgetSlots();
        ApplyMediaLayout();
        BuildPanelSelectors();
        UpdateSettingsControls();
        if (!await PersistAsync(CurrentSettings(), alreadyApplied: true))
            CloseControlsDrawer(restoreFocus: false);
    }

    private void CloseControls_Click(object sender, RoutedEventArgs args) => CloseControlsDrawer();
    private void ControlsScrim_Tapped(object sender, TappedRoutedEventArgs args) => CloseControlsDrawer();

    private void CloseControlsDrawer(bool restoreFocus = true)
    {
        if (ControlsOverlay.Visibility != Visibility.Visible) return;
        ControlsOverlay.Visibility = Visibility.Collapsed;
        Workspace.IsHitTestVisible = true;
        ControlHandle.IsEnabled = true;
        foreach (var panel in _webPanels) panel.SetInteractive(!_isWidgetView);
        if (restoreFocus)
        {
            if (_controlsFocusReturn is null || !_controlsFocusReturn.Focus(FocusState.Programmatic))
                ControlHandle.Focus(FocusState.Programmatic);
        }
        _controlsFocusReturn = null;
    }

    private void ToggleWidgetView_Click(object sender, RoutedEventArgs args)
    {
        if (_isWidgetView) ExitWidgetView();
        else EnterWidgetView();
        CloseControlsDrawer();
    }

    private void EnterWidgetView()
    {
        if (_isWidgetView) return;
        SettingsPanel.Visibility = Visibility.Collapsed;
        WidgetHost.Children.Clear();
        WidgetHost.ColumnDefinitions.Clear();
        _isWidgetView = true;
        WidgetGalleryRow.Children.Clear();
        foreach (var descriptor in _registry.Items)
        {
            var widget = descriptor.Create();
            if (widget is MediaWidget mediaWidget)
            {
                mediaWidget.Bind(_media);
                mediaWidget.ShowArtwork = _showArtwork;
            }
            WidgetSlotView.SetWidgetEdgeToEdge(widget, _isFullScreen);

            var card = new Grid
            {
                Width = Math.Max(MinimumPanelWidth, _configuredPanelWidth),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            card.Children.Add(widget);
            AutomationProperties.SetName(card, descriptor.DisplayName);
            WidgetGalleryRow.Children.Add(card);
        }
        ApplyMediaLayout();
    }

    private void ExitWidgetView()
    {
        if (!_isWidgetView) return;
        WidgetGalleryRow.Children.Clear();
        WidgetGallery.Visibility = Visibility.Collapsed;
        _isWidgetView = false;
        BuildWidgetSlots();
        ApplyMediaLayout();
    }

    private void Reload_Click(object sender, RoutedEventArgs args)
    {
        foreach (var panel in _webPanels) panel.Reload();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs args) => ToggleFullScreen();

    /// <summary>User-initiated: also remembers this screen and choice for the next launch.</summary>
    private async void ToggleFullScreen()
    {
        SetFullScreen(!_isFullScreen);
        _openFullScreen = _isFullScreen;
        _awaitingPreferredDisplay = false;
        _minimizedForMissingDisplay = false;
        _displayPreference = DisplayPlacement.PreferenceFor(CurrentDisplay()) ?? _displayPreference;
        await PersistDisplayAsync();
    }

    /// <summary>
    /// Saves only the remembered screen and full-screen choice, merged inside the save gate
    /// onto the last successfully saved settings. Snapshotting the whole in-memory layout
    /// could overwrite a newer change that another handler saved but has not applied yet.
    /// </summary>
    private Task PersistDisplayAsync()
    {
        var save = PersistDisplayCoreAsync();
        _displaySaves = Task.WhenAll(_displaySaves, save);
        return save;
    }

    private async Task PersistDisplayCoreAsync()
    {
        await _saveGate.WaitAsync();
        try { await SaveDisplayOntoSavedAsync(_displayPreference, _openFullScreen); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowSaveFailure(alreadyApplied: true);
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>Call only while holding the save gate.</summary>
    private async Task SaveDisplayOntoSavedAsync(DisplayPreference? display, bool openFullScreen)
    {
        var merged = (_savedSettings ?? CurrentSettings()) with { Display = display, OpenFullScreen = openFullScreen };
        await _settings.SaveAsync(merged);
        _savedSettings = SettingsStore.Normalize(merged);
    }

    private ScreenScheduleSettings ReadScreenSettings() => ScreenSchedule.Normalize(new ScreenScheduleSettings(
        DimAtNightToggle.IsOn,
        (int)DayBrightnessSlider.Value,
        (int)NightBrightnessSlider.Value,
        NightStartPicker.Time,
        NightEndPicker.Time,
        ScreenSchedule.BlankChoices[Math.Max(0, BlankAfterSelector.SelectedIndex)]));

    private void StartScreenSchedule()
    {
        _screenTimer = DispatcherQueue.CreateTimer();
        _screenTimer.Interval = TimeSpan.FromSeconds(5);
        _screenTimer.Tick += async (_, _) => await UpdateScreenAsync();
        _screenTimer.Start();
    }

    /// <summary>
    /// Applies night dimming and idle blanking. Only while full screen: then EdgeDock owns the
    /// screen; a windowed EdgeDock may be on the main monitor, which it must never dim.
    /// Brightness uses the monitor's own control (DDC/CI) where available, otherwise a dark
    /// layer over EdgeDock.
    /// </summary>
    private Task UpdateScreenAsync()
    {
        if (_closing || _closeAfterPendingSave || _screenBusy) return Task.CompletedTask;
        _screenBusy = true;
        return _screenWork = RunScreenUpdateAsync();
    }

    private async Task RunScreenUpdateAsync()
    {
        try
        {
            if (!_screenBlank && _brightnessBeforeBlank is not null) await RestoreBlankedBrightnessAsync();
            if (_restoreClearUnsaved && _brightnessRestore is null && await PersistBrightnessRestoreAsync()) _restoreClearUnsaved = false;
            var target = (_isFullScreen, MonitorBrightness.MonitorOf(_windowHandle));
            if (target != _scheduleTarget)
            {
                _scheduleTarget = target;
                _appliedScheduleBrightness = null;
            }
            var state = _isFullScreen
                ? ScreenSchedule.Evaluate(_screenSettings, DateTime.Now, IdleTime())
                : new ScreenState(null, false);
            if (state.Blank && !_screenBlank) await BlankScreenAsync();
            else if (!state.Blank && _screenBlank) await WakeScreenAsync(state.Brightness);
            else if (_screenBlank && _blankWritePending) await RetryBlankWriteAsync();
            else if (!_screenBlank && state.Brightness != _appliedScheduleBrightness)
            {
                // A refused write is retried on the next tick rather than marked as applied.
                var applied = true;
                if (state.Brightness is { } brightness) applied = (await ApplyBrightnessAsync(brightness)).Done;
                else ScreenDimLayer.Opacity = 0; // Schedule off or windowed: remove any software dimming.
                if (applied) _appliedScheduleBrightness = state.Brightness;
            }
            // Nothing scheduled: never leave software dimming behind, whatever the marker says.
            if (!_screenBlank && state.Brightness is null) ScreenDimLayer.Opacity = 0;
            // Nothing scheduled any more (turned off, or not full screen): put the monitor back
            // to how it was before EdgeDock changed it. Retried each tick if it is unplugged.
            if (!_screenBlank && state.Brightness is null && _brightnessRestore is not null) await RestoreOriginalBrightnessAsync();
        }
        finally
        {
            _screenBusy = false;
            // A tap or key during this work (even during the final write) is handled at once,
            // not on a later tick.
            if (_wakeDropped && !_closing)
            {
                _wakeDropped = false;
                DispatcherQueue.TryEnqueue(() => _ = UpdateScreenAsync());
            }
        }
    }

    private async Task BlankScreenAsync()
    {
        _screenBlank = true;
        _wakeRequested = false;
        ScreenBlankOverlay.Visibility = Visibility.Visible;
        // Take keyboard focus (from XAML controls and web pages alike) so wake keys land here.
        _focusBeforeBlank = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        ScreenWakeButton.Focus(FocusState.Programmatic);
        // Hide everything under the black overlay, so neither keys nor a screen reader can reach
        // it; only the wake button remains. It is shown again exactly as it was on wake.
        _hiddenWhileBlank.Clear();
        foreach (var child in Root.Children)
        {
            if (child == ScreenBlankOverlay || child == ScreenDimLayer || child.Visibility != Visibility.Visible) continue;
            _hiddenWhileBlank.Add(child);
            child.Visibility = Visibility.Collapsed;
        }
        // Check every second while blank so a touch or key wakes it promptly.
        if (_screenTimer is not null) _screenTimer.Interval = TimeSpan.FromSeconds(1);
        // An earlier blank's monitor (say, unplugged while dark) is still waiting for its level:
        // keep that until it is put back, and use only the overlay this time.
        if (_brightnessBeforeBlank is not null) return;
        var monitor = MonitorBrightness.MonitorOf(_windowHandle);
        var identity = DisplayPlacement.PreferenceFor(CurrentDisplay());
        // The black overlay always shows; the backlight is turned down only when its original
        // level is safely saved, so a crash can never leave it dark with nothing to restore.
        var before = await MonitorBrightness.GetAsync(monitor);
        // Moved to another monitor during the read: that reading is not this monitor's. The saved
        // identity must also find exactly this monitor again, or it could never be put back.
        if (MonitorBrightness.MonitorOf(_windowHandle) != monitor || !IdentifiesMonitor(identity, monitor)) before = null;
        if (before is not null && await RememberOriginalBrightnessAsync(identity, before.Value) &&
            // Still blank and full screen after the waits (F11, a touch or a key may have come in).
            _screenBlank && !_wakeRequested && StillIdle() && _isFullScreen && MonitorBrightness.MonitorOf(_windowHandle) == monitor)
        {
            _brightnessBeforeBlank = before;
            _blankedMonitor = identity;
            _blankHandle = monitor;
            // A refused write is retried each tick while blank, so the backlight still goes off.
            _blankWritePending = !await MonitorBrightness.SetAsync(monitor, 0);
        }
    }

    /// <summary>
    /// Whether the idle timeout still holds right now. Input anywhere on the PC (another app or
    /// monitor) resets it without passing through EdgeDock, so it is checked just before writing.
    /// </summary>
    private bool StillIdle() => ScreenSchedule.Evaluate(_screenSettings, DateTime.Now, IdleTime()).Blank;

    private readonly List<UIElement> _hiddenWhileBlank = [];
    // A wake gesture that arrived while screen work was busy, handled as soon as it finishes.
    private bool _wakeDropped;

    // Set by any wake gesture straight away, even while a blank is still talking to the monitor,
    // so a blank in progress never turns the backlight off after the user asked to wake.
    private bool _wakeRequested;
    private bool _blankWritePending;
    private IntPtr _blankHandle;

    private async Task RetryBlankWriteAsync()
    {
        if (_wakeRequested || !StillIdle() || !_isFullScreen || MonitorBrightness.MonitorOf(_windowHandle) != _blankHandle) return;
        _blankWritePending = !await MonitorBrightness.SetAsync(_blankHandle, 0) && !_wakeRequested;
    }

    private async Task WakeScreenAsync(int? scheduled)
    {
        _screenBlank = false;
        _blankWritePending = false;
        foreach (var child in _hiddenWhileBlank) child.Visibility = Visibility.Visible;
        _hiddenWhileBlank.Clear();
        ScreenBlankOverlay.Visibility = Visibility.Collapsed;
        if (_focusBeforeBlank is { } previous && previous != ScreenWakeButton) previous.Focus(FocusState.Programmatic);
        _focusBeforeBlank = null;
        if (_screenTimer is not null) _screenTimer.Interval = TimeSpan.FromSeconds(5);
        var applied = true;
        if (scheduled is not null && scheduled != _appliedScheduleBrightness)
        {
            // Day turned to night (or back) while blank: the schedule's new level wins, but the
            // pre-blank level is dropped only once the new one is written; if the write fails it
            // is restored instead (and retried every tick), so the screen never stays at zero.
            var (done, onMonitor) = await ApplyBrightnessAsync(scheduled.Value);
            applied = done;
            if (onMonitor)
            {
                _brightnessBeforeBlank = null;
                _blankedMonitor = null;
            }
            else await RestoreBlankedBrightnessAsync();
        }
        else
        {
            // Otherwise back to the level in use just before blanking, which may be a
            // monitor-button change made since the schedule last applied.
            await RestoreBlankedBrightnessAsync();
        }
        if (applied) _appliedScheduleBrightness = scheduled;
        else _appliedScheduleBrightness = null;
        // Waking outside full screen, or with the schedule off: no software dimming either.
        if (scheduled is null) ScreenDimLayer.Opacity = 0;
    }

    /// <summary>Restores the pre-blank level, only to the blanked monitor (retried if it is away).</summary>
    private async Task RestoreBlankedBrightnessAsync()
    {
        if (_brightnessBeforeBlank is not { } before) return;
        if (await SetOnMonitorAsync(_blankedMonitor, before))
        {
            _brightnessBeforeBlank = null;
            _blankedMonitor = null;
        }
    }

    /// <summary>Puts the monitor back to its level from before EdgeDock first changed it.</summary>
    private async Task<bool> RestoreOriginalBrightnessAsync()
    {
        if (_brightnessRestore is not { } restore) return true;
        if (!await SetOnMonitorAsync(restore.Monitor, restore.Brightness)) return false;
        _brightnessRestore = null;
        // If the removal is not saved, keep trying: a stale restore point on disk would
        // otherwise overwrite a later change made on the monitor's buttons at next launch.
        _restoreClearUnsaved = !await PersistBrightnessRestoreAsync();
        return true;
    }

    /// <summary>
    /// Saves the monitor's level the first time EdgeDock changes it. True only when it is safe to
    /// change <paramref name="monitor"/>: its restore point is saved on disk. One restore point
    /// at a time: another monitor still waiting to be put back must be restored first, so its
    /// original level is never lost.
    /// </summary>
    private async Task<bool> RememberOriginalBrightnessAsync(DisplayPreference? monitor, int brightness)
    {
        if (monitor is null) return false;
        if (_brightnessRestore is { } pending)
        {
            if (SameMonitor(pending.Monitor, monitor)) return true;
            if (!await RestoreOriginalBrightnessAsync() || _brightnessRestore is not null) return false;
        }
        _brightnessRestore = new BrightnessRestore(monitor, brightness);
        if (await PersistBrightnessRestoreAsync())
        {
            _restoreClearUnsaved = false;
            return true;
        }
        _brightnessRestore = null; // Not saved: leave the monitor alone and try again later.
        return false;
    }

    /// <summary>
    /// Writes brightness to one specific monitor, found by the same matcher used for the saved
    /// screen (device path, then model, then a unique resolution, never a guess). False if that
    /// monitor is not connected or refuses.
    /// </summary>
    private static async Task<bool> SetOnMonitorAsync(DisplayPreference? monitor, int brightness)
    {
        if (monitor is null) return false;
        var displays = ConnectedDisplays();
        var index = DisplayPlacement.FindPreferred(displays.Select(display => display.Info).ToArray(), monitor);
        if (index < 0) return false;
        return await MonitorBrightness.SetAsync(Win32Interop.GetMonitorFromDisplayId(displays[index].Area.DisplayId), brightness);
    }

    /// <summary>
    /// Sets the scheduled level. OnMonitor when the monitor itself now has it. Done is false when the monitor answered but the change could not be
    /// made safely (restore point not saved, or the write refused), so the caller retries; a
    /// monitor without DDC/CI gets software dimming and counts as done.
    /// </summary>
    /// <summary>
    /// Whether a saved identity leads back to exactly this monitor, using the same matcher as a
    /// restore. An ambiguous identity (for example two same-size screens without device paths)
    /// could never be restored, so such a monitor is left alone.
    /// </summary>
    private static bool IdentifiesMonitor(DisplayPreference? identity, IntPtr monitor) =>
        monitor != IntPtr.Zero && ResolveMonitor(identity) == monitor;

    /// <summary>The connected monitor a saved identity leads to, or zero if none or ambiguous.</summary>
    private static IntPtr ResolveMonitor(DisplayPreference? identity)
    {
        if (identity is null) return IntPtr.Zero;
        var displays = ConnectedDisplays();
        var index = DisplayPlacement.FindPreferred(displays.Select(display => display.Info).ToArray(), identity);
        return index < 0 ? IntPtr.Zero : Win32Interop.GetMonitorFromDisplayId(displays[index].Area.DisplayId);
    }

    /// <summary>
    /// Whether two saved identities are the same physical monitor, by the restore matcher rather
    /// than exact equality (resolution and orientation can change while it stays the same screen).
    /// </summary>
    private static bool SameMonitor(DisplayPreference? first, DisplayPreference? second)
    {
        if (first is null || second is null) return false;
        if (first == second) return true;
        var resolved = ResolveMonitor(first);
        return resolved != IntPtr.Zero && resolved == ResolveMonitor(second);
    }

    private async Task<(bool Done, bool OnMonitor)> ApplyBrightnessAsync(int brightness)
    {
        // Take the monitor's handle and identity together, before the slow read; if the window
        // moved to another monitor meanwhile, the reading belongs to the old one and is not used.
        var monitor = MonitorBrightness.MonitorOf(_windowHandle);
        var identity = DisplayPlacement.PreferenceFor(CurrentDisplay());
        // This monitor answered DDC/CI moments ago (before blanking), so a failed read now is a
        // hiccup to retry, not a monitor without DDC/CI.
        // Likewise when EdgeDock has already changed this monitor (its restore point is pending).
        var done = _brightnessBeforeBlank is null && !SameMonitor(_brightnessRestore?.Monitor, identity);
        // A different monitor EdgeDock changed earlier goes back first, whether or not this one
        // answers DDC/CI; if it cannot yet (unplugged), try again next tick.
        if (_brightnessRestore is { } pending && !SameMonitor(pending.Monitor, identity) &&
            (!await RestoreOriginalBrightnessAsync() || _brightnessRestore is not null))
            done = false;
        var current = await MonitorBrightness.GetAsync(monitor);
        if (current is not null && MonitorBrightness.MonitorOf(_windowHandle) != monitor)
        {
            current = null;
            done = false; // Moved mid-read: try again on the new monitor.
        }
        else if (current is not null && !IdentifiesMonitor(identity, monitor))
            current = null; // Could never be put back: software dimming only, for good.
        if (current is not null)
        {
            if (await RememberOriginalBrightnessAsync(identity, current.Value) &&
                // Still full screen on the same monitor after the waits.
                _isFullScreen && MonitorBrightness.MonitorOf(_windowHandle) == monitor &&
                await MonitorBrightness.SetAsync(monitor, brightness))
            {
                ScreenDimLayer.Opacity = 0;
                return (true, true);
            }
            done = false;
        }
        // No DDC/CI on this monitor or cable (or not safe to change it yet): darken EdgeDock itself.
        ScreenDimLayer.Opacity = Math.Clamp((100 - brightness) / 100.0, 0, 0.85);
        return (done, false);
    }

    /// <summary>Saves only the restore point, merged onto the last saved settings. False if not saved.</summary>
    private async Task<bool> PersistBrightnessRestoreAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            var merged = (_savedSettings ?? CurrentSettings()) with { PendingBrightness = _brightnessRestore };
            await _settings.SaveAsync(merged);
            _savedSettings = SettingsStore.Normalize(merged);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        finally { _saveGate.Release(); }
    }

    private Control? _focusBeforeBlank;

    private async void ScreenBlankOverlay_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Any key only wakes the screen; nothing underneath sees it.
        args.Handled = true;
        _wakeRequested = true;
        if (_screenBusy) _wakeDropped = true;
        await UpdateScreenAsync();
    }

    private async void ScreenWakeButton_Click(object sender, RoutedEventArgs args)
    {
        _wakeRequested = true;
        if (_screenBusy) _wakeDropped = true;
        await UpdateScreenAsync();
    }

    private async void ScreenBlankOverlay_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        // The tap only wakes the screen; it does not reach the widget underneath. It also
        // resets Windows' idle time, so the normal update path wakes the screen.
        args.Handled = true;
        _wakeRequested = true;
        if (_screenBusy) _wakeDropped = true;
        await UpdateScreenAsync();
    }

    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    private void SetFullScreen(bool fullScreen)
    {
        CloseControlsDrawer();
        if (fullScreen == _isFullScreen) return;
        if (!fullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _isFullScreen = false;
            AppTitleBar.Visibility = Visibility.Visible;
            FullScreenIcon.Symbol = Symbol.FullScreen;
            FullScreenText.Text = "Full screen";
            AutomationProperties.SetName(FullScreenButton, "Enter full screen");
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _isFullScreen = true;
            AppTitleBar.Visibility = Visibility.Collapsed;
            FullScreenIcon.Symbol = Symbol.BackToWindow;
            FullScreenText.Text = "Exit full screen";
            AutomationProperties.SetName(FullScreenButton, "Exit full screen");
        }
        ApplyFullscreenLayout();
    }

    /// <summary>Brings the window forward when EdgeDock is launched again while running.</summary>
    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(_windowHandle);
    }

    private void RestorePreferredDisplay()
    {
        if (_displayPreference is null) return;
        if (!MoveToPreferredDisplay())
        {
            // The screen may still be waking up at sign-in; the display watcher finishes the job.
            _awaitingPreferredDisplay = true;
            return;
        }
        if (_openFullScreen) SetFullScreen(true);
    }

    private void StartDisplayTracking()
    {
        // Treat launch like a display change: early moves are Windows placing the window.
        _lastDisplayChange = DateTimeOffset.Now;
        _displayChangeTimer = DispatcherQueue.CreateTimer();
        _displayChangeTimer.Interval = TimeSpan.FromMilliseconds(800);
        _displayChangeTimer.IsRepeating = false;
        _displayChangeTimer.Tick += (_, _) => OnDisplaysChanged();
        _windowMoveTimer = DispatcherQueue.CreateTimer();
        _windowMoveTimer.Interval = TimeSpan.FromSeconds(1.5);
        _windowMoveTimer.IsRepeating = false;
        _windowMoveTimer.Tick += async (_, _) => await RememberMovedWindowAsync();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;

        _displayWatcher = DisplayArea.CreateWatcher();
        _displayWatcher.Added += DisplayWatcher_Changed;
        _displayWatcher.Removed += DisplayWatcher_Changed;
        _displayWatcher.Updated += DisplayWatcher_Changed;
        _displayWatcher.Start();
        // First run, or settings from before screens were remembered: record where the window
        // opened once placement settles, so the next launch returns to the same screen.
        if (_displayPreference is null) _windowMoveTimer.Start();
    }

    // Watcher events arrive on a background thread, often several per change; settle them first.
    private void DisplayWatcher_Changed(DisplayAreaWatcher sender, DisplayArea args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            _lastDisplayChange = DateTimeOffset.Now;
            _displayChangeTimer?.Start();
        });

    private void OnDisplaysChanged()
    {
        if (_closing || _displayPreference is null) return;
        var displays = ConnectedDisplays();
        if (DisplayPlacement.FindPreferred(displays.Select(display => display.Info).ToArray(), _displayPreference) < 0)
        {
            // Windows moves a full-screen window to another monitor when its screen goes away.
            // Step aside instead of covering the main screen, and come back when it returns.
            if (_isFullScreen && !_minimizedForMissingDisplay)
            {
                _minimizedForMissingDisplay = true;
                SetFullScreen(false);
                (AppWindow.Presenter as OverlappedPresenter)?.Minimize();
            }
            else if (!_isFullScreen && !_minimizedForMissingDisplay)
            {
                // Windowed: Windows moves the window to another screen. Leave it usable there,
                // but move it back when its screen returns, unless the user picks a new one.
                _awaitingPreferredDisplay = true;
            }
            return;
        }

        if (_minimizedForMissingDisplay || _awaitingPreferredDisplay)
        {
            var restoreFullScreen = _openFullScreen;
            _minimizedForMissingDisplay = false;
            _awaitingPreferredDisplay = false;
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            MoveToPreferredDisplay();
            if (restoreFullScreen) SetFullScreen(true);
        }
        else if (_isFullScreen)
        {
            // A resolution or arrangement change can shuffle windows between monitors.
            MoveToPreferredDisplay();
        }
    }

    private bool MoveToPreferredDisplay()
    {
        var displays = ConnectedDisplays();
        var index = DisplayPlacement.FindPreferred(displays.Select(display => display.Info).ToArray(), _displayPreference);
        if (index < 0) return false;

        var target = displays[index].Area;
        var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (current?.DisplayId.Value == target.DisplayId.Value) return true;

        var wasFullScreen = _isFullScreen;
        if (wasFullScreen) AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        var work = target.WorkArea;
        var width = Math.Min(AppWindow.Size.Width, work.Width);
        var height = Math.Min(AppWindow.Size.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        // The full-screen presenter fills whichever monitor the window is on.
        if (wasFullScreen) AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        return true;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        // Restoring puts the window wherever Windows last had it; like a display change,
        // that placement is not a choice of screen, but a drag after it is.
        if (_wasMinimized && !minimized) _lastDisplayChange = DateTimeOffset.Now;
        _wasMinimized = minimized;
        if (args.DidPositionChange && !_closing) _windowMoveTimer?.Start();
    }

    /// <summary>Dragging the window to another screen makes that screen the one to reopen on.</summary>
    private async Task RememberMovedWindowAsync()
    {
        if (_closing || !CaptureMovedDisplay()) return;
        await PersistDisplayAsync();
    }

    /// <summary>Updates the remembered screen from the window's position; true if it changed.</summary>
    private bool CaptureMovedDisplay()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return false;
        // When the saved screen disconnects, Windows moves the window elsewhere straight away.
        // That is not the user choosing a new screen, so keep the old one to return to. A move
        // made later, while it is still missing, is a deliberate choice and replaces it.
        if (_displayPreference is not null &&
            DateTimeOffset.Now - _lastDisplayChange < TimeSpan.FromSeconds(10) &&
            DisplayPlacement.FindPreferred(ConnectedDisplays().Select(display => display.Info).ToArray(), _displayPreference) < 0)
            return false;
        var preference = DisplayPlacement.PreferenceFor(CurrentDisplay());
        if (preference is null || preference == _displayPreference) return false;
        _displayPreference = preference;
        _awaitingPreferredDisplay = false;
        // The user chose a new screen after stepping aside; stop waiting for the old one.
        _minimizedForMissingDisplay = false;
        return true;
    }

    /// <summary>
    /// Closing straight after a drag, or while its save is still writing, would otherwise lose
    /// the move. Hold the close, finish the save normally on this thread (a blocking wait could stall behind another save
    /// that needs this dispatcher), then close for real.
    /// </summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeReady) return;
        var movePending = _windowMoveTimer?.IsRunning == true;
        var screenPending = _screenBlank || _screenBusy || _brightnessBeforeBlank is not null || _brightnessRestore is not null || _restoreClearUnsaved;
        if (!_closeAfterPendingSave && !movePending && _displaySaves.IsCompleted && !screenPending) return;
        args.Cancel = true;
        // Already waiting: further close clicks must not end the process mid-save.
        if (_closeAfterPendingSave) return;
        _closeAfterPendingSave = true;
        if (movePending)
        {
            _windowMoveTimer!.Stop();
            if (CaptureMovedDisplay()) _ = PersistDisplayAsync();
        }
        // Covers saves started by the timer that are still waiting for the gate or the disk,
        // and any started while waiting (the window stays usable, so F11 can add one).
        Task pending;
        do
        {
            pending = _displaySaves;
            try { await pending; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        while (!ReferenceEquals(pending, _displaySaves));
        await RestoreScreenBeforeCloseAsync();
        _closeReady = true;
        Close();
    }

    /// <summary>
    /// Never leave a monitor dark or dimmed behind a closed EdgeDock. If the monitor is not
    /// connected, its restore point stays saved and the next launch restores it.
    /// </summary>
    private async Task RestoreScreenBeforeCloseAsync()
    {
        _screenTimer?.Stop();
        // Let a blank or wake that is already talking to the monitor finish completely first;
        // no new one starts once closing has begun.
        await _screenWork;
        _screenBlank = false;
        _brightnessBeforeBlank = null;
        _blankedMonitor = null;
        await RestoreOriginalBrightnessAsync();
        if (_restoreClearUnsaved && _brightnessRestore is null) await PersistBrightnessRestoreAsync();
    }

    private DisplayInfo? CurrentDisplay()
    {
        var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        return current is null ? null : DescribeDisplay(current);
    }

    private static List<(DisplayArea Area, DisplayInfo Info)> ConnectedDisplays()
    {
        var displays = new List<(DisplayArea, DisplayInfo)>();
        var areas = DisplayArea.FindAll();
        // Index rather than foreach: enumerating this projected list is unreliable.
        for (var index = 0; index < areas.Count; index++)
            displays.Add((areas[index], DescribeDisplay(areas[index])));
        return displays;
    }

    private static DisplayInfo DescribeDisplay(DisplayArea area) =>
        new(MonitorDeviceId(area) ?? string.Empty, area.OuterBounds.Width, area.OuterBounds.Height);

    /// <summary>The monitor's device interface path, which stays the same across restarts.</summary>
    private static string? MonitorDeviceId(DisplayArea area)
    {
        var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(info.DeviceName, 0, ref device, GetDeviceInterfaceName) &&
               !string.IsNullOrWhiteSpace(device.DeviceId)
            ? device.DeviceId
            : null;
    }

    private void ApplyFullscreenLayout()
    {
        Workspace.Padding = _isFullScreen ? new Thickness(0) : new Thickness(10);
        WebSurface.CornerRadius = new CornerRadius(_isFullScreen ? 0 : 14);
        if (_webClipGeometry is not null)
            _webClipGeometry.CornerRadius = new Vector2(_isFullScreen ? 0 : 14);
        foreach (var slot in WidgetHost.Children.OfType<WidgetSlotView>())
            slot.SetEdgeToEdge(_isFullScreen);
        foreach (var card in WidgetGalleryRow.Children.OfType<Grid>())
            if (card.Children.FirstOrDefault() is DependencyObject widget)
                WidgetSlotView.SetWidgetEdgeToEdge(widget, _isFullScreen);
        ApplyMediaLayout();
        ApplyWebSplitLayout();
        WebSurface_LayoutChanged(WebSurface, new RoutedEventArgs());
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyMediaLayout();
        ApplyWebSplitLayout();
        if (_isFullScreen || _enforcingMinimumSize ||
            (args.NewSize.Width >= MinimumWidth && args.NewSize.Height >= MinimumHeight))
        {
            return;
        }

        _enforcingMinimumSize = true;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Ceiling(Math.Max(args.NewSize.Width, MinimumWidth) * scale),
            (int)Math.Ceiling(Math.Max(args.NewSize.Height, MinimumHeight) * scale)));
        DispatcherQueue.TryEnqueue(() => _enforcingMinimumSize = false);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _screenTimer?.Stop();
        _closing = true;
        _displayChangeTimer?.Stop();
        _windowMoveTimer?.Stop();
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        if (_displayWatcher is not null)
        {
            _displayWatcher.Added -= DisplayWatcher_Changed;
            _displayWatcher.Removed -= DisplayWatcher_Changed;
            _displayWatcher.Updated -= DisplayWatcher_Changed;
            if (_displayWatcher.Status is DisplayAreaWatcherStatus.Started or DisplayAreaWatcherStatus.EnumerationCompleted)
                _displayWatcher.Stop();
            _displayWatcher = null;
        }
        PowerManager.SystemSuspendStatusChanged -= PowerManager_SystemSuspendStatusChanged;
        _resumeReloadTimer?.Stop();
        WidgetGalleryRow.Children.Clear();
        WidgetHost.Children.Clear();
        _media.Dispose();
        foreach (var panel in _webPanels) panel.Dispose();
        _webPanels.Clear();

        if (_messageHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_messageHook);
            _messageHook = IntPtr.Zero;
        }
    }

    private const int GetMessageHook = 3;
    private const int RemoveMessage = 1;
    private const uint NullMessage = 0x0000;
    private const uint WindowKeyDown = 0x0100;
    private const uint WindowSystemKeyDown = 0x0104;
    private const uint GetDeviceInterfaceName = 0x00000001;

    private delegate IntPtr GetMessageHookProc(int code, IntPtr removeMessage, IntPtr messagePointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr VirtualKey;
        public IntPtr KeyStatus;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, GetMessageHookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr removeMessage, IntPtr messagePointer);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr candidate);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}

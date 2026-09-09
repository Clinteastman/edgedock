using EdgeDock.Controls;
using EdgeDock.Widgets;
using Microsoft.UI;
using Microsoft.UI.Composition;
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
    private Uri? _dashboardUri;
    private IntPtr _windowHandle;
    private IntPtr _messageHook;
    private MediaPanelPlacement _mediaPlacement = MediaPanelPlacement.Right;
    private MediaPanelPlacement _lastVisiblePlacement = MediaPanelPlacement.Right;
    private double _configuredPanelWidth = 340;
    private bool _showArtwork = true;
    private bool _isFullScreen;
    private bool _enforcingMinimumSize;
    private bool _keyTransitionPending;
    private bool _isWebVisible = true;
    private IReadOnlyList<WidgetSlotSettings> _widgetSlots = [new(["media"], "media")];
    private readonly WidgetRegistry _registry = WidgetRegistry.CreateBuiltIns();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _closing;
    private WidgetSlotView? _menuSlot;
    private CompositionRoundedRectangleGeometry? _webClipGeometry;

    public MainWindow()
    {
        _messageHookCallback = MessageHookCallback;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop
        {
            Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt
        };
        ConfigureWindow();
        InstallMessageHook();
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
        _mediaPlacement = settings.MediaPanelPlacement;
        _lastVisiblePlacement = settings.LastVisibleMediaPanelPlacement;
        _configuredPanelWidth = settings.MediaPanelWidth;
        _showArtwork = settings.ShowArtwork;
        _isWebVisible = settings.IsWebVisible;
        _widgetSlots = settings.WidgetSlots;
        BuildWidgetSlots();
        ApplyMediaLayout();
        UpdateSettingsControls();

        if (settings.DashboardUrl is null)
        {
            ShowSetup();
            return;
        }

        UrlTextBox.Text = settings.DashboardUrl;
        _dashboardUri = new Uri(settings.DashboardUrl);
        if (_isWebVisible) await NavigateAsync(_dashboardUri);
    }

    private async Task EnsureWebViewAsync()
    {
        if (DashboardWebView.CoreWebView2 is not null)
        {
            return;
        }

        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _settings.WebViewProfilePath, null);
        await DashboardWebView.EnsureCoreWebView2Async(environment);
        var core = DashboardWebView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 initialization completed without a core instance.");
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        DashboardWebView.CoreProcessFailed += DashboardWebView_CoreProcessFailed;
    }

    private async Task NavigateAsync(Uri uri)
    {
        _dashboardUri = uri;
        SetupState.Visibility = Visibility.Collapsed;
        ShowWebStatus("Opening dashboard", uri.Host, true, false);

        try
        {
            await EnsureWebViewAsync();
            DashboardWebView.Source = uri;
            ReloadButton.IsEnabled = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            ShowWebStatus(
                "Dashboard could not open",
                "Check that the Microsoft Edge WebView2 Runtime is installed, then try again.",
                false,
                true);
        }
    }

    private void DashboardWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!SettingsStore.IsAllowedUrl(args.Uri, out _))
        {
            args.Cancel = true;
            ShowWebStatus("Link blocked", "EdgeDock only opens http and https web addresses.", false, true);
            return;
        }

        ShowWebStatus(
            "Loading dashboard",
            Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.Host : string.Empty,
            true,
            false);
    }

    private void DashboardWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            WebStatusState.Visibility = Visibility.Collapsed;
            return;
        }

        ShowWebStatus(
            "Dashboard did not load",
            $"WebView reported {args.WebErrorStatus}. Check the address and connection, then try again.",
            false,
            true);
    }

    private void DashboardWebView_CoreProcessFailed(WebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => ShowWebStatus(
            "Dashboard process stopped",
            "Reload the page. Your saved address and sign-in have not been removed.",
            false,
            true));
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

    private void ShowSetup()
    {
        _dashboardUri = null;
        SetupState.Visibility = Visibility.Visible;
        WebStatusState.Visibility = Visibility.Collapsed;
        ReloadButton.IsEnabled = false;
    }

    private void ShowWebStatus(string title, string message, bool loading, bool retry)
    {
        WebStatusTitle.Text = title;
        WebStatusMessage.Text = message;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        WebStatusState.Visibility = Visibility.Visible;
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
        AppControlsFlyout.Hide();
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        UrlTextBox.Text = _dashboardUri?.AbsoluteUri ?? UrlTextBox.Text;
        UrlErrorText.Visibility = Visibility.Collapsed;
        UpdateSettingsControls();
        SettingsPanel.Visibility = Visibility.Visible;
        UrlTextBox.Focus(FocusState.Programmatic);
        UrlTextBox.SelectAll();
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs args)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        UrlErrorText.Visibility = Visibility.Collapsed;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs args) => await SaveAndNavigateAsync();

    private async void UrlTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await SaveAndNavigateAsync();
        }
    }

    private async Task SaveAndNavigateAsync()
    {
        Uri? uri = null;
        if (!string.IsNullOrWhiteSpace(UrlTextBox.Text) && !SettingsStore.IsAllowedUrl(UrlTextBox.Text, out uri))
        {
            UrlErrorText.Text = "Enter a complete http or https web address, or leave it empty.";
            UrlErrorText.Visibility = Visibility.Visible;
            return;
        }
        var placement = PlacementComboBox.SelectedIndex switch
        {
            1 => MediaPanelPlacement.Left,
            2 => MediaPanelPlacement.Hidden,
            _ => MediaPanelPlacement.Right
        };
        var settings = SettingsStore.Normalize(new EdgeDockSettings(uri?.AbsoluteUri, placement,
            placement == MediaPanelPlacement.Hidden ? _lastVisiblePlacement : placement,
            PanelWidthSlider.Value, ArtworkToggle.IsOn, WebVisibleToggle.IsOn, LibraryEditor.GetSlots()));
        if (!await PersistAsync(settings)) return;
        _mediaPlacement = settings.MediaPanelPlacement;
        _lastVisiblePlacement = settings.LastVisibleMediaPanelPlacement;
        _configuredPanelWidth = settings.MediaPanelWidth;
        _showArtwork = settings.ShowArtwork;
        _isWebVisible = settings.IsWebVisible;
        _widgetSlots = settings.WidgetSlots;
        var previousUri = _dashboardUri;
        _dashboardUri = uri;
        BuildWidgetSlots();
        ApplyMediaLayout();
        SettingsPanel.Visibility = Visibility.Collapsed;
        UrlErrorText.Visibility = Visibility.Collapsed;
        if (_dashboardUri is null) ShowSetup();
        else if (_isWebVisible && (previousUri != uri || DashboardWebView.CoreWebView2 is null)) await NavigateAsync(_dashboardUri);
    }

    private async Task<bool> PersistAsync(EdgeDockSettings settings, bool alreadyApplied = false)
    {
        await _saveGate.WaitAsync();
        try { await _settings.SaveAsync(settings); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!_closing)
            {
                SettingsPanel.Visibility = Visibility.Visible;
                UrlErrorText.Text = alreadyApplied
                    ? "Your change is active but was not saved. It may reset when EdgeDock closes. Check access to local app data."
                    : "Could not save your layout. Check access to the local app data folder.";
                UrlErrorText.Visibility = Visibility.Visible;
            }
            return false;
        }
        finally { _saveGate.Release(); }
    }

    private async void ToggleMediaPanel_Click(object sender, RoutedEventArgs args)
    {
        AppControlsFlyout.Hide();
        if (!_isWebVisible) return;
        if (_mediaPlacement == MediaPanelPlacement.Hidden) _mediaPlacement = _lastVisiblePlacement;
        else { _lastVisiblePlacement = _mediaPlacement; _mediaPlacement = MediaPanelPlacement.Hidden; }
        ApplyMediaLayout();
        await PersistAsync(CurrentSettings(), alreadyApplied: true);
    }

    private EdgeDockSettings CurrentSettings() => new(
        _dashboardUri?.AbsoluteUri,
        _mediaPlacement,
        _lastVisiblePlacement,
        _configuredPanelWidth,
        _showArtwork,
        _isWebVisible,
        _widgetSlots);

    private void UpdateSettingsControls()
    {
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
        LibraryEditor.LoadSlots(_widgetSlots, _registry);
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
        _menuSlot?.SetHeaderActions(null);
        _menuSlot = null;
        WidgetHost.Children.Clear();
        WidgetHost.ColumnDefinitions.Clear();
        for (var index = 0; index < _widgetSlots.Count; index++)
        {
            var slotIndex = index;
            WidgetHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var slot = new WidgetSlotView(_registry, _media, _widgetSlots[index], _showArtwork);
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
        var widgetsVisible = _mediaPlacement != MediaPanelPlacement.Hidden || !_isWebVisible;
        WidgetHost.Visibility = widgetsVisible ? Visibility.Visible : Visibility.Collapsed;
        WebSurface.Visibility = _isWebVisible ? Visibility.Visible : Visibility.Collapsed;
        var available = Math.Max(800, Root.ActualWidth - 20);
        var requested = _configuredPanelWidth * _widgetSlots.Count + 10 * (_widgetSlots.Count - 1);
        var minimumWidgetsWidth = 240 * _widgetSlots.Count + 10 * (_widgetSlots.Count - 1);
        var width = Math.Max(minimumWidgetsWidth, Math.Min(requested, Math.Max(240, available - 360)));
        var widgetWidth = !_isWebVisible ? new GridLength(1, GridUnitType.Star) : widgetsVisible ? new GridLength(width) : new GridLength(0);
        var webWidth = _isWebVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        var left = _mediaPlacement == MediaPanelPlacement.Left;
        Grid.SetColumn(WidgetHost, left ? 0 : 1);
        Grid.SetColumn(WebSurface, left ? 1 : 0);
        WebColumn.Width = left ? widgetWidth : webWidth;
        MediaColumn.Width = left ? webWidth : widgetWidth;
        Workspace.ColumnSpacing = _isWebVisible && widgetsVisible ? 10 : 0;
        MediaVisibilityButton.IsEnabled = _isWebVisible;
        var label = widgetsVisible ? "Hide widgets" : "Show widgets";
        AutomationProperties.SetName(MediaVisibilityButton, label);
        ToolTipService.SetToolTip(MediaVisibilityButton, _isWebVisible ? label : "Enable the web dashboard before hiding widgets");
        WidgetVisibilityText.Text = label;
        MovePanelMenu(widgetsVisible);
    }

    private void MovePanelMenu(bool widgetsVisible)
    {
        var target = widgetsVisible ? WidgetHost.Children.OfType<WidgetSlotView>().FirstOrDefault() : null;
        if (_menuSlot != target || (target is not null && WebHeaderHost.Content is not null))
        {
            _menuSlot?.SetHeaderActions(null);
            WebHeaderHost.Content = null;
            _menuSlot = target;
            if (target is not null) target.SetHeaderActions(PanelMenuButton);
            else WebHeaderHost.Content = PanelMenuButton;
        }
        else if (target is null && WebHeaderHost.Content is null)
            WebHeaderHost.Content = PanelMenuButton;
        WebHeaderHost.Visibility = target is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reload_Click(object sender, RoutedEventArgs args)
    {
        if (DashboardWebView.CoreWebView2 is not null)
        {
            DashboardWebView.Reload();
        }
        else if (_dashboardUri is not null)
        {
            _ = NavigateAsync(_dashboardUri);
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs args) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        AppControlsFlyout.Hide();
        if (_isFullScreen)
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
    }



    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyMediaLayout();
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
        _closing = true;
        WidgetHost.Children.Clear();
        _media.Dispose();
        if (DashboardWebView.CoreWebView2 is not null)
        {
            DashboardWebView.CoreProcessFailed -= DashboardWebView_CoreProcessFailed;
            DashboardWebView.Close();
        }

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
}

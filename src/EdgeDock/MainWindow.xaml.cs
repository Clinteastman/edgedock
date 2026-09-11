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
    private readonly WidgetRegistry _registry = WidgetRegistry.CreateBuiltIns();
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

    public MainWindow()
    {
        _messageHookCallback = MessageHookCallback;
        InitializeComponent();
        RegisterDividerHandlers(WebDivider);
        RegisterDividerHandlers(GroupDivider);
        ApplyBackdrop();
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
                WebHost.Children.Remove(last);
                _webPanels.RemoveAt(_webPanels.Count - 1);
                last.Dispose();
            }
            while (_webPanels.Count < visibleCount)
            {
                var panel = new WebPanelView(BuildWebPanelsAsync);
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
            _configuredWebSplitRatio));
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
        _configuredWebSplitRatio);

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
            slots.Add(new WidgetSlotSettings(["media", "audio", "pc"], "media"));
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

    private void ToggleFullScreen()
    {
        CloseControlsDrawer();
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
        ApplyFullscreenLayout();
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
        _closing = true;
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

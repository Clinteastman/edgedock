using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace EdgeDock;

public sealed partial class MainWindow : Window
{
    private const double MinimumWidth = 900;
    private const double MinimumHeight = 420;
    private readonly SettingsStore _settings = new();
    private readonly MediaSessionService _media = new();
    private readonly GetMessageHookProc _messageHookCallback;
    private Uri? _dashboardUri;
    private IntPtr _windowHandle;
    private IntPtr _messageHook;
    private bool _isFullScreen;
    private bool _enforcingMinimumSize;
    private bool _keyTransitionPending;

    public MainWindow()
    {
        _messageHookCallback = MessageHookCallback;
        InitializeComponent();
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        ConfigureWindow();
        InstallMessageHook();
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
        _media.SnapshotChanged += Media_SnapshotChanged;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await InitializeAsync();
    }

    private void ConfigureWindow()
    {
        AppWindow.ResizeClient(new SizeInt32(1600, 720));
        AppWindow.TitleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 17, 16, 22);
        AppWindow.TitleBar.ForegroundColor = Colors.White;
        AppWindow.TitleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 26, 24, 33);
        AppWindow.TitleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 200, 194, 212);
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 17, 16, 22);
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 52, 43, 72);
    }

    private async Task InitializeAsync()
    {
        _ = _media.InitializeAsync();
        var savedUrl = await _settings.LoadUrlAsync();
        if (savedUrl is null)
        {
            ShowSetup();
            return;
        }

        UrlTextBox.Text = savedUrl;
        await NavigateAsync(new Uri(savedUrl));
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
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowWebStatus("Dashboard could not open", "Check that the Microsoft Edge WebView2 Runtime is installed, then try again.", false, true);
        }
    }

    private void DashboardWebView_NavigationStarting(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!SettingsStore.IsAllowedUrl(args.Uri, out _))
        {
            args.Cancel = true;
            ShowWebStatus("Link blocked", "EdgeDock only opens http and https web addresses.", false, true);
            return;
        }

        ShowWebStatus("Loading dashboard", Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.Host : string.Empty, true, false);
    }

    private void DashboardWebView_NavigationCompleted(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            WebStatusState.Visibility = Visibility.Collapsed;
            return;
        }

        ShowWebStatus("Dashboard did not load", $"WebView reported {args.WebErrorStatus}. Check the address and connection, then try again.", false, true);
    }

    private void DashboardWebView_CoreProcessFailed(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2ProcessFailedEventArgs args)
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

            if (isFromThisWindow && isKeyDown && !isRepeat && !_keyTransitionPending)
            {
                var key = message.VirtualKey.ToUInt64();
                if (key == (uint)VirtualKey.F11 || (key == (uint)VirtualKey.Escape && _isFullScreen))
                {
                    _keyTransitionPending = true;
                    message.Message = NullMessage;
                    Marshal.StructureToPtr(message, messagePointer, false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (key == (uint)VirtualKey.F11)
                        {
                            ToggleFullScreen();
                        }
                        else if (_isFullScreen)
                        {
                            ExitFullScreen();
                        }

                        _keyTransitionPending = false;
                    });
                }
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
        else if (args.Key == VirtualKey.Escape && _isFullScreen)
        {
            args.Handled = true;
            ExitFullScreen();
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

    private void OpenSettings_Click(object sender, RoutedEventArgs args)
    {
        UrlTextBox.Text = _dashboardUri?.AbsoluteUri ?? UrlTextBox.Text;
        UrlErrorText.Visibility = Visibility.Collapsed;
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
        if (!SettingsStore.IsAllowedUrl(UrlTextBox.Text, out var uri))
        {
            UrlErrorText.Text = "Enter a complete http or https web address.";
            UrlErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            await _settings.SaveUrlAsync(uri!.AbsoluteUri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UrlErrorText.Text = "EdgeDock could not save this address. Check access to your local app data folder.";
            UrlErrorText.Visibility = Visibility.Visible;
            return;
        }

        SettingsPanel.Visibility = Visibility.Collapsed;
        UrlErrorText.Visibility = Visibility.Collapsed;
        await NavigateAsync(uri!);
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
    private void ExitFullScreen_Click(object sender, RoutedEventArgs args) => ExitFullScreen();

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
            return;
        }

        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        _isFullScreen = true;
        ExitFullScreenButton.Visibility = Visibility.Visible;
        FullScreenButton.IsEnabled = false;
    }

    private void ExitFullScreen()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        _isFullScreen = false;
        ExitFullScreenButton.Visibility = Visibility.Collapsed;
        FullScreenButton.IsEnabled = true;
    }

    private async void Previous_Click(object sender, RoutedEventArgs args) => await _media.PreviousAsync();
    private async void PlayPause_Click(object sender, RoutedEventArgs args) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs args) => await _media.NextAsync();

    private void Media_SnapshotChanged(object? sender, MediaSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TrackTitle.Text = snapshot.Title;
            TrackArtist.Text = snapshot.Artist;
            PreviousButton.IsEnabled = snapshot.CanGoPrevious;
            PlayPauseButton.IsEnabled = snapshot.CanTogglePlayPause;
            NextButton.IsEnabled = snapshot.CanGoNext;
            PlayPauseIcon.Symbol = snapshot.IsPlaying ? Symbol.Pause : Symbol.Play;
            PlayPauseButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, snapshot.IsPlaying ? "Pause" : "Play");
            ToolTipService.SetToolTip(PlayPauseButton, snapshot.IsPlaying ? "Pause" : "Play");
            MediaStatus.Text = snapshot.StatusMessage ?? string.Empty;
            MediaStatus.Visibility = string.IsNullOrEmpty(snapshot.StatusMessage) ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_isFullScreen || _enforcingMinimumSize || (args.NewSize.Width >= MinimumWidth && args.NewSize.Height >= MinimumHeight))
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
        _media.SnapshotChanged -= Media_SnapshotChanged;
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

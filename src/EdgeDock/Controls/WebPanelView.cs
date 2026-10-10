using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;

namespace EdgeDock.Controls;

internal sealed class WebPanelView : Grid, IDisposable
{
    internal const string HideScrollbarsScript = """
        (() => {
          if (window.__edgeDockScrollbarHider) return;
          window.__edgeDockScrollbarHider = true;
          const css = `:host, html, body { scrollbar-width: none !important; -ms-overflow-style: none !important; }
            * { scrollbar-width: none !important; -ms-overflow-style: none !important; }
            ::-webkit-scrollbar { display: none !important; width: 0 !important; height: 0 !important; }`;
          const watched = new WeakSet();
          const inspect = element => {
            if (element.shadowRoot) watch(element.shadowRoot);
          };
          const discoverSubtree = node => {
            if (!(node instanceof Element)) return;
            inspect(node);
            node.querySelectorAll('*').forEach(inspect);
          };
          function watch(root) {
            if (!root || watched.has(root)) return;
            watched.add(root);
            const style = document.createElement('style');
            style.setAttribute('data-edgedock-scrollbars', 'hidden');
            style.textContent = css;
            root.appendChild(style);
            if (root.querySelectorAll) root.querySelectorAll('*').forEach(inspect);
            new MutationObserver(records => {
              for (const record of records) record.addedNodes.forEach(discoverSubtree);
              if (style.parentNode !== root) root.appendChild(style);
            }).observe(root, { childList: true, subtree: true });
          }
          const originalAttachShadow = Element.prototype.attachShadow;
          Element.prototype.attachShadow = function(init) {
            const root = originalAttachShadow.call(this, init);
            watch(root);
            return root;
          };
          const begin = () => { if (document.documentElement) watch(document.documentElement); };
          begin();
          if (!document.documentElement) {
            const documentObserver = new MutationObserver(() => {
              if (!document.documentElement) return;
              documentObserver.disconnect();
              watch(document.documentElement);
            });
            documentObserver.observe(document, { childList: true });
          }
        })();
        """;

    private readonly WebView2 _webView = new()
    {
        DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 11, 10, 14),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly Grid _setup = new() { Visibility = Visibility.Collapsed };
    private readonly Grid _status = new()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(230, 17, 16, 22)),
        Visibility = Visibility.Collapsed
    };
    private readonly ProgressRing _loading = new() { Width = 42, Height = 42 };
    private readonly TextBlock _statusTitle = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _statusMessage = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Button _retry = new() { Content = "Try again", HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private Uri? _uri;
    private readonly Func<Task>? _retryInitialization;
    private readonly RecoveryBudget _pageRecoveries = new(3, TimeSpan.FromMinutes(10));
    private DispatcherQueueTimer? _retryTimer;
    private int _retryAttempt;
    private bool _initialized;
    private bool _browserFailed;
    private bool _blanking;
    private bool _disposed;
    private double _zoom = 1.0;
    private bool _zoomApplied;
    private bool _zoomRunning;
    private bool _zoomPending;
    private DispatcherQueueTimer? _zoomTimer;

    internal WebPanelView(Func<Task>? retryInitialization = null)
    {
        _retryInitialization = retryInitialization;
        AutomationProperties.SetName(_webView, "Web dashboard");
        Children.Add(_webView);
        BuildSetup();
        BuildStatus();
        Children.Add(_setup);
        Children.Add(_status);
        _retry.Click += async (_, _) =>
        {
            if (_browserFailed) BrowserProcessFailed?.Invoke(this, true);
            else if (_webView.CoreWebView2 is not null) Reload();
            else if (_retryInitialization is not null) await _retryInitialization();
        };
        _webView.NavigationStarting += NavigationStarting;
        _webView.NavigationCompleted += NavigationCompleted;
        _webView.SizeChanged += WebView_SizeChanged;
    }

    internal string? CardId { get; private set; }
    internal WebView2 WebView => _webView;

    /// <summary>
    /// The shared browser process ended. Every card using it is dead and must be recreated
    /// with a new environment, which only the window can do. The argument is true when the
    /// user pressed Try again, which is never limited.
    /// </summary>
    internal event EventHandler<bool>? BrowserProcessFailed;

    internal async Task ShowCardAsync(WebCardSettings card, CoreWebView2Environment environment)
    {
        if (_disposed) return;
        var uri = new Uri(card.Url);
        // Rebuilding the layout re-shows unchanged cards; keep an unreachable one retrying.
        var sameCardRetrying = uri == _uri && _retryTimer?.IsRunning == true;
        if (!sameCardRetrying) StopRetry();
        CardId = card.Id;
        AutomationProperties.SetName(_webView, card.Name);
        _uri = uri;
        _zoom = SettingsStore.NormalizeZoom(card.Zoom);
        _setup.Visibility = Visibility.Collapsed;
        ShowStatus("Opening " + card.Name, _uri.Host, loading: true, retry: false);
        try
        {
            await EnsureInitializedAsync(environment);
            if (_disposed) return;
            await ApplyZoomAsync();
            if (_webView.Source != _uri) _webView.Source = _uri;
            else if (sameCardRetrying) ScheduleRetryStatus();
            else _status.Visibility = Visibility.Collapsed;
        }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            ShowStatus("Web card could not open", "Check that the Microsoft Edge WebView2 Runtime is installed, then try again.", false, true);
        }
    }

    internal void ShowSetup()
    {
        StopRetry();
        CardId = null;
        _uri = null;
        _status.Visibility = Visibility.Collapsed;
        _setup.Visibility = Visibility.Visible;
        // Unload the removed card so it stops running behind the setup screen
        // (for example a dashboard holding a live connection to its server).
        if (_webView.CoreWebView2 is not null && !_browserFailed)
        {
            _blanking = true;
            _webView.CoreWebView2.Navigate("about:blank");
        }
    }

    internal void ShowUnavailable() => ShowStatus(
        "Web cards could not start",
        "Check that the Microsoft Edge WebView2 Runtime is installed and that EdgeDock can access its local profile, then try again.",
        false,
        true);

    internal void Reload()
    {
        // A panel without a card shows setup over a blank page; there is nothing to reload.
        if (_uri is null) return;
        StopRetry();
        if (_webView.CoreWebView2 is not null && !_browserFailed) _webView.Reload();
    }

    internal void SetInteractive(bool interactive)
    {
        _webView.IsHitTestVisible = interactive;
        _webView.IsTabStop = interactive;
    }

    /// <summary>
    /// Page zoom that behaves like the browser's own: the page lays out on a viewport of
    /// panel size divided by the zoom, then the result is scaled to fit. CSS zoom is not
    /// used because it shrinks full-height (100vh) layouts and leaves a gap below them.
    /// </summary>
    /// <summary>
    /// DevTools calls can complete out of order unless each is awaited, so updates run one at
    /// a time; a request arriving mid-update is folded into one more pass with the latest
    /// size and zoom.
    /// </summary>
    private async Task ApplyZoomAsync()
    {
        if (_zoomRunning)
        {
            _zoomPending = true;
            return;
        }
        _zoomRunning = true;
        try
        {
            do
            {
                _zoomPending = false;
                await ApplyZoomOnceAsync();
            }
            while (_zoomPending && !_disposed);
        }
        finally { _zoomRunning = false; }
    }

    private async Task ApplyZoomOnceAsync()
    {
        var core = _webView.CoreWebView2;
        if (core is null || _disposed || _browserFailed) return;
        try
        {
            if (Math.Abs(_zoom - 1.0) < 0.001)
            {
                if (!_zoomApplied) return;
                await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
                _zoomApplied = false;
                return;
            }
            var width = (int)Math.Round(_webView.ActualWidth / _zoom);
            var height = (int)Math.Round(_webView.ActualHeight / _zoom);
            if (width <= 0 || height <= 0) return;
            var parameters = System.Text.Json.JsonSerializer.Serialize(new
            {
                width,
                height,
                deviceScaleFactor = 0,
                mobile = false,
                scale = _zoom
            });
            await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", parameters);
            _zoomApplied = true;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            // Zoom is cosmetic; a page that refuses it still works at 100%.
        }
    }

    private void WebView_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_zoomApplied && Math.Abs(_zoom - 1.0) < 0.001) return;
        if (_zoomTimer is null)
        {
            _zoomTimer = DispatcherQueue.CreateTimer();
            _zoomTimer.IsRepeating = false;
            _zoomTimer.Interval = TimeSpan.FromMilliseconds(150);
            _zoomTimer.Tick += async (_, _) => await ApplyZoomAsync();
        }
        _zoomTimer.Start();
    }

    internal async Task<string?> ExecuteScriptAsync(string script) =>
        _webView.CoreWebView2 is null ? null : await _webView.CoreWebView2.ExecuteScriptAsync(script);

    private async Task EnsureInitializedAsync(CoreWebView2Environment environment)
    {
        if (_initialized) return;
        await _webView.EnsureCoreWebView2Async(environment);
        if (_disposed) throw new ObjectDisposedException(nameof(WebPanelView));
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 initialization completed without a core instance.");
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(HideScrollbarsScript);
        if (_disposed) throw new ObjectDisposedException(nameof(WebPanelView));
        try { await core.ExecuteScriptAsync(HideScrollbarsScript); }
        catch (Exception exception) when (exception is InvalidOperationException or COMException) { }
        _webView.CoreProcessFailed += CoreProcessFailed;
        _initialized = true;
    }

    private void NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _retryTimer?.Stop();
        if (_blanking && args.Uri == "about:blank") return;
        if (!SettingsStore.IsAllowedUrl(args.Uri, out _))
        {
            args.Cancel = true;
            ShowStatus("Link blocked", "EdgeDock only opens http and https web addresses.", false, true);
            return;
        }
        ShowStatus("Loading web card", Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.Host : string.Empty, true, false);
    }

    private void NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_blanking)
        {
            _blanking = false;
            return;
        }
        if (_uri is null) return;
        if (args.IsSuccess)
        {
            _retryAttempt = 0;
            _status.Visibility = Visibility.Collapsed;
            return;
        }
        if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
        if (!NeedsUserAction(args.WebErrorStatus))
        {
            // Network not back yet at sign-in or after waking, or the server restarting
            // (refused connections and proxy errors report Unknown). Keep trying quietly
            // instead of leaving an always-on dashboard on an error page.
            ScheduleRetry();
            return;
        }
        ShowStatus("Web card did not load", $"WebView reported {args.WebErrorStatus}. Check the address and connection, then try again.", false, true);
    }

    /// <summary>Failures that retrying cannot fix: certificates, credentials and broken redirects.</summary>
    private static bool NeedsUserAction(CoreWebView2WebErrorStatus status) => status is
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.CertificateIsInvalid or
        CoreWebView2WebErrorStatus.CertificateRevoked or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or
        CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired or
        CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired or
        CoreWebView2WebErrorStatus.RedirectFailed;

    private void ScheduleRetry()
    {
        if (_disposed) return;
        var delay = WebRecovery.RetryDelay(_retryAttempt++);
        if (_retryTimer is null)
        {
            _retryTimer = DispatcherQueue.CreateTimer();
            _retryTimer.IsRepeating = false;
            _retryTimer.Tick += (_, _) =>
            {
                // A card removed while waiting must never be contacted again.
                if (!_disposed && !_browserFailed && _uri is not null && _webView.CoreWebView2 is not null) _webView.Reload();
            };
        }
        _retryTimer.Interval = delay;
        _retryTimer.Start();
        ScheduleRetryStatus();
    }

    private void ScheduleRetryStatus() => ShowStatus("Waiting for connection",
        $"{_uri?.Host ?? "The page"} cannot be reached yet. Trying again in {_retryTimer?.Interval.TotalSeconds ?? 5:0} seconds.", true, true);

    private void StopRetry()
    {
        _retryTimer?.Stop();
        _retryAttempt = 0;
    }

    private void CoreProcessFailed(WebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        var kind = args.ProcessFailedKind;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            switch (kind)
            {
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    _browserFailed = true;
                    StopRetry();
                    ShowStatus("Restarting web cards", "The browser engine stopped. Your saved address and sign-in have not been removed.", true, true);
                    BrowserProcessFailed?.Invoke(this, false);
                    break;
                case CoreWebView2ProcessFailedKind.RenderProcessExited or
                     CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or
                     CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                    if (_pageRecoveries.TryConsume(DateTimeOffset.Now))
                    {
                        ShowStatus("Reloading web card", "The page stopped responding, so EdgeDock is reloading it.", true, false);
                        Reload();
                    }
                    else
                    {
                        ShowStatus("Web card process stopped", "This page keeps stopping. Reload it when ready; your saved address and sign-in have not been removed.", false, true);
                    }
                    break;
                // GPU, utility and other helper processes are restarted by WebView2 itself.
            }
        });
    }

    /// <summary>Shows a status for a card the window could not recreate after a browser failure.</summary>
    internal void ShowBrowserFailed() => ShowStatus(
        "Web cards stopped",
        "The browser engine keeps stopping. Try again when ready; your saved address and sign-in have not been removed.",
        false,
        true);

    private void ShowStatus(string title, string message, bool loading, bool retry)
    {
        _statusTitle.Text = title;
        _statusMessage.Text = message;
        _loading.IsActive = loading;
        _loading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = Visibility.Visible;
    }

    private void BuildSetup()
    {
        _setup.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 16, 22));
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, Spacing = 14 };
        content.Children.Add(new SymbolIcon(Symbol.World) { Width = 48, Height = 48 });
        content.Children.Add(new TextBlock { Text = "Add a web card", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "Open Settings to name a page and add its web address. Your sign-in stays on this computer.", FontSize = 17, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        _setup.Children.Add(content);
    }

    private void BuildStatus()
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560, Spacing = 12 };
        content.Children.Add(_loading);
        content.Children.Add(_statusTitle);
        content.Children.Add(_statusMessage);
        content.Children.Add(_retry);
        _status.Children.Add(content);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer?.Stop();
        _zoomTimer?.Stop();
        _webView.SizeChanged -= WebView_SizeChanged;
        _webView.NavigationStarting -= NavigationStarting;
        _webView.NavigationCompleted -= NavigationCompleted;
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreProcessFailed -= CoreProcessFailed;
            // Closing a card whose browser process already ended can fail; it is being discarded anyway.
            try { _webView.Close(); }
            catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
        }
    }
}

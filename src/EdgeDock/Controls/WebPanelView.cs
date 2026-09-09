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
    private bool _initialized;
    private bool _disposed;

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
            if (_webView.CoreWebView2 is not null) Reload();
            else if (_retryInitialization is not null) await _retryInitialization();
        };
        _webView.NavigationStarting += NavigationStarting;
        _webView.NavigationCompleted += NavigationCompleted;
    }

    internal string? CardId { get; private set; }
    internal WebView2 WebView => _webView;

    internal async Task ShowCardAsync(WebCardSettings card, CoreWebView2Environment environment)
    {
        if (_disposed) return;
        CardId = card.Id;
        AutomationProperties.SetName(_webView, card.Name);
        _uri = new Uri(card.Url);
        _setup.Visibility = Visibility.Collapsed;
        ShowStatus("Opening " + card.Name, _uri.Host, loading: true, retry: false);
        try
        {
            await EnsureInitializedAsync(environment);
            if (_disposed) return;
            if (_webView.Source != _uri) _webView.Source = _uri;
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
        CardId = null;
        _uri = null;
        _status.Visibility = Visibility.Collapsed;
        _setup.Visibility = Visibility.Visible;
    }

    internal void ShowUnavailable() => ShowStatus(
        "Web cards could not start",
        "Check that the Microsoft Edge WebView2 Runtime is installed and that EdgeDock can access its local profile, then try again.",
        false,
        true);

    internal void Reload()
    {
        if (_webView.CoreWebView2 is not null) _webView.Reload();
    }

    internal void SetInteractive(bool interactive)
    {
        _webView.IsHitTestVisible = interactive;
        _webView.IsTabStop = interactive;
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
        if (args.IsSuccess)
        {
            _status.Visibility = Visibility.Collapsed;
            return;
        }
        ShowStatus("Web card did not load", $"WebView reported {args.WebErrorStatus}. Check the address and connection, then try again.", false, true);
    }

    private void CoreProcessFailed(WebView2 sender, CoreWebView2ProcessFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => ShowStatus("Web card process stopped", "Reload the page. Your saved address and sign-in have not been removed.", false, true));

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
        _webView.NavigationStarting -= NavigationStarting;
        _webView.NavigationCompleted -= NavigationCompleted;
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreProcessFailed -= CoreProcessFailed;
            _webView.Close();
        }
    }
}

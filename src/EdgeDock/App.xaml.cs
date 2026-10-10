using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EdgeDock;

public partial class App : Application
{
    private static MainWindow? _window;
    private static DispatcherQueue? _dispatcher;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>Called from another launch of EdgeDock; may arrive on a background thread.</summary>
    internal static void ShowRunningWindow() => _dispatcher?.TryEnqueue(() => _window?.BringToFront());
}

using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace MeshMonitor.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly string dataDirectory =
        Environment.GetEnvironmentVariable("MESHMONITOR_WINUI_DATA") ?? BackendHost.DefaultDataDirectory;
    private BackendHost? backend;
    private Exception? browserInitializationError;
    private Uri? origin;
    private bool closed;

    public MainWindow()
    {
        InitializeComponent();
        Dashboard.CoreWebView2Initialized += (_, args) => browserInitializationError = args.Exception;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= OnLoaded;
        await StartAsync();
    }

    private async void OnRetry(object sender, RoutedEventArgs args) => await StartAsync();

    private async void OnOpenInBrowser(object sender, RoutedEventArgs args)
    {
        if (origin is not { } address) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(address))
                Details.Text = $"Open {address} in your browser while this window stays open.";
        }
        catch (Exception error)
        {
            Details.Text = $"Open {address} in your browser while this window stays open. {error.Message}";
        }
    }

    private async Task StartAsync()
    {
        var cancellation = lifetime.Token;
        ShowStatus("Starting MeshMonitor…");
        try
        {
            if (backend is null)
            {
                var host = new BackendHost(Path.GetFullPath(dataDirectory));
                backend = host;
                origin = await host.StartAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                _ = WatchBackendAsync(host, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            ShowFailure(error.Message);
            return;
        }

        var activeHost = backend;
        try
        {
            ShowStatus("Opening the dashboard…");
            var web = await InitializeDashboardAsync();
            cancellation.ThrowIfCancellationRequested();
            if (backend != activeHost || origin is not { } address) return;
            web.NavigationStarting -= OnNavigationStarting;
            web.NavigationStarting += OnNavigationStarting;
            web.NavigationCompleted -= OnNavigationCompleted;
            web.NavigationCompleted += OnNavigationCompleted;
            web.NewWindowRequested -= OnNewWindowRequested;
            web.NewWindowRequested += OnNewWindowRequested;

            web.Navigate(address.AbsoluteUri);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The window closed during startup.
        }
        catch (Exception error)
        {
            if (backend == activeHost) ShowBrowserFailure(error.Message);
        }
    }

    private async Task<CoreWebView2> InitializeDashboardAsync()
    {
        try
        {
            if (Dashboard.CoreWebView2 is { } initialized)
                return initialized;

            browserInitializationError = null;
            string profile = Path.Combine(Path.GetFullPath(dataDirectory), "webview");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                "", profile, new CoreWebView2EnvironmentOptions());
            await Dashboard.EnsureCoreWebView2Async(environment);
            return Dashboard.CoreWebView2 ??
                throw new InvalidOperationException(
                    browserInitializationError?.Message ?? "The browser control did not initialize.",
                    browserInitializationError);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"WebView2 could not start (0x{(error.InnerException ?? error).HResult:X8}). " +
                $"Check that the Microsoft Edge WebView2 Runtime is installed. {error.Message}",
                error);
        }
    }

    private async Task WatchBackendAsync(BackendHost host, CancellationToken cancellation)
    {
        try
        {
            var exitCode = await host.WaitForExitAsync(cancellation);
            if (ReferenceEquals(backend, host))
                ShowFailure($"The MeshMonitor backend exited with code {exitCode}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || backend != host)
        {
            // The window closed or this backend was replaced.
        }
        catch (Exception error)
        {
            if (ReferenceEquals(backend, host))
                ShowFailure(error.Message);
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = !IsDashboardUri(args.Uri);
    }

    private async void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (IsDashboardUri(args.Uri))
            sender.Navigate(args.Uri);
        else if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private bool IsDashboardUri(string address) =>
        origin is not null &&
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port;

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (closed || backend is null || args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            return;

        if (args.IsSuccess)
        {
            Startup.Visibility = Visibility.Collapsed;
            Progress.IsActive = false;
        }
        else
        {
            ShowBrowserFailure($"The dashboard could not load ({args.WebErrorStatus}).");
        }
    }

    private void ShowStatus(string message)
    {
        Startup.Visibility = Visibility.Visible;
        Status.Text = message;
        Details.Text = "";
        Progress.IsActive = true;
        Progress.Visibility = Visibility.Visible;
        Retry.Visibility = Visibility.Collapsed;
        OpenInBrowser.Visibility = Visibility.Collapsed;
    }

    private void ShowBrowserFailure(string message)
    {
        if (closed) return;
        ShowStatus("The dashboard could not open in this window");
        Details.Text = $"{message}\n\nMeshMonitor is running at {origin}";
        Progress.IsActive = false;
        Progress.Visibility = Visibility.Collapsed;
        Retry.Visibility = Visibility.Visible;
        OpenInBrowser.Visibility = Visibility.Visible;
    }

    private void ShowFailure(string message)
    {
        if (closed) return;
        string logs = backend?.LogDirectory ?? Path.Combine(dataDirectory, "logs");
        StopBackend();
        ShowStatus("MeshMonitor could not open");
        Details.Text = $"{message}\n\nBackend logs: {logs}";
        Progress.IsActive = false;
        Progress.Visibility = Visibility.Collapsed;
        Retry.Visibility = Visibility.Visible;
    }

    private void StopBackend()
    {
        var host = backend;
        backend = null;
        origin = null;
        host?.Dispose();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        lifetime.Cancel();
        StopBackend();
        lifetime.Dispose();
    }
}

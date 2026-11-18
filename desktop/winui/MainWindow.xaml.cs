using MeshMonitor.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherQueueTimer timer;
    private Tray? tray;
    private BackendHost? backend;
    private MeshApi? api;
    private NodeRow[] nodes = [];
    private NodeRow[] mapNodes = [];
    private NodeRow? selectedMapNode;
    private bool closed;
    private bool quitting;
    private bool busy;
    private bool signedIn;
    private bool needsCode;
    private bool connected;
    private bool updatingSources;
    private bool fitMap = true;

    public MainWindow()
    {
        InitializeComponent();
        MeshMap.PopulateLegend(MapLegend);
        NodeMap.NodeSelected += node => SelectMapNode(node, center: false);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 740));
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.Tick += OnTick;
        AppWindow.Closing += (_, args) =>
        {
            if (quitting || tray is null) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= OnLoaded;
        CreateTray();
        await StartAsync();
    }

    private void CreateTray()
    {
        try
        {
            tray = new Tray(
                () => DispatcherQueue.TryEnqueue(ShowNativeWindow),
                () => DispatcherQueue.TryEnqueue(async () => await OpenFolderAsync(logs: true)),
                () => DispatcherQueue.TryEnqueue(async () => await OpenFolderAsync(logs: false)),
                () => DispatcherQueue.TryEnqueue(Quit));
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    private void ShowNativeWindow()
    {
        if (closed) return;
        AppWindow.Show();
        Activate();
    }

    private async void OnViewLogs(object sender, RoutedEventArgs args) => await OpenFolderAsync(logs: true);
    private async void OnOpenData(object sender, RoutedEventArgs args) => await OpenFolderAsync(logs: false);
    private void OnQuit(object sender, RoutedEventArgs args) => Quit();

    private async Task OpenFolderAsync(bool logs)
    {
        if (closed || backend is not { } host) return;
        string folder = logs ? host.LogDirectory : host.DataDirectory;
        try
        {
            var location = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder);
            if (await Windows.System.Launcher.LaunchFolderAsync(location)) return;
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        ShowNativeWindow();
        ShowNotice($"Open {folder} in File Explorer.", InfoBarSeverity.Warning);
    }

    private void Quit()
    {
        if (closed) return;
        quitting = true;
        Close();
    }

    private async void OnRetry(object sender, RoutedEventArgs args) => await StartAsync();

    private async Task StartAsync()
    {
        Retry.Visibility = Visibility.Collapsed;
        AccessTitle.Text = "Starting MeshMonitor";
        StartupProgress.Visibility = Visibility.Visible;
        StartupProgress.IsActive = true;
        Notice.IsOpen = false;
        try
        {
            var folder = Environment.GetEnvironmentVariable("MESHMONITOR_WINUI_DATA")
                ?? BackendHost.DefaultDataDirectory;
            var host = new BackendHost(Path.GetFullPath(folder));
            backend = host;
            var endpoint = await host.StartAsync(lifetime.Token);
            if (closed) return;
            SetBackendReady(true);
            api = new MeshApi(endpoint, lifetime.Token);
            _ = ObserveBackendAsync(host);
            await ApplySessionAsync();
        }
        catch (Exception error) when (!closed)
        {
            ShowStartupFailure(error.Message);
        }
        catch (OperationCanceledException) when (closed) { }
        finally
        {
            if (!closed)
            {
                StartupProgress.IsActive = false;
                StartupProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task ObserveBackendAsync(BackendHost host)
    {
        try
        {
            int code = await host.WaitForExitAsync(lifetime.Token);
            if (!closed && backend == host)
                ShowStartupFailure($"The MeshMonitor backend exited with code {code}.");
        }
        catch (Exception error)
        {
            if (!closed && backend == host) ShowStartupFailure(error.Message);
        }
    }

    private void ShowStartupFailure(string message)
    {
        string? logs = backend?.LogDirectory;
        ShowAccess();
        StopBackend();
        Credentials.Visibility = Visibility.Collapsed;
        AccessTitle.Text = "MeshMonitor could not start";
        Retry.Visibility = Visibility.Visible;
        ShowNotice(message + (logs is null ? "" : $"\nLogs are in {logs}"), InfoBarSeverity.Error);
    }

    private void ShowAccess()
    {
        signedIn = false;
        canReadSourceSettings = canWriteSources = false;
        needsCode = false;
        timer.Stop();
        Workspace.Visibility = Visibility.Collapsed;
        AccountTools.Visibility = Visibility.Collapsed;
        SignInView.Visibility = Visibility.Collapsed;
        Access.Visibility = Visibility.Visible;
        AccessTitle.Text = "Sign in to MeshMonitor";
        Credentials.Visibility = Visibility.Visible;
        Code.Visibility = Visibility.Collapsed;
        Code.Text = "";
        Password.Password = "";
        Password.Visibility = Visibility.Visible;
        Username.IsEnabled = true;
        SignIn.Content = "Sign in";
        SourcesList.ItemsSource = null;
        ClearSource();
    }

    private async Task ApplySessionAsync()
    {
        var status = await api!.AuthStatus();
        if (closed) return;
        signedIn = status.Flag("authenticated") == true;
        bool isAdmin = signedIn && status.Property("user").Flag("isAdmin") == true;
        var grants = status.Property("permissions").Property("global").Property("sources");
        canWriteSources = signedIn && (isAdmin || grants.Flag("write") == true);
        canReadSourceSettings = signedIn && (isAdmin || grants.Flag("read") == true);
        needsCode = false;
        Code.Text = "";
        ClearActivity();
        AddSource.Visibility = canWriteSources ? Visibility.Visible : Visibility.Collapsed;
        Access.Visibility = Visibility.Collapsed;
        Credentials.Visibility = Visibility.Collapsed;
        AccountTools.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignInView.Visibility = signedIn || status.Flag("localAuthDisabled") == true
            ? Visibility.Collapsed : Visibility.Visible;
        Workspace.Visibility = Visibility.Visible;
        if (!signedIn && (Activity.SelectedItem as PivotItem) != MapTab) Activity.SelectedItem = MapTab;
        await LoadSourcesAsync();
        if (!closed && Workspace.Visibility == Visibility.Visible) timer.Start();
    }

    private void OnShowSignIn(object sender, RoutedEventArgs args) => ShowAccess();

    private async void OnCancelSignIn(object sender, RoutedEventArgs args) =>
        await RunAsync(_ => ApplySessionAsync());

    private async void OnSignIn(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (needsCode)
        {
            await client.VerifyCode(Code.Text.Trim());
            Code.Text = "";
        }
        else
        {
            string password = Password.Password;
            Password.Password = "";
            var result = await client.SignIn(Username.Text.Trim(), password);
            if (result.Flag("requireMfa") == true)
            {
                needsCode = true;
                Password.Visibility = Visibility.Collapsed;
                Username.IsEnabled = false;
                Code.Visibility = Visibility.Visible;
                SignIn.Content = "Verify code";
                Code.Focus(FocusState.Programmatic);
                return;
            }
        }
        await ApplySessionAsync();
    });

    private async void OnSignOut(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        await client.SignOut();
        if (!closed)
        {
            ShowAccess();
            await ApplySessionAsync();
        }
    });

    private async void OnChangePassword(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        var current = new PasswordBox { Header = "Current password" };
        var updated = new PasswordBox { Header = "New password" };
        var confirm = new PasswordBox { Header = "Confirm new password" };
        var error = DialogError();
        var dialog = Form("Change password", "Save", current, updated, confirm, error);
        dialog.PrimaryButtonClick += (_, action) =>
        {
            if (updated.Password.Length >= 8 && updated.Password == confirm.Password) return;
            action.Cancel = true;
            error.Text = "Enter matching passwords with at least eight characters.";
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
        await client.ChangePassword(current.Password, updated.Password);
        ShowNotice("Password changed.", InfoBarSeverity.Success);
    });

    private ContentDialog Form(string title, string action, params FrameworkElement[] fields)
    {
        var stack = new StackPanel { Spacing = 12 };
        foreach (var field in fields) stack.Children.Add(field);
        return new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = title,
            Content = new ScrollViewer { Content = stack, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
    }

    private static TextBlock DialogError() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private async void OnRefreshSources(object sender, RoutedEventArgs args) =>
        await RunAsync(_ => LoadSourcesAsync());

    private async void OnTick(DispatcherQueueTimer sender, object args)
    {
        var tab = Activity.SelectedItem as PivotItem;
        if (ConversationPicker.IsDropDownOpen || (showingOlder && tab == MessagesTab) ||
            (tab == MapTab && MapSearch.Text.Length > 0 &&
                (MapSearch.IsSuggestionListOpen || MapSearchHasFocus()))) return;
        await RunAsync(_ => LoadSourceAsync(), clearNotice: false);
    }

    private async Task LoadSourcesAsync(string? preferred = null)
    {
        string? previous = (SourcesList.SelectedItem as SourceRow)?.Id;
        string? selected = preferred ?? previous;
        var sources = await api!.Sources();
        SourceRow[] rows = [SourceRow.Unified, .. sources];
        if (closed) return;
        var next = rows.FirstOrDefault(source => source.Id == selected) ?? rows.FirstOrDefault();
        if (next?.Id != previous) ClearActivity();
        updatingSources = true;
        try
        {
            SourcesList.ItemsSource = rows;
            SourcesList.SelectedItem = next;
        }
        finally { updatingSources = false; }
        await LoadSourceAsync();
    }

    private async void OnSourceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (updatingSources || busy || Workspace.Visibility != Visibility.Visible) return;
        ClearActivity();
        await RunAsync(_ => LoadSourceAsync());
    }

    private async void OnTabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (busy || Workspace.Visibility != Visibility.Visible || !args.AddedItems.OfType<PivotItem>().Any()) return;
        await RunAsync(_ => LoadSourceAsync());
    }

    private async void OnConnect(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow { CanConnect: true, Enabled: true } source) return;
        await client.SetConnected(source, !connected);
        await LoadSourceAsync();
    });

    private async Task LoadSourceAsync()
    {
        if (SourcesList.SelectedItem is not SourceRow source)
        {
            ClearSource();
            return;
        }

        SourceName.Text = source.Name;
        EditSource.Visibility = !source.IsUnified && canWriteSources ? Visibility.Visible : Visibility.Collapsed;
        ConnectionDiagnostics.Visibility = source.IsUnified ? Visibility.Collapsed : Visibility.Visible;
        SourceHeader.Visibility = Visibility.Visible;
        Activity.Visibility = Visibility.Visible;
        NoSource.Visibility = Visibility.Collapsed;
        if (source.IsUnified)
        {
            Connection.Text = "All readable sources";
            ConnectionAction.Visibility = Visibility.Collapsed;
        }
        else
        {
            var status = await api!.SourceStatus(source);
            if (closed) return;
            localMessageNode = status.String("nodeId");
            connected = status.Flag("connected") == true;
            Connection.Text = $"{source.Subtitle} · {ConnectionState(source, status)}";
            ConnectionAction.Visibility = canWriteSources && source.Enabled && source.CanConnect
                ? Visibility.Visible : Visibility.Collapsed;
            ConnectionAction.Content = connected ? "Disconnect" : "Connect";
        }

        var selectedTab = Activity.SelectedItem as PivotItem;
        if (selectedTab == MessagesTab)
        {
            await LoadConversationAsync(source);
        }
        else if (selectedTab == TelemetryTab)
        {
            await LoadTelemetryAsync(source);
        }
        else if (selectedTab == NodesTab)
        {
            nodes = await api!.Nodes(source);
            if (!closed) FilterNodes();
        }
        else
        {
            var positioned = await api!.MapNodes(source);
            if (closed) return;
            mapNodes = positioned;
            if (selectedMapNode is { } previous)
            {
                selectedMapNode = positioned.FirstOrDefault(node => node.Id == previous.Id &&
                    node.SourceId == previous.SourceId);
                if (selectedMapNode is null) ClearPositionTrail();
            }
            bool autoFit = fitMap && positioned.Length > 0;
            NodeMap.SetNodes(positioned, autoFit, selectedMapNode);
            if (autoFit) fitMap = false;
            UpdateMapSearch();
            UpdateMapSummary();
        }
    }

    private void UpdateMapSummary()
    {
        MapSummary.Text = selectedMapNode is { } node
            ? DescribeNode(node, MapSourceName(node))
            : NodeMap.PositionedCount == 0 ? "No positioned nodes." : $"{NodeMap.PositionedCount} positioned nodes";
        PositionHistoryActions.Visibility = selectedMapNode is { } selected && FindMapSource(selected) is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnZoomIn(object sender, RoutedEventArgs args) => NodeMap.ChangeZoom(1);
    private void OnZoomOut(object sender, RoutedEventArgs args) => NodeMap.ChangeZoom(-1);
    private void OnFitMap(object sender, RoutedEventArgs args) => NodeMap.Fit();

    private void OnBasemapChanged(object sender, SelectionChangedEventArgs args)
    {
        if (MapAttribution is null) return;
        bool gray = BasemapStyle.SelectedIndex == 1;
        NodeMap.SetGrayBasemap(gray);
        MapAttribution.Content = gray ? "Esri, DeLorme, NAVTEQ" : "Esri and contributors";
        MapAttribution.NavigateUri = new Uri(gray
            ? "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Light_Gray_Base/MapServer"
            : "https://www.arcgis.com/home/item.html?id=6e850093c837475e8c23d905ac43b7d0");
    }

    private async void OnSend(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow { Enabled: true, CanTransmit: true } source) return;
        string text = MessageText.Text.Trim();
        string destination = Destination.Text.Trim();
        int maxChannel = source.Type == "meshcore" ? 255 : 7;
        if (text.Length == 0 || !(Channel.Value >= 0 && Channel.Value <= maxChannel) ||
            Channel.Value != Math.Truncate(Channel.Value))
        {
            ShowNotice("Enter a message and a valid channel number.", InfoBarSeverity.Warning);
            return;
        }
        var conversation = activeConversation;
        int channel = conversation.Channel ?? (conversation.Peer is not null ? 0 : (int)Channel.Value);
        string? peer = conversation.IsRecent ? (destination.Length == 0 ? null : destination) : conversation.Peer;
        await client.Send(source, text, channel, peer, replyTarget?.PacketId);
        if (closed) return;
        MessageText.Text = "";
        ClearReply();
        ResetMessageHistory();
        ShowNotice("The backend accepted the message.", InfoBarSeverity.Success);
        await LoadSourceAsync();
    });

    private void OnSearch(object sender, TextChangedEventArgs args)
    {
        if (Workspace.Visibility == Visibility.Visible) FilterNodes();
    }

    private void FilterNodes()
    {
        string query = Search.Text.Trim();
        var selection = NodesList.SelectedItem as NodeRow;
        var visible = nodes.Where(node => MatchesNode(node, query)).ToArray();
        NodesList.ItemsSource = visible;
        NodesList.SelectedItem = selection is null ? null : visible.FirstOrDefault(node =>
            node.Id == selection.Id && node.SourceId == selection.SourceId);
        if (NodesList.SelectedItem is null)
            NodeDetails.Text = visible.Length == 0 ? "No matching nodes." : $"{visible.Length} nodes";
    }

    private void OnNodeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (NodesList.SelectedItem is NodeRow node)
            NodeDetails.Text = DescribeNode(node);
    }

    private static string DescribeNode(NodeRow node, string? source = null)
    {
        var details = new List<string> { node.Name, node.Id };
        if (!string.IsNullOrEmpty(source)) details.Add(source);
        details.Add($"Last heard {ValueFormat.Time(node.Timestamp)}");
        if (node.Snr is not null) details.Add(node.Signal);
        if (node.Rssi is { } rssi) details.Add($"{rssi:g} dBm RSSI");
        if (node.Battery is { } battery) details.Add(battery > 100 ? "External power" : $"{battery:g}% battery");
        if (node.Latitude is { } latitude && node.Longitude is { } longitude)
            details.Add($"Position {latitude:F5}, {longitude:F5}");
        return string.Join(" · ", details);
    }

    private void ClearActivity()
    {
        nodes = [];
        NodesList.ItemsSource = null;
        MessagesList.ItemsSource = null;
        ResetConversations();
        NodeDetails.Text = "";
        MessageStatus.Text = "";
        TelemetryStatus.Text = "";
        telemetryRows = [];
        TelemetryList.ItemsSource = null;
        NodeMap.SetNodes([], fit: false);
        ClearPositionTrail();
        PositionHistoryActions.Visibility = Visibility.Collapsed;
        mapNodes = [];
        MapSearch.Text = "";
        MapSearch.ItemsSource = null;
        fitMap = true;
        selectedMapNode = null;
        MapSummary.Text = "";
        Composer.Visibility = Visibility.Collapsed;
        MessageText.Text = "";
        Destination.Text = "";
        Channel.Value = 0;
        ConnectionAction.Visibility = Visibility.Collapsed;
        Connection.Text = "";
    }

    private void ClearSource()
    {
        ClearActivity();
        NoSource.Visibility = Visibility.Visible;
        SourceHeader.Visibility = Visibility.Collapsed;
        Activity.Visibility = Visibility.Collapsed;
    }

    private async Task RunAsync(Func<MeshApi, Task> action, bool clearNotice = true)
    {
        if (busy || closed || api is not { } client) return;
        busy = true;
        UpdateDetailActions();
        UpdateConversationActions();
        SignIn.IsEnabled = Refresh.IsEnabled = AddSource.IsEnabled = ConnectionAction.IsEnabled = SendMessage.IsEnabled = false;
        EditSource.IsEnabled = ConnectionDiagnostics.IsEnabled = false;
        SourcesList.IsEnabled = Activity.IsEnabled = ChangePassword.IsEnabled = SignOut.IsEnabled = false;
        if (clearNotice) Notice.IsOpen = false;
        try { await action(client); }
        catch (Exception error)
        {
            if (!closed && api == client)
            {
                if (signedIn && error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
                    ShowAccess();
                ShowNotice(error.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            busy = false;
            if (!closed)
            {
                UpdateDetailActions();
                UpdateConversationActions();
                SignIn.IsEnabled = Refresh.IsEnabled = AddSource.IsEnabled = ConnectionAction.IsEnabled = SendMessage.IsEnabled = true;
                EditSource.IsEnabled = ConnectionDiagnostics.IsEnabled = true;
                SourcesList.IsEnabled = Activity.IsEnabled = ChangePassword.IsEnabled = SignOut.IsEnabled = true;
            }
        }
    }

    private void ShowNotice(string text, InfoBarSeverity severity)
    {
        if (closed) return;
        Notice.Severity = severity;
        Notice.Message = text;
        Notice.IsOpen = true;
    }

    private void StopBackend()
    {
        if (!closed)
        {
            SetBackendReady(false);
        }
        api?.Dispose();
        api = null;
        var host = backend;
        backend = null;
        host?.Dispose();
    }

    private void SetBackendReady(bool ready)
    {
        ViewLogs.IsEnabled = OpenData.IsEnabled = ready;
        if (tray is not null) tray.IsBackendReady = ready;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        timer.Stop();
        lifetime.Cancel();
        StopBackend();
        tray?.Dispose();
        tray = null;
        lifetime.Dispose();
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeshMonitor.WinUI;

internal sealed class BackendHost(string dataDirectory) : IDisposable
{
    internal static readonly string DefaultDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshMonitor.WinUI");

    internal string DataDirectory => dataDirectory;
    internal string LogDirectory => Path.Combine(dataDirectory, "logs");

    private FileStream? instanceLock;
    private Process? process;
    private Process? notificationProcess;
    private Task logPumps = Task.CompletedTask;
    private Task notificationLogs = Task.CompletedTask;

    public async Task<Uri> StartAsync(CancellationToken cancellation)
    {
        string resources = Path.GetFullPath(
            Environment.GetEnvironmentVariable("MESHMONITOR_RESOURCES") ??
            Path.Combine(AppContext.BaseDirectory, "resources"));
        string dist = Path.Combine(resources, "dist");
        string node = Path.Combine(resources, "binaries", "node.exe");
        string entry = Path.Combine(dist, "server", "server.js");
        if (!File.Exists(node) || !File.Exists(entry) ||
            !File.Exists(Path.Combine(dist, "index.html")) ||
            !File.Exists(Path.Combine(dist, "package.json")) ||
            !Directory.Exists(Path.Combine(dist, "node_modules")))
        {
            throw new InvalidOperationException(
                $"MeshMonitor runtime files are missing from {resources}. " +
                "Set MESHMONITOR_RESOURCES to a prepared desktop resources folder.");
        }

        Directory.CreateDirectory(LogDirectory);
        try
        {
            instanceLock = File.Open(Path.Combine(dataDirectory, "backend.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "The backend data folder is locked. Close any other MeshMonitor WinUI window and retry.", error);
        }

        string portText = Environment.GetEnvironmentVariable("MESHMONITOR_WINUI_PORT") ?? "38081";
        if (!int.TryParse(portText, out int port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("MESHMONITOR_WINUI_PORT must be a port from 1024 to 65535.");

        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { listener.Start(); }
        catch (SocketException error)
        {
            throw new InvalidOperationException(
                $"Port {port} is unavailable. Close the application using it or set MESHMONITOR_WINUI_PORT.", error);
        }
        finally { listener.Stop(); }

        var origin = new Uri($"http://127.0.0.1:{port}/");
        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = dist,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(entry);
        start.Environment["NODE_ENV"] = "production";
        start.Environment["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["BIND_ADDRESS"] = "127.0.0.1";
        start.Environment["BASE_URL"] = "/";
        start.Environment["ALLOWED_ORIGINS"] = origin.GetLeftPart(UriPartial.Authority);
        start.Environment["DATA_DIR"] = dataDirectory;
        start.Environment["DATABASE_PATH"] = Path.Combine(dataDirectory, "meshmonitor.db");
        start.Environment["DATABASE_URL"] = "";
        start.Environment["SESSION_COOKIE_NAME"] = "meshmonitor.winui.sid";
        start.Environment["COOKIE_SECURE"] = "false";
        start.Environment["SESSION_SECRET"] = GetSessionSecret();
        start.Environment["IS_DESKTOP"] = "true";
        start.Environment["FIRMWARE_CHECK_ENABLED"] = "false";
        if (!start.Environment.TryGetValue("APPRISE_URL", out string? configuredApprise) ||
            string.IsNullOrWhiteSpace(configuredApprise))
        {
            if (TryStartNotifications(resources) is { } notifications)
                start.Environment["APPRISE_URL"] = notifications;
        }

        var child = Process.Start(start) ?? throw new InvalidOperationException("Node.js did not start.");
        process = child;
        logPumps = Task.WhenAll(
            CopyLogAsync(child.StandardOutput.BaseStream, "server-stdout.log"),
            CopyLogAsync(child.StandardError.BaseStream, "server-stderr.log"));

        await WaitUntilReadyAsync(origin, child, cancellation);
        return origin;
    }

    private string? TryStartNotifications(string resources)
    {
        string executable = Path.Combine(resources, "binaries", "apprise-api.exe");
        if (!File.Exists(executable)) return null;

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            int port;
            try
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally { listener.Stop(); }

            string config = Path.Combine(dataDirectory, "apprise-config");
            Directory.CreateDirectory(config);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["APPRISE_HOST"] = "127.0.0.1";
            start.Environment["APPRISE_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["APPRISE_CONFIG_DIR"] = config;
            var child = Process.Start(start);
            if (child is null) return null;
            notificationProcess = child;
            notificationLogs = Task.WhenAll(
                CopyLogAsync(child.StandardOutput.BaseStream, "apprise-stdout.log"),
                CopyLogAsync(child.StandardError.BaseStream, "apprise-stderr.log"));
            return $"http://127.0.0.1:{port}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or SocketException)
        {
            Debug.WriteLine(error);
            return null;
        }
    }

    private string GetSessionSecret()
    {
        string path = Path.Combine(dataDirectory, "session-secret");
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 64) return existing;
        }

        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, secret);
        File.Move(temporary, path, overwrite: true);
        return secret;
    }

    private async Task CopyLogAsync(Stream input, string filename)
    {
        await using var file = new FileStream(Path.Combine(LogDirectory, filename),
            FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await input.CopyToAsync(file).ConfigureAwait(false);
    }

    private static async Task WaitUntilReadyAsync(Uri origin, Process child, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (child.HasExited)
                    throw new InvalidOperationException($"The MeshMonitor backend exited with code {child.ExitCode}.");

                try
                {
                    using var response = await client.GetAsync(new Uri(origin, "api/health"), deadline.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                        if (json.RootElement.ValueKind == JsonValueKind.Object &&
                            json.RootElement.TryGetProperty("status", out var status) &&
                            status.ValueKind == JsonValueKind.String && status.GetString() == "ok" && !child.HasExited)
                            return;
                    }
                }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }

                await Task.Delay(350, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The MeshMonitor backend did not become healthy within 60 seconds.");
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        var child = process ?? throw new InvalidOperationException("The backend has not started.");
        await child.WaitForExitAsync(cancellation);
        return child.ExitCode;
    }

    public void Dispose()
    {
        var child = process;
        var notifications = notificationProcess;
        process = null;
        notificationProcess = null;
        try
        {
            StopProcess(child, logPumps);
        }
        finally
        {
            try { StopProcess(notifications, notificationLogs); }
            finally
            {
                instanceLock?.Dispose();
                instanceLock = null;
            }
        }
    }

    private static void StopProcess(Process? child, Task logs)
    {
        if (child is null) return;
        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            if (child.WaitForExit(1500)) logs.Wait(500);
        }
        catch (InvalidOperationException) when (child.HasExited) { }
        catch (AggregateException error) { Debug.WriteLine(error); }
        finally
        {
            child.Dispose();
            _ = logs.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        }
    }
}

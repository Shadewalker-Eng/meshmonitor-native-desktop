using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using MeshMonitor.WinUI;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private static string ProfileRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("MESHMONITOR_WINUI_DATA") ?? BackendHost.DefaultDataDirectory);
    private static string ProfilePointer => Path.Combine(ProfileRoot, "active-recovery-profile");
    private static string ActiveProfile()
    {
        if (!File.Exists(ProfilePointer)) return ProfileRoot;
        var id = File.ReadAllText(ProfilePointer).Trim();
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid recovery profile selection.");
        var path = Path.Combine(ProfileRoot, "recovery", id);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        return path;
    }

    private async Task RestoreArchive()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".gz");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var archive = await picker.PickSingleFileAsync();
        if (archive == null) return;
        var otherSecret = new CheckBox { Content = "Select the original session-secret file from another installation" };
        if (await Form("Restore system backup", "Restore and switch", Wrapped(
            $"Restore {archive.Name} into a new local profile? MeshMonitor will disconnect the current sources and start the restored backend. Your previous database stays on disk."), otherSecret).ShowAsync() != ContentDialogResult.Primary) return;
        string secretFile = Path.Combine(ActiveProfile(), "session-secret");
        if (otherSecret.IsChecked == true)
        {
            var keys = new FileOpenPicker();
            keys.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(keys, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selected = await keys.PickSingleFileAsync();
            if (selected == null) return;
            secretFile = selected.Path;
        }
        if (!File.Exists(secretFile) || new FileInfo(secretFile).Length > 4096 || File.ReadAllText(secretFile).Trim().Length < 64)
            throw new InvalidDataException("Select the session-secret file created by MeshMonitor Native.");
        string id = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(ProfileRoot, "recovery", id);
        string backupRoot = Path.Combine(destination, "system-backups");
        Directory.CreateDirectory(backupRoot);
        string? name = null;
        using (var file = await archive.OpenStreamForReadAsync())
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (await tar.GetNextEntryAsync(false, lifetime.Token) is { } entry)
            {
                var match = Regex.Match(entry.Name, @"\A(\d{4}-\d{2}-\d{2}_\d{6})(?:/([A-Za-z0-9_-]+\.json)?)?\z");
                if (!match.Success || (name != null && name != match.Groups[1].Value))
                    throw new InvalidDataException("Unexpected archive layout.");
                name = match.Groups[1].Value;
                string directory = Path.Combine(backupRoot, name);
                Directory.CreateDirectory(directory);
                if (entry.EntryType == TarEntryType.Directory && !match.Groups[2].Success) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !match.Groups[2].Success || entry.DataStream == null)
                    throw new InvalidDataException("Unexpected backup entry.");
                using var output = new FileStream(Path.Combine(directory, match.Groups[2].Value), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await entry.DataStream.CopyToAsync(output, lifetime.Token);
            }
        }
        if (name == null || !File.Exists(Path.Combine(backupRoot, name, "metadata.json")))
            throw new InvalidDataException("The archive has no backup metadata.");
        File.Copy(secretFile, Path.Combine(destination, "session-secret"));
        foreach (var preferences in Directory.EnumerateFiles(ActiveProfile(), "desktop-*.json"))
            if (otherSecret.IsChecked != true) File.Copy(preferences, Path.Combine(destination, Path.GetFileName(preferences)));
        timer.Stop();
        StopBackend();
        try
        {
            var host = new BackendHost(destination);
            backend = host;
            var endpoint = await host.StartAsync(lifetime.Token, name);
            string marker = Path.Combine(destination, ".restore-completed");
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != name)
                throw new InvalidDataException("The backend did not confirm restoration. See logs in " + destination);
            string temporary = ProfilePointer + "." + id;
            File.WriteAllText(temporary, id);
            api = new MeshApi(endpoint, lifetime.Token);
            SetBackendReady(true);
            _ = ObserveBackendAsync(host);
            await ApplySessionAsync();
            File.Move(temporary, ProfilePointer, true);
            ShowNotice("Restored database opened.", InfoBarSeverity.Success);
        }
        catch
        {
            StopBackend();
            await StartAsync();
            ShowNotice("Restore failed. See the recovery logs in " + destination, InfoBarSeverity.Error);
            throw;
        }
    }
}

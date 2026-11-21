using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private async void OnBrowserSignIn(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        var view = new WebView2 { Height = 425, MinWidth = 350 };
        var dialog = Form("Passkey or single sign-on", "Use signed-in account", view);
        string profile = Path.Combine(Path.GetTempPath(), "MeshMonitor-Login-" + Guid.NewGuid().ToString("N"));
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            dialog.IsPrimaryButtonEnabled = false;
            var choice = dialog.ShowAsync().AsTask();
            if (await Task.WhenAny(loaded.Task, choice) == choice) return;
            await view.EnsureCoreWebView2Async(environment);
            var browser = view.CoreWebView2;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.IsPasswordAutosaveEnabled = false;
            browser.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
                    (!string.Equals(uri.GetLeftPart(UriPartial.Authority), client.LoginOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                        && (uri.Scheme != "https" || uri.IsLoopback))) e.Cancel = true;
            };
            browser.Navigate(client.LoginOrigin.ToString());
            dialog.IsPrimaryButtonEnabled = true;
            if (await choice != ContentDialogResult.Primary) return;
            var cookies = await browser.CookieManager.GetCookiesAsync(client.LoginOrigin.ToString());
            var cookie = cookies.FirstOrDefault(c => c.Name == "meshmonitor.winui.sid" && c.IsHttpOnly &&
                c.Domain.TrimStart('.') == client.LoginOrigin.Host && c.Path == "/");
            if (cookie == null) throw new InvalidOperationException("Complete sign-in on the MeshMonitor page first.");
            client.ImportDesktopSession(cookie.Value);
            await ApplySessionAsync();
            if (!signedIn) throw new InvalidOperationException("MeshMonitor could not confirm the sign-in.");
        }
        finally
        {
            dialog.Hide();
            try { if (view.CoreWebView2 != null) await view.CoreWebView2.Profile.ClearBrowsingDataAsync(); }
            finally { view.Close(); }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });

    private void ConfigureAccountTools(Func<string, MenuFlyoutSubItem> group, ToolItem add, SourceRow source)
    {
        var menu = group("Accounts and backups");
        if (File.Exists(ProfilePointer)) add(menu, "Return to original database", async _ =>
        {
            if (await Form("Switch database", "Switch", Wrapped("Close this recovery profile and open the original database?")).ShowAsync() != ContentDialogResult.Primary) return;
            timer.Stop();
            StopBackend();
            File.Delete(ProfilePointer);
            await StartAsync();
        });
        add(menu, "Create local account", CreateLocalAccount);
        add(menu, "Edit account profile", c => EditAccount(c, source, "profile"));
        add(menu, "Change administrator role", c => EditAccount(c, source, "role"));
        add(menu, "Edit access permissions", c => EditAccount(c, source, "permissions"));
        add(menu, "Create system backup", async c =>
        {
            await c.RequestAsync("api/system/backup", new {}, timeout: TimeSpan.FromMinutes(30));
            ShowNotice("System backup created.", InfoBarSeverity.Success);
        });
        add(menu, "Restore or migrate a system backup", _ => RestoreArchive());
        add(menu, "Download system backup", async c =>
        {
            var row = await ChooseRecord("System backups", Records(await c.RequestAsync("api/system/backup/list")),
                r => r.String("dirname") ?? "", "Choose file");
            if (row?.String("dirname") is string name)
                await SaveDownload(c, "api/system/backup/download/" + Esc(name), name + ".tar", ".gz");
        });
    }

    private async Task CreateLocalAccount(MeshApi client)
    {
        var name = new TextBox { Header = "Username" };
        var password = new PasswordBox { Header = "Initial password" };
        if (await Form("Create local account", "Create", name, password).ShowAsync() != ContentDialogResult.Primary) return;
        await client.RequestAsync("api/users", new { username = name.Text.Trim(), password = password.Password });
        ShowNotice("Account created.", InfoBarSeverity.Success);
    }

    private async Task EditAccount(MeshApi client, SourceRow source, string action)
    {
        var user = await ChooseRecord("Select an account", Records((await client.RequestAsync("api/users")).Property("users")),
            u => u.String("username") ?? u.Property("id").ToString(), "Select");
        if (user == null) return;
        string path = "api/users/" + user.Value.Property("id");
        string title = user.Value.String("username") ?? "Account";
        if (action == "permissions")
        {
            await EditAccess(client, path + "/permissions", source, title);
            return;
        }
        if (action == "role")
        {
            var admin = new CheckBox { Content = "Administrator", IsChecked = user.Value.Flag("isAdmin") ?? user.Value.Number("isAdmin") == 1 };
            if (await Form("Role · " + title, "Save role", admin).ShowAsync() != ContentDialogResult.Primary) return;
            await client.RequestAsync(path + "/admin", new { isAdmin = admin.IsChecked == true }, HttpMethod.Put);
        }
        else
        {
            var display = new TextBox { Header = "Display name", Text = user.Value.String("displayName") ?? "" };
            var email = new TextBox { Header = "Email", Text = user.Value.String("email") ?? "" };
            if (await Form(title, "Save profile", display, email).ShowAsync() != ContentDialogResult.Primary) return;
            await client.RequestAsync(path, new { displayName = display.Text.Trim(), email = email.Text.Trim() }, HttpMethod.Put);
        }
        ShowNotice("Account updated.", InfoBarSeverity.Success);
    }

    private async Task EditAccess(MeshApi client, string path, SourceRow source, string title)
    {
        string url = path + (source.IsUnified ? "" : "?" + Scope(source));
        var original = (await client.RequestAsync(url)).Property("permissions");
        var grants = JsonNode.Parse(original.GetRawText())!.AsObject();
        string[] global = ["dashboard","info","automations","audit","security","themes","sources","channel_database"];
        string[] scoped = ["messages","nodes","nodes_private","traceroute","packetmonitor","configuration","connection","automation","waypoints","remote_admin","settings"];
        var names = global.Concat(source.IsUnified ? [] : scoped.Concat(Enumerable.Range(0, 8).Select(i => "channel_" + i))).Union(grants.Select(g => g.Key)).Order().ToArray();
        var resource = new ComboBox { Header = "Resource", ItemsSource = names, SelectedIndex = 0 };
        var read = new CheckBox { Content = "Read" };
        var write = new CheckBox { Content = "Write" };
        var map = new CheckBox { Content = "View on map" };
        void LoadGrant()
        {
            var item = grants[(string)resource.SelectedItem];
            read.IsChecked = item?["read"]?.GetValue<bool>() == true;
            write.IsChecked = item?["write"]?.GetValue<bool>() == true;
            map.IsChecked = item?["viewOnMap"]?.GetValue<bool>() == true;
        }
        resource.SelectionChanged += (_, _) => LoadGrant();
        LoadGrant();
        if (await Form(title + " · " + source.Name, "Save selected grant", resource, read, write, map).ShowAsync() != ContentDialogResult.Primary) return;
        if ((await client.RequestAsync(url)).Property("permissions").GetRawText() != original.GetRawText())
            throw new InvalidOperationException("Permissions changed. Reopen this account.");
        var key = (string)resource.SelectedItem;
        var grant = grants[key] as JsonObject;
        if (grant == null) grants[key] = grant = new JsonObject();
        grant["read"] = read.IsChecked == true;
        grant["write"] = write.IsChecked == true;
        grant["viewOnMap"] = map.IsChecked == true;
        await client.RequestAsync(path, new { permissions = grants, sourceId = source.IsUnified ? null : source.Id }, HttpMethod.Put);
        ShowNotice("Access permissions saved.", InfoBarSeverity.Success);
    }
}

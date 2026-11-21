using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private static string Esc(string value) => Uri.EscapeDataString(value);
    private static string Scope(SourceRow s) => "sourceId=" + Esc(s.Id);
    private static string SourceEndpoint(SourceRow s, string path) => $"api/sources/{Esc(s.Id)}/{path}";
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    private static JsonElement[] Records(JsonElement value)
    {
        var rows = value.ValueKind == JsonValueKind.Array ? value : value.Property("data");
        return rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToArray() : [];
    }
    private Task ShowResult(string title, string text)
    {
        var block = Wrapped(text.Length == 0 ? "No records." : text);
        block.IsTextSelectionEnabled = true;
        return DetailDialog(title, new ScrollViewer { Content = block, MaxHeight = 430 }).ShowAsync().AsTask();
    }
    private async Task<JsonElement?> ChooseRecord(string title, JsonElement[] rows, Func<JsonElement,string> label, string action)
    {
        if (rows.Length == 0) { await ShowResult(title, "No records."); return null; }
        var list = new ListView { ItemsSource = rows.Select(label).ToArray(), MaxHeight = 330, SelectedIndex = 0 };
        return await Form(title, action, list).ShowAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0 ? rows[list.SelectedIndex] : null;
    }

    private void OnWorkflowTools(object sender, RoutedEventArgs args)
    {
        if (busy || SourcesList.SelectedItem is not SourceRow s) return;
        var menu = new MenuFlyout();
        MenuFlyoutSubItem Group(string name) { var g = new MenuFlyoutSubItem { Text = name }; menu.Items.Add(g); return g; }
        void Add(MenuFlyoutSubItem group, string name, Func<MeshApi,Task> work, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = name, IsEnabled = enabled };
            item.Click += async (_, _) => await RunAsync(work);
            group.Items.Add(item);
        }
        var n = Group("Nodes");
        var node = NodesList.SelectedItem as NodeRow;
        bool target = node is not null && !s.IsUnified && s.Type == "meshtastic_tcp" && MeshApi.TryRemoteNodeNumber(node.Id, out _);
        Add(n,"Filter nodes",_ => EditNodeFilters(s));
        Add(n,"Ignored nodes",c => IgnoredNodes(c,s), !s.IsUnified);
        Add(n,"Ignore selected node",c => IgnoreNode(c,s,node!),target);
        Add(n,"Selected position override",c => OverridePosition(c,s,node!),target);
        if (s.IsMeshCore && node?.MeshCoreKey is string key && (node.SourceId.Length == 0 || node.SourceId == s.Id))
        {
            string prefix = SourceEndpoint(s,"meshcore/");
            Add(n,"Contact position history",async c => await ReportTable(c,node.Name+" · Position history",await c.RequestAsync(prefix+"nodes/"+Esc(key)+"/position-history")));
            Add(n,"Request contact path discovery",async c =>
            {
                if (await Form("Discover path", "Send request", Wrapped("Request a route to "+node.Name+" through "+s.Name+"?")).ShowAsync() == ContentDialogResult.Primary)
                {
                    await c.RequestAsync(prefix+"contacts/"+Esc(key)+"/discover-path",new {});
                    ShowNotice("Path discovery requested.",InfoBarSeverity.Success);
                }
            },signedIn);
        }
        Add(n,"Node enrichment",c => EnrichNodes(c));
        ConfigureMessageTools(Group,Add,s);
        ConfigureAutomationTools(Group,Add,s);
        ConfigureMapTools(Group,Add,s);
        ConfigureAnalysisTools(Group,Add,s);
        ConfigureIntegrationTools(Group,Add,s);
        if (radioAdmin) ConfigureAccountTools(Group, Add, s);
        menu.ShowAt(WorkflowTools);
    }

    private bool positionedOnly;
    private int heardHours;
    private double? minSignal;
    private bool NodeFilterMatches(NodeRow row) => (!positionedOnly || row.Latitude.HasValue && row.Longitude.HasValue)
        && (heardHours == 0 || row.Timestamp >= DateTimeOffset.UtcNow.AddHours(-heardHours))
        && (minSignal is null || row.Snr >= minSignal);

    private async Task EditNodeFilters(SourceRow source)
    {
        var position = new CheckBox { Content = "Has position", IsChecked = positionedOnly };
        int[] hours = [0,1,24,168];
        var age = new ComboBox { Header="Last heard", ItemsSource=new[]{"Any time","Last hour","Last day","Last week"}, SelectedIndex=Array.IndexOf(hours,heardHours) };
        var signal = new NumberBox { Header="Minimum SNR (dB, blank for any)", Value=minSignal ?? double.NaN };
        if (await Form("Node filters", "Apply",position,age,signal).ShowAsync() != ContentDialogResult.Primary) return;
        positionedOnly=position.IsChecked==true; heardHours=hours[age.SelectedIndex]; minSignal=double.IsFinite(signal.Value)?signal.Value:null;
        Activity.SelectedItem=NodesTab; await LoadSourceAsync();
    }

    private async Task IgnoreNode(MeshApi c, SourceRow s, NodeRow node)
    {
        var sync = new CheckBox { Content="Also sync the ignore list to the radio" };
        if (await Form("Ignore " + node.Name, "Ignore",Wrapped(s.Name+" · "+node.Id),sync).ShowAsync()!=ContentDialogResult.Primary) return;
        await c.RequestAsync($"api/nodes/{Esc(node.Id)}/ignored?{Scope(s)}",new {sourceId=s.Id,isIgnored=true,syncToDevice=sync.IsChecked==true});
        await LoadSourceAsync();
    }
    private async Task IgnoredNodes(MeshApi c,SourceRow s)
    {
        var rows=Records(await c.RequestAsync("api/ignored-nodes?"+Scope(s)));
        var selected=await ChooseRecord("Ignored nodes · "+s.Name,rows,r=>$"{r.String("longName")} {r.String("nodeId")} {r.String("reason")}","Remove from ignore list");
        if (selected is not {} row) return;
        string id=row.String("nodeId") ?? $"!{row.UInt32("nodeNum"):x8}";
        await c.RequestAsync($"api/ignored-nodes/{Esc(id)}?{Scope(s)}",new {},HttpMethod.Delete);
        await LoadSourceAsync();
    }
    private async Task OverridePosition(MeshApi c,SourceRow s,NodeRow node)
    {
        string path=$"api/nodes/{Esc(node.Id)}/position-override";
        var current=await c.RequestAsync(path+"?"+Scope(s));
        if (current.Flag("isPrivate")==true && current.Number("latitude")==null) { await ShowResult(node.Name,"This position is private."); return; }
        var enabled=new CheckBox {Content="Use override",IsChecked=current.Flag("enabled")==true};
        var hidden=new CheckBox {Content="Private coordinates",IsChecked=current.Flag("isPrivate")==true};
        var lat=new NumberBox {Header="Latitude",Value=current.Number("latitude")??node.Latitude??double.NaN};
        var lon=new NumberBox {Header="Longitude",Value=current.Number("longitude")??node.Longitude??double.NaN};
        var alt=new NumberBox {Header="Altitude (m)",Value=current.Number("altitude")??double.NaN};
        var err=DialogError();
        var dialog=Form("Position override · "+node.Name,"Save",enabled,lat,lon,alt,hidden,err);
        dialog.PrimaryButtonClick+=(_,a)=>{ if(enabled.IsChecked==true && !(lat.Value is >=-90 and <=90 && lon.Value is >=-180 and <=180)){a.Cancel=true;err.Text="Enter valid latitude and longitude.";} };
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        await c.RequestAsync(path,new {sourceId=s.Id,enabled=enabled.IsChecked==true,isPrivate=hidden.IsChecked==true,
            latitude=double.IsFinite(lat.Value)?(double?)lat.Value:null, longitude=double.IsFinite(lon.Value)?(double?)lon.Value:null, altitude=double.IsFinite(alt.Value)?(double?)alt.Value:null});
        await LoadSourceAsync();
    }
}

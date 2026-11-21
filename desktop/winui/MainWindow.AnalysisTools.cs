using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private void ConfigureAnalysisTools(Func<string,MenuFlyoutSubItem> group,ToolItem add,SourceRow s)
    {
        var g=group("Packet analysis and reports");
        add(g,"Unified packet monitor",c=>UnifiedPackets(c));
        add(g,"Cross-source activity",c=>NetworkReport(c,s,"packets"));
        add(g,"Mesh issues",async c=>await ReportTable(c,"Mesh issues",await c.RequestAsync("api/analysis/mesh-issues?limit=100")));
        add(g,"MQTT gateway violations",async c=>await ReportTable(c,"MQTT gateway violations",await c.RequestAsync("api/analysis/mqtt-violations/gateways")));
        add(g,"Solar monitoring",async c=>await ReportTable(c,"Solar nodes · Last week",await c.RequestAsync("api/analysis/solar-nodes?lookback_days=7")));
        add(g,"Direct RF contacts",async c=>await ReportTable(c,"Direct RF contacts · "+s.Name,await c.RequestAsync(SourceEndpoint(s,"direct-rf-contacts"))),s.Type=="meshtastic_tcp");
        add(g,"Hop counts",c=>NetworkReport(c,s,"hop-counts"));
        add(g,"RF coverage senders",c=>NetworkReport(c,s,"coverage/senders"));
        add(g,"Cross-source receptions",c=>NetworkReport(c,s,"coverage/receptions"));
        add(g,"Export packets as JSONL",c=>ExportPackets(c,s),s.Type=="meshtastic_tcp");
    }
    private async Task UnifiedPackets(MeshApi c)
    {
        var filter=new ComboBox{Header="Cross-source traffic",ItemsSource=new[]{"All readable packets","Originated at another source","Likely relayed by another source"},SelectedIndex=0};
        if(await Form("Unified packet monitor","Open",filter).ShowAsync()!=ContentDialogResult.Primary)return;
        string path="api/unified/packets?limit=100"+(filter.SelectedIndex==0?"":"&crossSource="+(filter.SelectedIndex==1?"origin":"relay"));
        var cursors=new Stack<string>();string cursor="";
        while(!closed)
        {
            var snapshot=await c.RequestAsync(path+(cursor.Length==0?"":"&cursor="+Esc(cursor)));
            var rows=Records(snapshot.Property("packets"));
            var list=new ListView{MaxHeight=215,ItemsSource=rows.Select(r=>$"{ValueFormat.Time(r.Timestamp("timestamp"))} · {r.String("sourceName")} · {r.String("portnum_name")??r.String("payload_type_name")} · {r.String("from_node_id")}").ToArray()};
            var detail=Wrapped("");detail.IsTextSelectionEnabled=true;
            list.SelectionChanged+=(_,_)=>detail.Text=list.SelectedIndex>=0?Pretty(rows[list.SelectedIndex]):"";
            var panel=new StackPanel{Spacing=6};panel.Children.Add(list);panel.Children.Add(new ScrollViewer{Content=detail,MaxHeight=145});
            var dlg=DetailDialog("Unified packet monitor",panel);
            string next=snapshot.String("nextCursor")??"";
            if(next.Length>0 && next!=cursor && snapshot.Flag("hasMore")==true)dlg.PrimaryButtonText="Older";
            dlg.SecondaryButtonText=cursors.Count>0?"Newer":"Refresh";
            var action=await dlg.ShowAsync();
            if(action==ContentDialogResult.Primary){cursors.Push(cursor);cursor=next;}
            else if(action==ContentDialogResult.Secondary){if(cursors.Count>0)cursor=cursors.Pop();}
            else return;
        }
    }
    private async Task NetworkReport(MeshApi c,SourceRow s,string report)
    {
        var hours=new ComboBox{Header="Time window",ItemsSource=new[]{"Last hour","Last day","Last week"},SelectedIndex=1};
        if(report!="hop-counts" && await Form("Report period","Generate",hours).ShowAsync()!=ContentDialogResult.Primary)return;
        long since=DateTimeOffset.UtcNow.AddHours(-new[]{1,24,168}[hours.SelectedIndex]).ToUnixTimeMilliseconds();
        string path=report=="packets"?"api/unified/packets/distribution?since="+since:
            "api/analysis/"+report+"?since="+since+(s.IsUnified?"":"&sources="+Esc(s.Id))+(report=="coverage/receptions"?"&crossSourceOnly=true":"");
        var result=await c.RequestAsync(path);
        await ReportTable(c, report=="packets"?"Packet activity across readable sources":report+" · "+s.Name, result, path);
    }
    private async Task ExportPackets(MeshApi c,SourceRow s)
    {
        string path="api/packets/export?"+Scope(s)+"&since="+(packetSince>0?packetSince:PacketWindowStart());
        if(packetNode.HasValue)path+="&from_node="+packetNode;
        if(packetType.HasValue)path+="&portnum="+packetType;
        await SaveDownload(c,path,"meshmonitor-packets",".jsonl");
    }
}

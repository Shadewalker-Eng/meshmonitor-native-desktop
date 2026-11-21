using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MeshMonitor.Native;

internal sealed partial class MeshApi
{
    internal async Task UploadGeoLayer(string name,Stream input)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"api/geojson/upload");
        request.Headers.Add("X-CSRF-Token",(await RequestAsync("api/csrf-token")).String("csrfToken"));
        request.Headers.Add("X-Filename",string.Concat(Path.GetFileName(name).Select(ch=>ch is >= ' ' and <= '~' ? ch : '_')));
        request.Content=new StreamContent(input);
        request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response=await client.SendAsync(request,cancellation);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellation));
    }
    internal async Task Download(string path, Stream output)
    {
        using var response = await client.GetAsync(path,HttpCompletionOption.ResponseHeadersRead,cancellation);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(output,cancellation);
    }
}

public sealed partial class MainWindow
{
    private delegate void ToolItem(MenuFlyoutSubItem group,string title,Func<MeshApi,Task> action,bool enabled=true);

    private async Task EnrichNodes(MeshApi c)
    {
        var data=(await c.RequestAsync("api/nodes/enrichment/analysis")).Property("data");
        var choices=Records(data.Property("nodes")).SelectMany(node=>Records(node.Property("targets")).Select(t=>
            JsonSerializer.SerializeToElement(new { nodeNum=node.UInt32("nodeNum"),targetSourceId=t.String("targetSourceId"),
                donorSourceId=t.String("donorSourceId"),label=$"{node.String("displayName")} · {t.String("donorSourceName")} → {t.String("targetSourceName")} · {string.Join(", ",Records(t.Property("fillableFields")).Select(f=>f.ToString()))}" }))).ToArray();
        var row=await ChooseRecord("Copy missing node information",choices,r=>r.String("label")!,"Copy missing fields");
        if(row is not {} item)return;
        var result=await c.RequestAsync("api/nodes/enrichment/apply",new {items=new[]{new {nodeNum=item.UInt32("nodeNum"),donorSourceId=item.String("donorSourceId"),targetSourceId=item.String("targetSourceId")}},pushToNodeDb=false});
        await ShowResult("Enrichment result",Pretty(result.Property("data")));
    }

    private void ConfigureMessageTools(Func<string,MenuFlyoutSubItem> group,ToolItem add,SourceRow s)
    {
        var g=group("Messages and channels");
        add(g,"Unified channel conversations",c=>UnifiedChannels(c));
        add(g,"MeshCore channel history and management",c=>MeshCoreChannelHistory(c,s),s.IsMeshCore && signedIn);
        add(g,"Search messages",c=>SearchMessages(c,s));
        add(g,"Insert node mention",async c=>
        {
            var candidates=(await c.Nodes(s)).Where(n=>MeshApi.TryRemoteNodeNumber(n.Id,out _)).ToArray();
            var picker=new ComboBox{Header="Mention a node",ItemsSource=candidates.Select(n=>$"{n.Name} ({n.Id})").ToArray(),SelectedIndex=candidates.Length>0?0:-1};
            if(await Form("Insert mention","Insert",picker).ShowAsync()!=ContentDialogResult.Primary || picker.SelectedIndex<0)return;
            Activity.SelectedItem=MessagesTab;await LoadSourceAsync();
            int caret=MessageText.SelectionStart;
            string token="@"+candidates[picker.SelectedIndex].Id+" ";
            MessageText.Text=MessageText.Text.Remove(caret,MessageText.SelectionLength).Insert(caret,token);
            MessageText.SelectionStart=caret+token.Length;
        },s.Type=="meshtastic_tcp"&&signedIn);
        add(g,"Find mentions",c=>SearchMessages(c,s,true));
        add(g,"Export message CSV",c=>ExportMessages(c,s));
        add(g,"New virtual channel",c=>EditVirtualChannel(c,true));
        add(g,"Channel actions and permissions",c=>VirtualChannelActions(c));
        add(g,"Manage virtual channels",c=>EditVirtualChannel(c,false));
    }
    private async Task UnifiedChannels(MeshApi c)
    {
        var channel=await ChooseRecord("Unified channels",Records(await c.RequestAsync("api/unified/channels")),
            r=>$"{r.String("name")} · {Records(r.Property("sources")).Length} source slots","Open");
        if(channel is not {} ch)return;
        string path="api/unified/messages?limit=100&channel="+Esc(ch.String("name")!);
        long? before=null;
        while(!closed)
        {
            var rows=Records(await c.RequestAsync(path+(before.HasValue?"&before="+before:"")));
            var panel=new StackPanel{Spacing=8};
            foreach(var row in rows)
            {
                string heard=string.Join(", ",Records(row.Property("receptions")).Select(r=>r.String("sourceName")).Distinct());
                panel.Children.Add(Wrapped($"{ValueFormat.Time(row.Timestamp("timestamp"))} · {row.String("fromNodeLongName")??row.String("fromNodeId")} · {heard}",strong:true));
                panel.Children.Add(Wrapped(row.String("text")??""));
            }
            if(rows.Length==0)panel.Children.Add(Wrapped("No messages in this page."));
            var dlg=DetailDialog(ch.String("name")!,new ScrollViewer{Content=panel,MaxHeight=390});
            long? oldest=rows.Select(row=>row.Timestamp("createdAt")?.ToUnixTimeMilliseconds()).Min();
            if(rows.Length>=100 && oldest.HasValue && (!before.HasValue||oldest<before))dlg.PrimaryButtonText="Older";
            dlg.SecondaryButtonText="Send on a source";
            var result=await dlg.ShowAsync();
            if(result==ContentDialogResult.Primary){before=oldest;continue;}
            if(result!=ContentDialogResult.Secondary)return;
            var sources=(SourcesList.ItemsSource as SourceRow[]??[]).Where(s=>s.Type=="meshtastic_tcp"&&s.Enabled).ToArray();
            var slots=Records(ch.Property("sources")).Where(r=>r.Number("channelNumber") is >=0 and <=7 && sources.Any(s=>s.Id==r.String("sourceId"))).ToArray();
            var sendVia=await ChooseRecord("Send on one radio",slots,r=>$"{r.String("sourceName")} · Channel {r.Number("channelNumber")}","Compose");
            if(sendVia is not {} via)continue;
            var message=Multiline("Message","");
            if(await Form("Send on "+via.String("sourceName"),"Send",message).ShowAsync()!=ContentDialogResult.Primary)continue;
            if(message.Text.Trim().Length==0)continue;
            await c.Send(sources.First(s=>s.Id==via.String("sourceId")),message.Text.Trim(),(int)via.Number("channelNumber")!.Value,null);
            before=null;
        }
    }

    private async Task SearchMessages(MeshApi c,SourceRow s,bool mention=false)
    {
        var q=new TextBox { Header=mention?"Mentioned node ID (for example !abcd1234)":"Search text" };
        if(await Form("Search · "+s.Name,"Find",q).ShowAsync()!=ContentDialogResult.Primary)return;
        var term=q.Text.Trim();
        if(term.Length==0)return;
        if(mention)
        {
            string id=term.TrimStart('@');
            if(!MeshApi.TryRemoteNodeNumber(id,out _))throw new InvalidOperationException("Enter an eight-digit !node ID.");
            term="@"+id.ToLowerInvariant();
        }
        int offset=0;
        while(!closed)
        {
            var result=await c.RequestAsync($"api/messages/search?q={Esc(term)}&limit=100&offset={offset}"+(s.IsUnified?"":"&"+Scope(s)));
            var hits=Records(result);
            var list=new ListView {ItemsSource=hits.Select(row=>$"{row.String("fromNodeId")} · {ValueFormat.Time(row.Timestamp("timestamp"))}\n{row.String("text")}").ToArray(),MaxHeight=360};
            var dlg=DetailDialog($"{result.Number("total")??hits.Length} matches · {s.Name}",list);
            if(hits.Length>0 && offset+hits.Length<result.Number("total"))dlg.PrimaryButtonText="Next page";
            if(offset>0)dlg.SecondaryButtonText="Previous page";
            var action=await dlg.ShowAsync();
            if(action==ContentDialogResult.Primary)offset+=hits.Length;
            else if(action==ContentDialogResult.Secondary)offset=Math.Max(0,offset-100);
            else return;
        }
    }

    private async Task SaveDownload(MeshApi c,string path,string filename,string extension)
    {
        var picker=new FileSavePicker {SuggestedFileName=filename};
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(),new List<string>{extension});
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
        var target=await picker.PickSaveFileAsync();
        if(target==null)return;
        using var transaction=await target.OpenTransactedWriteAsync();
        using(var output=transaction.Stream.AsStreamForWrite())
        {
            output.SetLength(0);
            await c.Download(path,output);
            await output.FlushAsync();
            await transaction.CommitAsync();
        }
        ShowNotice("Export saved.",InfoBarSeverity.Success);
    }

    private async Task ExportMessages(MeshApi c,SourceRow s)
    {
        var type=new ComboBox{Header="Messages",ItemsSource=new[]{"All","Channels","Direct"},SelectedIndex=0};
        var hours=new NumberBox{Header="Hours of history",Value=24,Minimum=1,Maximum=87600};
        var keyword=new TextBox{Header="Include keyword (optional)"};
        if(await Form("Export CSV · "+s.Name,"Choose file",type,hours,keyword).ShowAsync()!=ContentDialogResult.Primary)return;
        if(!double.IsFinite(hours.Value))throw new InvalidOperationException("Enter a history window.");
        string path=$"api/messages/export?type={new[]{"all","channels","dms"}[type.SelectedIndex]}&start={DateTimeOffset.UtcNow.AddHours(-hours.Value).ToUnixTimeMilliseconds()}";
        if(!s.IsUnified)path+="&source="+Esc(s.Id);
        if(keyword.Text.Trim().Length>0)path+="&include="+Esc(keyword.Text.Trim());
        await SaveDownload(c,path,"meshmonitor-messages",".csv");
    }

    private async Task MeshCoreChannelHistory(MeshApi c, SourceRow source)
    {
        var slot = new NumberBox { Header = "Channel slot", Minimum = 0, Maximum = 255, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        if (await Form("MeshCore channel · " + source.Name, "Open", slot).ShowAsync() != ContentDialogResult.Primary) return;
        if (!double.IsFinite(slot.Value) || slot.Value != Math.Truncate(slot.Value)) throw new FormatException("Enter an integer channel slot.");
        string endpoint = SourceEndpoint(source, "meshcore/messages/channel/" + (int)slot.Value);
        int offset = 0;
        while (!closed)
        {
            var page = await c.RequestAsync(endpoint + "?limit=100&offset=" + offset);
            var messages = Records(page);
            var list = new ListView { MaxHeight = 300, ItemsSource = messages.Select(m =>
                (m.String("senderName") ?? m.String("sender") ?? "") + "  " + (m.String("text") ?? m.String("message") ?? m.ToString())).ToArray() };
            var clear = new CheckBox { Content = "Clear stored messages from this channel" };
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(list); body.Children.Add(clear);
            var dlg = DetailDialog("Channel " + (int)slot.Value + " · " + source.Name, body);
            dlg.PrimaryButtonText = page.Flag("hasMore") == true ? "Older" : "";
            dlg.SecondaryButtonText = offset > 0 ? "Newer" : "";
            clear.Click += (_, _) => dlg.PrimaryButtonText = clear.IsChecked == true ? "Review deletion" : page.Flag("hasMore") == true ? "Older" : "";
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.None) return;
            if (result == ContentDialogResult.Primary && clear.IsChecked == true)
            {
                if (await Form("Clear channel history", "Delete messages", Wrapped($"Delete stored messages in channel {(int)slot.Value} on {source.Name}?")).ShowAsync() == ContentDialogResult.Primary)
                    await c.RequestAsync(endpoint, new {}, HttpMethod.Delete);
                offset = 0;
            }
            else offset = result == ContentDialogResult.Primary ? offset + 100 : Math.Max(0, offset - 100);
        }
    }

    private async Task VirtualChannelActions(MeshApi c)
    {
        const string channels="api/channel-database";
        var all=Records(await c.RequestAsync(channels)).Where(row=>row.String("protocol")!="meshcore").ToArray();
        var picked=await ChooseRecord("Virtual channels",all,row=>row.String("name")??"Channel","Select");
        if(picked is not {} ch)return;
        string path=channels+"/"+ch.Property("id");
        var action=new ComboBox{Header=ch.String("name"),ItemsSource=new[]{"View access","Change user access","Reorder entry","Decrypt historical packets","Decryption progress","Delete entry"},SelectedIndex=0};
        if(await Form("Channel actions","Continue",action).ShowAsync()!=ContentDialogResult.Primary)return;
        switch(action.SelectedIndex)
        {
            case 0: await ShowResult("Channel access",Pretty(await c.RequestAsync(path+"/permissions")));break;
            case 1:
                var rules=Records(await c.RequestAsync(path+"/permissions"));
                var id=new NumberBox{Header="User ID",Minimum=1};
                var read=new CheckBox{Content="Read messages"};var view=new CheckBox{Content="View on map"};
                id.ValueChanged+=(_,_)=>{var r=rules.FirstOrDefault(row=>row.Number("userId")==id.Value);read.IsChecked=r.Flag("canRead")==true;view.IsChecked=r.Flag("canViewOnMap")==true;};
                if(await Form("Channel access · "+ch.String("name"),"Save",id,read,view).ShowAsync()!=ContentDialogResult.Primary)return;
                if(!double.IsFinite(id.Value)||id.Value<1||id.Value!=Math.Truncate(id.Value))throw new InvalidOperationException("Enter a user ID.");
                await c.RequestAsync(path+"/permissions/"+(long)id.Value,new {canRead=read.IsChecked==true,canViewOnMap=view.IsChecked==true},HttpMethod.Put);break;
            case 2:
                var order=new NumberBox{Header="Sort order",Value=ch.Number("sortOrder")??0,Minimum=0};
                if(await Form("Channel order","Save",order).ShowAsync()==ContentDialogResult.Primary && double.IsFinite(order.Value))
                    await c.RequestAsync(channels+"/reorder",new{channels=new[]{new{id=ch.Number("id"),sortOrder=(int)order.Value}}},HttpMethod.Put);
                break;
            case 3:
                if(await Form("Decrypt historical packets","Start",Wrapped("Process retained packets with the stored key for "+ch.String("name")+"?")).ShowAsync()==ContentDialogResult.Primary)
                    await ShowResult("Decryption job",Pretty(await c.RequestAsync(path+"/retroactive-decrypt",new {})));
                break;
            case 4: await ShowResult("Decryption progress",Pretty(await c.RequestAsync(channels+"/retroactive-decrypt/progress")));break;
            case 5:
                if(await Form("Delete virtual channel","Delete",Wrapped(ch.String("name")??"Channel")).ShowAsync()==ContentDialogResult.Primary)
                    await c.RequestAsync(path,new {},HttpMethod.Delete);
                break;
        }
    }

    private async Task EditVirtualChannel(MeshApi c,bool create)
    {
        JsonElement original=default;
        if(!create)
        {
            var choice=await ChooseRecord("Meshtastic virtual channels",Records(await c.RequestAsync("api/channel-database"))
                .Where(r=>r.String("protocol")!="meshcore").ToArray(),r=>r.String("name")??"Channel","Edit");
            if(choice==null)return;
            original=choice.Value;
        }
        var name=new TextBox{Header="Channel name",Text=original.String("name")??""};
        var desc=new TextBox{Header="Description",Text=original.String("description")??""};
        var key=new PasswordBox{Header=create?"Base64 channel key":"Replacement Base64 key (blank keeps current)"};
        var enabled=new CheckBox{Content="Enabled",IsChecked=original.Flag("isEnabled")??true};
        if(await Form("Virtual channel","Save",name,desc,key,enabled).ShowAsync()!=ContentDialogResult.Primary)return;
        var body=new JsonObject{["name"]=name.Text.Trim(),["description"]=desc.Text,["isEnabled"]=enabled.IsChecked==true};
        if(create || key.Password.Length>0)body["psk"]=key.Password;
        string path="api/channel-database"+(create?"":"/"+original.Property("id"));
        await c.RequestAsync(path,body,create?HttpMethod.Post:HttpMethod.Put);
        ShowNotice("Channel saved.",InfoBarSeverity.Success);
    }
}

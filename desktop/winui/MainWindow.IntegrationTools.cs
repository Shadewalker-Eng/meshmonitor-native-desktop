using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private void ConfigureIntegrationTools(Func<string,MenuFlyoutSubItem> group,ToolItem add,SourceRow s)
    {
        var g=group("Source integrations");
        add(g,"Add Reticulum source",c=>ReticulumConnection(c,null),canWriteSources);
        add(g,"Edit Reticulum connection",c=>ReticulumConnection(c,s),canReadSourceSettings&&canWriteSources&&s.Type=="reticulum");
        add(g,"Reticulum status and directories",c=>ReticulumDirectory(c,s),s.Type=="reticulum");
        add(g,"Send LXMF message",c=>SendLxmf(c,s),s.Type=="reticulum"&&signedIn);
        add(g,"MQTT bridge filters and rewrites",c=>AdvancedMqtt(c,s),s.Type=="mqtt_bridge"&&canReadSourceSettings&&canWriteSources);
        add(g,"Broker observers and forwarding",c=>AdvancedMqtt(c,s),s.Type=="meshcore_mqtt_broker"&&canReadSourceSettings&&canWriteSources);
    }

    private async Task ReticulumConnection(MeshApi c,SourceRow? source)
    {
        var initial=source==null?default:(await c.SourceConfiguration(source)).Property("config");
        var conf=initial.ValueKind==JsonValueKind.Object?JsonNode.Parse(initial.GetRawText())!.AsObject():new JsonObject();
        var first=Records(initial.Property("peers")).FirstOrDefault();
        var name=new TextBox{Header="Source name",Text=source?.Name??""};
        var mode=new ComboBox{Header="Connection mode",ItemsSource=new[]{"Shared rnsd instance","TCP peer"},SelectedIndex=initial.String("mode")=="tcp_peer"?1:0};
        var bridge=new TextBox{Header="Bridge WebSocket URL",Text=initial.String("bridgeUrl")??"ws://127.0.0.1:8765"};
        var token=new PasswordBox{Header="Bridge token"};
        var dir=new TextBox{Header="Config directory on bridge host",Text=initial.String("configDir")??""};
        var host=new TextBox{Header="TCP peer host",Text=first.String("host")??""};
        var port=new NumberBox{Header="TCP peer port",Value=first.Number("port")??4242,Minimum=1,Maximum=65535};
        var auto=new CheckBox{Content="Connect automatically",IsChecked=initial.Flag("autoConnect")??true};
        void Refresh(){dir.Visibility=mode.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;host.Visibility=port.Visibility=mode.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;}
        mode.SelectionChanged+=(_,_)=>Refresh();Refresh();
        var error=DialogError();var dlg=Form("Reticulum connection","Save",name,mode,bridge,token,dir,host,port,auto,error);
        dlg.PrimaryButtonClick+=(_,a)=>{
            bool valid=name.Text.Trim().Length>0&&Uri.TryCreate(bridge.Text.Trim(),UriKind.Absolute,out var uri)&&uri.Scheme is "ws" or "wss" &&
                (mode.SelectedIndex==0?dir.Text.Trim().Length>0:host.Text.Trim().Length>0 && port.Value is >=1 and <=65535 && port.Value==Math.Truncate(port.Value));
            a.Cancel=!valid;if(!valid)error.Text="Enter a source name, WebSocket URL, and the selected connection details.";
        };
        if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
        conf.Remove("token");
        if(token.Password.Length>0)conf["token"]=token.Password;
        else if(source!=null && bridge.Text.Trim()!=(initial.String("bridgeUrl")??"ws://127.0.0.1:8765"))conf["token"]=null;
        conf["mode"]=mode.SelectedIndex==0?"attach":"tcp_peer";conf["autoConnect"]=auto.IsChecked==true;conf["bridgeUrl"]=bridge.Text.Trim();
        if(mode.SelectedIndex==0){conf["configDir"]=dir.Text.Trim();conf.Remove("peers");}
        else{conf.Remove("configDir");if(initial.String("mode")!="tcp_peer" || first.String("host")!=host.Text.Trim() || first.Number("port")!=port.Value)
            conf["peers"]=JsonSerializer.SerializeToNode(new[]{new {host=host.Text.Trim(),port=(int)port.Value}});}
        if(source==null)
        {
            var added=await c.RequestAsync("api/sources",new {name=name.Text.Trim(),type="reticulum",enabled=true,config=conf});
            await LoadSourcesAsync(added.String("id")??added.Property("data").String("id"));return;
        }
        var latest=(await c.SourceConfiguration(source)).Property("config");
        if(latest.GetRawText()!=initial.GetRawText())throw new InvalidOperationException("Connection settings changed. Reopen the editor.");
        await c.UpdateSource(source,new() {["name"]=name.Text.Trim(),["config"]=conf});await LoadSourcesAsync(source.Id);
    }
    private async Task ReticulumDirectory(MeshApi c,SourceRow s)
    {
        string[] routes=["status","destinations","interfaces","paths","messages","device-info"];
        var picker=new ComboBox{Header="View",ItemsSource=routes,SelectedIndex=0};
        if(await Form("Reticulum · "+s.Name,"Open",picker).ShowAsync()!=ContentDialogResult.Primary)return;
        await ShowResult("Reticulum · "+picker.SelectedItem,Pretty(await c.RequestAsync(SourceEndpoint(s,"reticulum/"+picker.SelectedItem))));
    }
    private async Task SendLxmf(MeshApi c,SourceRow s)
    {
        var to=new TextBox{Header="Destination hash"};var title=new TextBox{Header="Title"};var body=Multiline("Message","");
        if(await Form("LXMF · "+s.Name,"Send",to,title,body).ShowAsync()!=ContentDialogResult.Primary)return;
        await c.RequestAsync(SourceEndpoint(s,"reticulum/messages"),new {to=to.Text.Trim(),title=title.Text,content=body.Text});
        ShowNotice("Message accepted by the bridge.",InfoBarSeverity.Success);
    }
    private async Task AdvancedMqtt(MeshApi c,SourceRow s)
    {
        var initial=(await c.SourceConfiguration(s)).Property("config");
        var original=JsonNode.Parse(initial.GetRawText())!.AsObject();
        bool broker=s.Type=="meshcore_mqtt_broker";
        var keys=broker?new[]{"allowedObservers","forwarding"}:
            new[]{"downlinkFilters","uplinkFilters","downlinkTopicRewrite","uplinkTopicRewrite"};
        var fields=new List<FrameworkElement>();var inputs=new Dictionary<string,TextBox>();
        foreach(string key in keys)
        {
            var value=key=="allowedObservers"?original["auth"]?[key]:original[key];
            if (key == "forwarding" && value is JsonObject forwarding)
            {
                value = forwarding.DeepClone();
                if (value["targets"] is JsonArray targets)
                    foreach (var entry in targets.OfType<JsonObject>()) entry.Remove("password");
            }
            var field=Multiline(key,value?.ToJsonString(new JsonSerializerOptions{WriteIndented=true})??"null");
            inputs[key]=field;fields.Add(field);
        }
        fields.Insert(0,Wrapped(broker?"Observer keys are a JSON array. Forwarding settings contain the target list.":
            "Filters use the bridge filter schema. A rewrite uses from and to topic prefixes and requires a linked broker. Use null to clear a setting."));
        var error=DialogError();fields.Add(error);
        var dlg=Form(s.Name,"Save settings",fields.ToArray());
        var config=original.DeepClone().AsObject();
        dlg.PrimaryButtonClick+=(_,a)=>{
            try{
                foreach(var key in keys){var value=JsonNode.Parse(inputs[key].Text);
                    if(key=="allowedObservers" && config["auth"] is not JsonObject)config["auth"]=new JsonObject();
                    var target=key=="allowedObservers"?config["auth"]!.AsObject():config;
                    if(value==null)target.Remove(key);else target[key]=value;}
            } catch(Exception ex){a.Cancel=true;error.Text=ex.Message;}
        };
        if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
        if((await c.SourceConfiguration(s)).Property("config").GetRawText()!=initial.GetRawText())throw new InvalidOperationException("Settings changed. Reopen the editor.");
        if (config["forwarding"]?["targets"] is JsonArray updated &&
            original["forwarding"]?["targets"] is JsonArray existing)
        {
            foreach (var target in updated.OfType<JsonObject>())
            {
                var prior = existing.OfType<JsonObject>().FirstOrDefault(item =>
                    JsonNode.DeepEquals(item["id"], target["id"]));
                if (prior != null && !JsonNode.DeepEquals(prior["url"], target["url"]) &&
                    !target.ContainsKey("password"))
                    target["password"] = null;
            }
        }
        if(config["upstream"] is JsonObject upstream)upstream.Remove("password");
        if(config["auth"] is JsonObject auth)auth.Remove("password");
        await c.UpdateSource(s,new(){["config"]=config});
        ShowNotice("Source settings saved.",InfoBarSeverity.Success);
    }
}

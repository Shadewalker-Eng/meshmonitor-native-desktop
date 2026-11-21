using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private void ConfigureAutomationTools(Func<string,MenuFlyoutSubItem> group,ToolItem add,SourceRow s)
    {
        var g=group("Automations");
        add(g,"Create rule",c=>Automation(c,"create"));
        add(g,"Edit rule",c=>Automation(c,"edit"));
        add(g,"Enable or disable",c=>Automation(c,"toggle"));
        add(g,"Run history",c=>Automation(c,"history"));
        var alerts=group("Notifications");
        add(alerts,"Event subscriptions and destinations",c=>NotificationSettings(c,s),signedIn);
        add(alerts,"Delivery status",async c=>{
            var push=await c.RequestAsync("api/push/status");
            string result="Push delivery\n"+Pretty(push);
            if(radioAdmin)result+="\nApprise\n"+Pretty(await c.RequestAsync("api/apprise/status"));
            await ShowResult("Notification delivery",result);
        });
        add(alerts,"Send Apprise test",async c=>{
            if(await Form("Send test notification","Send",Wrapped("Send a test to the destinations configured for "+s.Name+"?")).ShowAsync()==ContentDialogResult.Primary)
                await ShowResult("Delivery result",Pretty(await c.RequestAsync("api/apprise/test",new {sourceId=s.Id})));
        },radioAdmin&&!s.IsUnified);
    }

    private static TextBox Multiline(string header,string value) => new() { Header=header,Text=value,AcceptsReturn=true,
        TextWrapping=TextWrapping.Wrap,MinHeight=65,MaxHeight=230 };
    private async Task Automation(MeshApi c,string action)
    {
        const string path="api/automations";
        JsonElement existing=default;
        string endpoint=path;
        if(action!="create")
        {
            var choice=await ChooseRecord("Automations",Records(await c.RequestAsync(path)),
                r=>$"{r.String("name")} · {(r.Flag("enabled")==true?"Enabled":"Disabled")}", action=="history"?"View runs":"Select");
            if(choice==null)return;
            endpoint+="/"+Esc(choice.Value.Property("id").ToString());
            existing=await c.RequestAsync(endpoint);
            if(action=="history"){await ShowResult("Recent automation runs",Pretty(await c.RequestAsync(endpoint+"/runs?limit=100")));return;}
            if(action=="toggle")
            {
                bool on=existing.Flag("enabled")==true || existing.Number("enabled")==1;
                if(await Form(existing.String("name")??"Rule",on?"Disable":"Enable",Wrapped(on?"Stop this rule from triggering.":"Allow this rule to execute its configured actions.")).ShowAsync()!=ContentDialogResult.Primary)return;
                await c.RequestAsync(endpoint+(on?"/disable":"/enable"),new {});
                return;
            }
        }
        var catalog=await c.RequestAsync(path+"/catalog");
        var trigger=new ComboBox{Header="Trigger",ItemsSource=Records(catalog.Property("triggers")).Select(r=>r.ToString()).ToArray(),SelectedIndex=0};
        var effect=new ComboBox{Header="Action",ItemsSource=Records(catalog.Property("actions")).Select(r=>r.ToString()).ToArray(),SelectedIndex=0};
        var name=new TextBox{Header="Rule name",Text=existing.String("name")??""};
        var desc=new TextBox{Header="Description",Text=existing.String("description")??""};
        var rule=Multiline("Rule graph",existing.String("config")??"");
        void Build() => rule.Text=Pretty(JsonSerializer.SerializeToElement(new {version=1,nodes=new[]{
            new {id="trigger",type=(string)trigger.SelectedItem,@params=new {}},new{id="action",type=(string)effect.SelectedItem,@params=new {}}},
            edges=new[]{new {from="trigger",to="action"}}}));
        if(action=="create")Build();
        var template=new Button{Content="Use selected trigger and action"};
        template.Click+=(_,_)=>Build();
        var error=DialogError();
        var blocks=BlockParameters(rule,error);
        var dlg=Form(action=="create"?"Create disabled rule":"Edit automation","Save",name,desc,trigger,effect,template,RuleBranches(rule,error),blocks,rule,error);
        JsonNode? graph=null;
        dlg.PrimaryButtonClick+=(_,args)=>{
            try{graph=JsonNode.Parse(rule.Text);if(graph is not JsonObject || string.IsNullOrWhiteSpace(name.Text))throw new FormatException("Enter a name and a JSON rule graph.");}
            catch(Exception ex){args.Cancel=true;error.Text=ex.Message;}
        };
        if(await dlg.ShowAsync()!=ContentDialogResult.Primary)return;
        await c.RequestAsync(endpoint,new {name=name.Text.Trim(),description=desc.Text,config=graph},action=="create"?HttpMethod.Post:HttpMethod.Put);
        ShowNotice("Automation saved.",InfoBarSeverity.Success);
    }

    private async Task NotificationSettings(MeshApi c,SourceRow s)
    {
        string path="api/push/preferences"+(s.IsUnified?"/mesh":"");
        var value=await c.RequestAsync(path+(s.IsUnified?"":"?"+Scope(s)));
        if(s.IsUnified && value.Property("preferences").ValueKind==JsonValueKind.Object)value=value.Property("preferences");
        var panel=new StackPanel{Spacing=6};
        var edits=new Dictionary<string,Func<JsonNode?>>();
        foreach(var prop in value.EnumerateObject())
        {
            string key=prop.Name;
            if(key is "usingDefaults" or "isOverride" or "usingMeshDefault" or "sourceFallback" or "userId" or "sourceId" or "id" or "updatedAt" or "createdAt" or "exists" or "sources" or "overrides")continue;
            if(prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var box=new CheckBox{Content=key,IsChecked=prop.Value.GetBoolean()};panel.Children.Add(box);
                edits[key]=()=>JsonValue.Create(box.IsChecked==true);
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var box = new TextBox { Header = key, Text = prop.Value.GetString() ?? "" };
                panel.Children.Add(box);
                edits[key] = () => JsonValue.Create(box.Text);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String))
            {
                var box = Multiline(key + " (one per line)", string.Join(Environment.NewLine, prop.Value.EnumerateArray().Select(v => v.GetString())));
                panel.Children.Add(box);
                edits[key] = () => JsonSerializer.SerializeToNode(box.Text.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if(prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null)
            {
                var field = StructuredField(key, JsonNode.Parse(prop.Value.GetRawText()), "Default", out var readValue);
                panel.Children.Add(field); edits[key] = readValue;
            }
        }
        var err=DialogError();panel.Children.Add(err);
        var dlg=Form("Notification preferences · "+s.Name,"Save changes",panel);
        var patch=new JsonObject();
        dlg.PrimaryButtonClick+=(_,a)=>{
            patch.Clear();
            try{foreach(var (key,read) in edits){var updated=read();if(!JsonNode.DeepEquals(updated,JsonNode.Parse(value.Property(key).GetRawText())))patch[key]=updated;}}
            catch(Exception ex) when(ex is JsonException or FormatException){a.Cancel=true;err.Text=ex.Message;}
        };
        if(await dlg.ShowAsync()!=ContentDialogResult.Primary || patch.Count==0)return;
        if(!s.IsUnified)patch["sourceId"]=s.Id;
        await c.RequestAsync(path,patch);
        ShowNotice("Notification preferences saved.",InfoBarSeverity.Success);
    }
}

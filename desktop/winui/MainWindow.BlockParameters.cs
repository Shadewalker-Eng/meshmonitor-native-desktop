using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private static readonly Lazy<JsonElement> blockCatalog = new(() =>
    {
        using var stream=typeof(MainWindow).Assembly.GetManifestResourceStream("AutomationCatalog.json")!;
        using var doc=JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    });

    private static Expander BlockParameters(TextBox graphText,TextBlock error)
    {
        var panel=new StackPanel{Spacing=6};
        var selected=new ComboBox{Header="Rule block"};
        var inputs=new StackPanel{Spacing=6};
        var apply=new Button{Content="Apply block changes"};
        panel.Children.Add(selected);panel.Children.Add(inputs);panel.Children.Add(apply);
        var expander=new Expander{Header="Edit block parameters",Content=panel};
        JsonObject[] nodes=[];
        string original="";
        var readers=new Dictionary<string,Func<JsonNode?>>();
        void Load()
        {
            try
            {
                original=graphText.Text;
                nodes=(JsonNode.Parse(original)?["nodes"] as JsonArray)?.OfType<JsonObject>().ToArray()??[];
                selected.ItemsSource=nodes.Select(n=>$"{n["id"]} · {n["type"]}").ToArray();
                selected.SelectedIndex=nodes.Length>0?0:-1;
                error.Text="";
            }
            catch(Exception ex){nodes=[];selected.ItemsSource=null;inputs.Children.Clear();readers.Clear();error.Text=ex.Message;}
        }
        expander.Expanding+=(_,_)=>Load();
        selected.SelectionChanged+=(_,_)=>
        {
            inputs.Children.Clear();readers.Clear();
            if(selected.SelectedIndex<0 || selected.SelectedIndex>=nodes.Length)return;
            var n=nodes[selected.SelectedIndex];var parameters=n["params"] as JsonObject;
            foreach(var field in Records(blockCatalog.Value.Property(n["type"]?.ToString()??"").Property("fields")))
            {
                string key=field.String("name")!,label=field.String("label")??key,kind=field.String("kind")??"text";
                var old=parameters?[key];
                if(kind=="checkbox")
                {
                    var box=new CheckBox{Content=label,IsThreeState=true,IsChecked=old?.GetValue<bool>()};
                    readers[key]=()=>box.IsChecked.HasValue?JsonValue.Create(box.IsChecked.Value):null; inputs.Children.Add(box);
                }
                else if(kind=="select" && Records(field.Property("options")) is {Length:>0} opts)
                {
                    var values=opts.Select(o=>o.String("value")??"").Prepend("").ToList();
                    var labels=opts.Select(o=>o.String("label")??"").Prepend("Default").ToList();
                    string stored=old?.ToString()??"";
                    if(!values.Contains(stored)){values.Add(stored);labels.Add(stored);}
                    var choice=new ComboBox{Header=label,ItemsSource=labels,SelectedIndex=values.IndexOf(stored)};
                    readers[key]=()=>choice.SelectedIndex<=0?null:JsonValue.Create(values[choice.SelectedIndex]); inputs.Children.Add(choice);
                }
                else
                {
                    bool structured=old is JsonObject or JsonArray || kind is "sourceMulti" or "sendSourceMulti" or "channelMulti" or "nodeMulti" or "geofence" or "tracePaths";
                    if (structured)
                    {
                        inputs.Children.Add(StructuredField(label, old, old == null ? "Default" : "List", out var getValue));
                        readers[key] = getValue;
                        continue;
                    }
                    string initial=old?.ToString()??"";
                    var box=new TextBox{Header=label,Text=initial,PlaceholderText=field.String("placeholder")??"",
                        AcceptsReturn=kind=="textarea",TextWrapping=TextWrapping.Wrap};
                    readers[key]=()=>
                    {
                        if(box.Text==initial)return old?.DeepClone();
                        if(string.IsNullOrWhiteSpace(box.Text))return null;
                        if(kind=="number")
                        {
                            if(!double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double number)||!double.IsFinite(number))
                                throw new FormatException("Enter a number for "+label);
                            return JsonValue.Create(number);
                        }
                        return JsonValue.Create(box.Text);
                    };
                    inputs.Children.Add(box);
                }
            }
        };
        apply.Click+=(_,_)=>
        {
            try
            {
                if(selected.SelectedIndex<0)return;
                if(original!=graphText.Text){Load();error.Text="The graph changed. Edit the reloaded block.";return;}
                var graph=JsonNode.Parse(original)!.AsObject();
                var node=graph["nodes"]![selected.SelectedIndex]!.AsObject();
                var parameters=node["params"] as JsonObject??new JsonObject();
                if(node["params"]==null)node["params"]=parameters;
                foreach(var (key,read) in readers){var v=read();if(v==null)parameters.Remove(key);else parameters[key]=v;}
                original=graph.ToJsonString(new JsonSerializerOptions{WriteIndented=true}); graphText.Text=original;
                expander.IsExpanded=false; error.Text="Block updated. Save the rule to apply it.";
            }
            catch(Exception ex){error.Text=ex.Message;}
        };
        return expander;
    }
}

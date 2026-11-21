using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private static Expander BridgeRuleFields(string key, JsonNode? stored, out Func<JsonNode?> read)
    {
        var panel = new StackPanel { Spacing = 6 };
        var updates = new List<Action<JsonObject>>();
        bool rewrite = key.EndsWith("Rewrite", StringComparison.Ordinal);
        void Field(string group, string name, bool list, bool numeric)
        {
            var value = group.Length == 0 ? stored?[name] : stored?[group]?[name];
            string initial = value is JsonArray array ? string.Join(Environment.NewLine, array.Select(v => v?.ToString())) : value?.ToString() ?? "";
            var box = new TextBox { Header = (group + " " + name).Trim(), Text = initial, AcceptsReturn = list };
            panel.Children.Add(box);
            updates.Add(result =>
            {
                if (box.Text == initial) return;
                var target = group.Length == 0 ? result : result[group] as JsonObject;
                if (target == null) result[group] = target = new JsonObject();
                string text = box.Text.Trim();
                if (text.Length == 0) target.Remove(name);
                else if (list)
                {
                    string[] lines = text.Split(new[]{'\r','\n'}, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    target[name] = numeric ? JsonSerializer.SerializeToNode(lines.Select(int.Parse)) : JsonSerializer.SerializeToNode(lines);
                }
                else if (numeric) target[name] = double.Parse(text, CultureInfo.InvariantCulture);
                else target[name] = text;
                if (group.Length > 0 && target.Count == 0) result.Remove(group);
            });
        }
        if (rewrite)
        {
            Field("", "from", false, false);
            Field("", "to", false, false);
        }
        else
        {
            foreach (string group in new[]{"topics","channels","nodes","portnums"})
                foreach (string rule in new[]{"allow","block"}) Field(group, rule, true, group == "portnums");
            foreach (string bound in new[]{"minLat","maxLat","minLng","maxLng"}) Field("geo", bound, false, true);
        }
        read = () =>
        {
            var result = stored?.DeepClone().AsObject() ?? new JsonObject();
            foreach (var update in updates) update(result);
            return result.Count == 0 ? null : result;
        };
        string direction = key.StartsWith("downlink") ? "Downlink" : "Uplink";
        return new Expander { Header = direction + (rewrite ? " topic rewrite" : " filters"), Content = panel };
    }
}

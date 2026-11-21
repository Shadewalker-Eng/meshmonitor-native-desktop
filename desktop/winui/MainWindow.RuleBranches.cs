using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private static Expander RuleBranches(TextBox graphText, TextBlock error)
    {
        var parent = new ComboBox { Header = "After block" };
        var kind = new ComboBox { Header = "New block type", ItemsSource = blockCatalog.Value.EnumerateObject()
            .Select(p => p.Name).Where(k => k.StartsWith("condition.") || k.StartsWith("action.")).Order().ToArray(), SelectedIndex = 0 };
        var branch = new CheckBox { Content = "Run as a parallel branch" };
        var insert = new Button { Content = "Insert block" };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(parent); panel.Children.Add(kind); panel.Children.Add(branch); panel.Children.Add(insert);
        var expander = new Expander { Header = "Insert a condition, action, or branch", Content = panel };
        void Reload()
        {
            var nodes = JsonNode.Parse(graphText.Text)?["nodes"] as JsonArray;
            parent.ItemsSource = nodes?.OfType<JsonObject>().Select(n => n["id"]!.ToString()).ToArray();
            parent.SelectedIndex = parent.Items.Count > 0 ? 0 : -1;
        }
        expander.Expanding += (_, _) => { try { Reload(); } catch (Exception ex) { error.Text = ex.Message; } };
        insert.Click += (_, _) =>
        {
            try
            {
                if (parent.SelectedItem is not string from || kind.SelectedItem is not string type) return;
                var graph = JsonNode.Parse(graphText.Text)!.AsObject();
                var nodes = graph["nodes"]!.AsArray();
                var edges = graph["edges"]!.AsArray();
                if (!nodes.Any(n => n?["id"]?.ToString() == from)) throw new FormatException("Select an existing block.");
                var outgoing = edges.OfType<JsonObject>().Where(e => e["from"]?.ToString() == from).ToArray();
                string AddNode(string blockType)
                {
                    string id = "b_" + Guid.NewGuid().ToString("N")[..12];
                    nodes.Add(new JsonObject { ["id"] = id, ["type"] = blockType, ["params"] = new JsonObject() });
                    return id;
                }
                void Connect(string a, string b) => edges.Add(new JsonObject { ["from"] = a, ["to"] = b });
                string added = AddNode(type);
                if (branch.IsChecked == true && outgoing.Length > 0)
                {
                    string fork = AddNode("flow.fanout");
                    foreach (var e in outgoing) e["from"] = fork;
                    Connect(from, fork);
                    Connect(fork, added);
                }
                else
                {
                    foreach (var e in outgoing) e["from"] = added;
                    Connect(from, added);
                }
                graphText.Text = graph.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                Reload();
                parent.SelectedItem = added;
                error.Text = "Block inserted. Set its parameters before saving the rule.";
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        return expander;
    }
}

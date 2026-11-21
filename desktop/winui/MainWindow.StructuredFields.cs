using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private static FrameworkElement StructuredField(string label, JsonNode? stored, string defaultKind, out Func<JsonNode?> read)
    {
        var kind = new ComboBox { Header = "Value type", ItemsSource = new[] { "Default", "Text", "Number", "Boolean", "List", "Object" } };
        var body = new StackPanel { Spacing = 4 };
        var controls = new StackPanel { Spacing = 4 };
        controls.Children.Add(kind);
        controls.Children.Add(body);
        string initialKind = stored?.GetValueKind() switch
        {
            JsonValueKind.Object => "Object", JsonValueKind.Array => "List", JsonValueKind.String => "Text",
            JsonValueKind.Number => "Number", JsonValueKind.True or JsonValueKind.False => "Boolean", _ => defaultKind
        };
        Func<JsonNode?> current = () => null;
        void Build(JsonNode? value)
        {
            body.Children.Clear();
            string selected = (string)kind.SelectedItem;
            if (selected == "Default") { current = () => null; return; }
            if (selected == "Boolean")
            {
                var toggle = new CheckBox { Content = label, IsChecked = value?.GetValue<bool>() == true };
                body.Children.Add(toggle); current = () => JsonValue.Create(toggle.IsChecked == true); return;
            }
            if (selected is "Text" or "Number")
            {
                var text = new TextBox { Header = label, Text = value?.ToString() ?? "" };
                body.Children.Add(text);
                current = () => selected == "Text" ? JsonValue.Create(text.Text) :
                    JsonNode.Parse(text.Text) is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n :
                    throw new FormatException("Enter a number for " + label);
                return;
            }
            bool named = selected == "Object";
            var entries = new List<(TextBox Key, Func<JsonNode?> Get)>();
            var rows = new StackPanel { Spacing = 6 };
            body.Children.Add(rows);
            void Add(string key, JsonNode? item)
            {
                var row = new StackPanel { Spacing = 3 };
                var field = new TextBox { Header = "Field name", Text = key };
                if (named) row.Children.Add(field);
                row.Children.Add(StructuredField(named ? "Field value" : "Item", item, item == null ? "Default" : "Text", out var getter));
                entries.Add((field, getter));
                var remove = new Button { Content = "Remove" };
                remove.Click += (_, _) => { entries.Remove((field, getter)); rows.Children.Remove(row); };
                row.Children.Add(remove); rows.Children.Add(row);
            }
            if (value is JsonObject obj) foreach (var pair in obj) Add(pair.Key, pair.Value);
            if (value is JsonArray arr) foreach (var item in arr) Add("", item);
            var add = new Button { Content = named ? "Add field" : "Add item" };
            add.Click += (_, _) => Add("", null);
            body.Children.Add(add);
            current = () =>
            {
                if (!named) return new JsonArray(entries.Select(e => e.Get()).ToArray());
                var result = new JsonObject();
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Key.Text) || result.ContainsKey(e.Key.Text))
                        throw new FormatException("Enter a distinct name for each field in " + label);
                    result[e.Key.Text] = e.Get();
                }
                return result;
            };
        }
        kind.SelectedItem = initialKind;
        Build(stored);
        kind.SelectionChanged += (_, _) => Build(null);
        read = () => current();
        return new Expander { Header = label, Content = controls };
    }
}

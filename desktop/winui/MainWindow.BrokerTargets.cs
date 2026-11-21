using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private static Expander BrokerTargets(JsonNode? previous, out Func<JsonNode?> collect)
    {
        var root = previous?.DeepClone().AsObject() ?? new JsonObject();
        var panel = new StackPanel { Spacing = 8 };
        var name = new TextBox { Header = "Own observer identity name", Text = root["ownIdentityName"]?.ToString() ?? "" };
        panel.Children.Add(name);
        var forms = new StackPanel { Spacing = 6 };
        panel.Children.Add(forms);
        var readers = new List<Func<JsonObject>>();
        void AddTarget(JsonObject initial)
        {
            var fields = new StackPanel { Spacing = 5 };
            var get = new Dictionary<string,Func<JsonNode?>>();
            foreach (string key in new[]{"label","url","username","tokenAudience","observers","iataFilter"})
            {
                bool lines = key is "observers" or "iataFilter";
                var stored = initial[key];
                string initialText = lines && stored is JsonArray arr ? string.Join(Environment.NewLine, arr.Select(v => v?.ToString())) : stored?.ToString() ?? "";
                var text = new TextBox { Header = key, Text = initialText, AcceptsReturn = lines };
                fields.Children.Add(text);
                get[key] = () => text.Text == initialText ? stored?.DeepClone() : lines
                    ? JsonSerializer.SerializeToNode(text.Text.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    : JsonValue.Create(text.Text.Trim());
            }
            var password = new PasswordBox { Header = "Replacement password" };
            var mode = new ComboBox { Header = "Identity", ItemsSource = new[]{"passthrough","own"}, SelectedIndex = initial["identityMode"]?.ToString()=="own"?1:0 };
            var enabled = new CheckBox { Content = "Enabled", IsChecked = initial["enabled"]?.GetValue<bool>() == true };
            var tls = new CheckBox { Content = "Verify upstream TLS certificate", IsChecked = initial["rejectUnauthorized"]?.GetValue<bool>() ?? true };
            var remove = new CheckBox { Content = "Remove this target when saved" };
            fields.Children.Add(mode);fields.Children.Add(enabled);fields.Children.Add(tls);fields.Children.Add(password);fields.Children.Add(remove);
            forms.Children.Add(new Expander { Header = initial["label"]?.ToString() ?? initial["url"]?.ToString() ?? "New forwarding target", Content = fields });
            readers.Add(() =>
            {
                if (remove.IsChecked == true) return new JsonObject();
                var target = initial.DeepClone().AsObject();
                target.Remove("password");
                foreach (var (key,read) in get)
                {
                    var value = read();
                    if (value == null) target.Remove(key); else target[key] = value;
                }
                if (string.IsNullOrWhiteSpace(target["url"]?.ToString())) throw new FormatException("Enter a URL for each forwarding target.");
                target["identityMode"] = (string)mode.SelectedItem;
                target["enabled"] = enabled.IsChecked == true;
                target["rejectUnauthorized"] = tls.IsChecked == true;
                if (password.Password.Length > 0) target["password"] = password.Password;
                return target;
            });
        }
        foreach (var target in (root["targets"] as JsonArray ?? []).OfType<JsonObject>()) AddTarget(target);
        var add = new Button { Content = "Add forwarding target" };
        add.Click += (_, _) => AddTarget(new JsonObject { ["id"] = Guid.NewGuid().ToString("N") });
        panel.Children.Add(add);
        collect = () =>
        {
            var value = root.DeepClone().AsObject();
            if (name.Text.Trim().Length > 0) value["ownIdentityName"] = name.Text.Trim(); else value.Remove("ownIdentityName");
            var targets = new JsonArray();
            foreach (var read in readers) { var target = read(); if (target.Count > 0) targets.Add(target); }
            value["targets"] = targets;
            return value;
        };
        return new Expander { Header = "Forwarding targets", Content = panel };
    }
}

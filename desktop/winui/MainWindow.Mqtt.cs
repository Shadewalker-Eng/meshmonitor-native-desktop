using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private sealed class MqttFields
    {
        private readonly TextBox url = new() { Header = "Broker URL", PlaceholderText = "mqtts://broker.example.org:8883" };
        private readonly TextBox username = new() { Header = "Username (optional)" };
        private readonly PasswordBox password = new() { Header = "Password (optional)", PasswordRevealMode = PasswordRevealMode.Hidden };
        private readonly TextBox subscription = new();
        private readonly ToggleSwitch connection = new();
        private readonly ComboBox bridgeMode = new() { Header = "Bridge direction" };
        private readonly ComboBox brokerSource = new() { Header = "Local Meshtastic MQTT broker" };
        private readonly ComboBox publisherMode = new() { Header = "Upstream publisher connections" };
        private readonly TextBox downlinkBlock = new() { Header = "Block incoming topics", AcceptsReturn = true, MaxHeight = 80 };
        private readonly TextBox uplinkAllow = new() { Header = "Allow outgoing topics", AcceptsReturn = true, MaxHeight = 80 };
        private readonly (string Id, string Name)[] brokers;
        private static readonly string[] Modes = ["subscribe_only", "publish_only", "bidirectional"];
        private readonly bool editing;
        internal StackPanel View { get; } = new() { Spacing = 10 };
        internal MqttSourceSettings Initial { get; }
        internal string Password => password.Password;
        internal bool Enabled => connection.IsOn;

        internal MqttFields(bool bridge, JsonElement config = default, SourceRow[]? sources = null)
        {
            editing = config.ValueKind == JsonValueKind.Object;
            Initial = MqttSourceSettings.From(bridge, config);
            var choices = (sources ?? []).Where(source => source.Type == "mqtt_broker")
                .Select(source => (source.Id, source.Description)).ToList();
            if (Initial.Forwarding.ParentId is { } parent && !choices.Any(choice => choice.Id == parent))
                choices.Add((parent, "Current broker (unavailable)"));
            brokers = [("", "Standalone"), .. choices];
            url.Text = Initial.Url;
            username.Text = Initial.Username;
            if (editing) password.Header = "New password (optional)";
            subscription.Header = bridge ? "Topic filters (one per line)" : "Region";
            subscription.Text = Initial.Subscription;
            if (bridge)
            {
                subscription.AcceptsReturn = true;
                subscription.MinHeight = 54;
                subscription.MaxHeight = 90;
                connection.Header = "Enable source";
                connection.IsOn = true;
            }
            else
            {
                subscription.PlaceholderText = "DEN";
                connection.Header = "Connect automatically";
                connection.IsOn = Initial.AutoConnect;
            }
            View.Children.Add(url);
            View.Children.Add(subscription);
            View.Children.Add(username);
            View.Children.Add(password);
            if (!bridge || !editing) View.Children.Add(connection);
            if (bridge)
            {
                var forwarding = Initial.Forwarding;
                bridgeMode.ItemsSource = new[] { "Receive only", "Send only", "Send and receive" };
                bridgeMode.SelectedIndex = Array.IndexOf(Modes, forwarding.Mode);
                brokerSource.ItemsSource = brokers.Select(choice => choice.Name).ToArray();
                brokerSource.SelectedIndex = Array.FindIndex(brokers, choice => choice.Id == (forwarding.ParentId ?? ""));
                publisherMode.ItemsSource = new[] { "One per gateway", "Single connection" };
                publisherMode.SelectedIndex = forwarding.PublisherMode == "single" ? 1 : 0;
                downlinkBlock.Text = forwarding.DownlinkBlock;
                uplinkAllow.Text = forwarding.UplinkAllow;
                var details = new StackPanel { Spacing = 8 };
                foreach (var field in new Control[] { bridgeMode, brokerSource, publisherMode, downlinkBlock, uplinkAllow })
                    details.Children.Add(field);
                View.Children.Add(new Expander { Header = "Bridge forwarding and topic filters", Content = details });
            }
            if (editing)
                View.Children.Add(Wrapped("A blank password keeps the saved password for the same broker. Changing the broker clears it."));
        }

        internal string Read(out MqttSourceSettings settings)
        {
            settings = Initial with
            {
                Url = url.Text.Trim(), Username = username.Text.Trim(),
                Subscription = Initial.Bridge ? MqttSourceSettings.FormatTopics(subscription.Text) : subscription.Text.Trim(),
                AutoConnect = Initial.Bridge ? Initial.AutoConnect : connection.IsOn
            };
            if ((!editing || settings.Url != Initial.Url || Password.Length > 0) && !settings.IsValidUrl())
                return "Enter a broker address or MQTT URL. Enter credentials in the separate fields.";
            if (Initial.Bridge)
            {
                string mode = bridgeMode.SelectedIndex >= 0 ? Modes[bridgeMode.SelectedIndex] : Initial.Forwarding.Mode;
                string? parent = brokerSource.SelectedIndex > 0 ? brokers[brokerSource.SelectedIndex].Id : null;
                var forwarding = new BridgeForwarding(mode, parent,
                    publisherMode.SelectedIndex == 1 ? "single" : "per_gateway",
                    MqttSourceSettings.FormatTopics(downlinkBlock.Text), MqttSourceSettings.FormatTopics(uplinkAllow.Text));
                if (!MqttSourceSettings.SplitTopics(forwarding.DownlinkBlock)
                    .Concat(MqttSourceSettings.SplitTopics(forwarding.UplinkAllow)).All(MqttSourceSettings.IsTopicFilter))
                    return "Enter valid MQTT topic filters, one per line.";
                settings = settings with { Forwarding = forwarding };
            }
            if (settings.Forwarding.Mode != "publish_only" &&
                (!editing || settings.Subscription != Initial.Subscription || Initial.Forwarding.Mode == "publish_only") &&
                !settings.HasValidSubscription())
                return Initial.Bridge ? "Enter at least one valid MQTT topic filter." : "Enter a region without spaces or MQTT wildcards.";
            return "";
        }
    }
}

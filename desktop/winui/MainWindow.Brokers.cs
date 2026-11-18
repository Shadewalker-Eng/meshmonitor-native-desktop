using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private sealed class BrokerFields
    {
        private readonly TextBox host = new() { Header = "Listen address" };
        private readonly TextBox port = new() { Header = "Listen port" };
        private readonly TextBox username = new() { Header = "MQTT username" };
        private readonly PasswordBox password = new() { Header = "MQTT password", PasswordRevealMode = PasswordRevealMode.Hidden };
        private readonly TextBox rootTopic = new() { Header = "Root topic" };
        private readonly ToggleSwitch autoConnect = new() { Header = "Start automatically" };
        private readonly bool editing;
        internal StackPanel View { get; } = new() { Spacing = 8 };
        internal EmbeddedBrokerSettings Initial { get; }
        internal string Password => password.Password;

        internal BrokerFields(bool meshCore, JsonElement config = default)
        {
            editing = config.ValueKind == JsonValueKind.Object;
            Initial = editing ? EmbeddedBrokerSettings.From(meshCore, config) : EmbeddedBrokerSettings.New(meshCore);
            host.Text = Initial.Host;
            port.Text = Initial.Port.ToString();
            username.Text = Initial.Username;
            rootTopic.Text = Initial.RootTopic;
            autoConnect.IsOn = Initial.AutoConnect;
            if (editing) password.Header = "New MQTT password (optional)";
            View.Children.Add(host);
            if (!editing) View.Children.Add(Wrapped("Use 127.0.0.1 for this PC, or your LAN address for other devices."));
            View.Children.Add(port);
            View.Children.Add(username);
            View.Children.Add(password);
            View.Children.Add(meshCore ? autoConnect : rootTopic);
        }

        internal string Read(out EmbeddedBrokerSettings settings)
        {
            settings = Initial;
            if (!TryTcpEndpoint(host.Text, port.Text, out int number))
                return "Enter a listen address and a port between 1 and 65535.";
            string login = username.Text.Trim();
            if ((!editing || login != Initial.Username || Password.Length > 0) && login.Length == 0)
                return "Enter a username for password authentication.";
            if ((!editing || login != Initial.Username) && string.IsNullOrWhiteSpace(Password))
                return "Enter a password for this username.";
            string topic = rootTopic.Text.Trim();
            if (!Initial.MeshCore && (topic.Length == 0 || topic.IndexOfAny(['+', '#', '\0']) >= 0 || topic.EndsWith('/')))
                return "Enter a root topic without wildcards or a trailing slash.";

            settings = Initial with
            {
                Host = NormalizeHost(host.Text), Port = number, Username = login,
                RootTopic = topic, AutoConnect = autoConnect.IsOn
            };
            return "";
        }
    }
}

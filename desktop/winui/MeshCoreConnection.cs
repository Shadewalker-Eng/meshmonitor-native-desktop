using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshMonitor.Native;

internal sealed record MeshCoreConnection(bool Tcp, string Host, int Port, string SerialPort,
    bool Repeater, bool AutoConnect)
{
    internal static MeshCoreConnection New { get; } = new(false, "", 5000, "", false, true);

    internal static MeshCoreConnection From(JsonElement config) => new(
        config.String("transport") == "tcp",
        config.String("tcpHost") ?? "",
        config.Number("tcpPort") is double port && port is >= 1 and <= 65535 && port == Math.Truncate(port)
            ? (int)port : 4403,
        config.String("serialPort") ?? config.String("port") ?? "",
        config.String("deviceType") == "repeater",
        config.Flag("autoConnect") != false);

    internal JsonObject Apply(JsonElement config = default)
    {
        bool editing = config.ValueKind == JsonValueKind.Object;
        var json = editing ? JsonNode.Parse(config.GetRawText())!.AsObject() : new JsonObject();
        var original = editing ? From(config) : null;
        if (original is null || original.Tcp != Tcp)
        {
            json["transport"] = Tcp ? "tcp" : "usb";
            if (Tcp)
            {
                json.Remove("serialPort");
                json.Remove("port");
            }
            else
            {
                json.Remove("tcpHost");
                json.Remove("tcpPort");
            }
        }
        if (Tcp)
        {
            if (original is null || !original.Tcp || original.Host != Host) json["tcpHost"] = Host;
            if (original is null || !original.Tcp || original.Port != Port) json["tcpPort"] = Port;
        }
        else if (original is null || original.Tcp || original.SerialPort != SerialPort)
        {
            json["serialPort"] = SerialPort;
            json.Remove("port");
        }
        if (original is null || original.Repeater != Repeater) json["deviceType"] = Repeater ? "repeater" : "companion";
        if (original is null || original.AutoConnect != AutoConnect) json["autoConnect"] = AutoConnect;
        return json;
    }
}

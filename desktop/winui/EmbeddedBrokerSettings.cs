using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshMonitor.Native;

internal sealed record EmbeddedBrokerSettings(bool MeshCore, string Host, int Port, string Username,
    string RootTopic, bool AutoConnect)
{
    internal static EmbeddedBrokerSettings New(bool meshCore) =>
        new(meshCore, "127.0.0.1", meshCore ? 1884 : 1883, "", "msh", true);

    internal static EmbeddedBrokerSettings From(bool meshCore, JsonElement config)
    {
        var listener = config.Property("listener");
        var auth = config.Property("auth");
        var defaults = New(meshCore);
        return defaults with
        {
            Host = listener.String("host") is { Length: > 0 } host ? host : "0.0.0.0",
            Port = listener.Number("port") is double port && port is >= 1 and <= 65535 && port == Math.Truncate(port)
                ? (int)port : defaults.Port,
            Username = auth.String("username") ?? "",
            RootTopic = config.String("rootTopic") ?? defaults.RootTopic,
            AutoConnect = config.Flag("autoConnect") != false
        };
    }

    internal JsonObject Apply(string name, string password, JsonElement config = default)
    {
        bool editing = config.ValueKind == JsonValueKind.Object;
        var json = editing ? JsonNode.Parse(config.GetRawText())!.AsObject() : new JsonObject();
        var original = editing ? From(MeshCore, config) : null;
        var listener = json["listener"] as JsonObject;
        if (listener is null)
        {
            listener = new JsonObject();
            json["listener"] = listener;
        }
        if (Host != original?.Host) listener["host"] = Host;
        if (Port != original?.Port) listener["port"] = Port;
        if (json["auth"] is not JsonObject auth)
        {
            auth = new JsonObject();
            json["auth"] = auth;
        }
        auth.Remove("password");
        if (Username != original?.Username) auth["username"] = Username;
        if (password.Length > 0) auth["password"] = password;

        if (MeshCore)
        {
            if (AutoConnect != original?.AutoConnect) json["autoConnect"] = AutoConnect;
        }
        else
        {
            if (RootTopic != original?.RootTopic) json["rootTopic"] = RootTopic;
            if (!editing)
            {
                uint node = 0x80000000u | (uint)RandomNumberGenerator.GetInt32(0, int.MaxValue);
                json["gateway"] = new JsonObject
                {
                    ["nodeNum"] = node,
                    ["nodeId"] = $"!{node:x8}",
                    ["longName"] = name,
                    ["shortName"] = name[..Math.Min(name.Length, 4)]
                };
            }
        }
        return json;
    }
}

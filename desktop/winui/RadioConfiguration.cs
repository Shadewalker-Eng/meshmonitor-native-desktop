using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace MeshMonitor.Native;

internal sealed record RadioField(string Key, string Label, object Default, int Minimum = 0, int Maximum = 0)
{
    internal string Kind { get; init; } = "";
    internal Dictionary<string, int>? Options { get; init; }
    internal string[]? Choices { get; init; }
    internal bool ReadOnly { get; init; }
    internal bool Secret { get; init; }
}

internal sealed record RadioSection(string Key, string Label, int? RequestType, RadioField[] Fields)
{
    internal static readonly RadioSection[] All = LoadCatalog();
    internal static readonly RadioSection[] Remote =
    [
        .. All.Select(section => section with
        {
            AdminCommand = section.Key switch
            {
                "owner" => "setOwner", "device" => "setDeviceConfig", "lora" => "setLoRaConfig",
                "position" => "setPositionConfig", "network" => "setNetworkConfig", "mqtt" => "setMQTTConfig",
                "neighborinfo" => "setNeighborInfoConfig", "telemetry" => "setTelemetryConfig",
                "statusmessage" => "setStatusMessageConfig", "trafficmanagement" => "setTrafficManagementConfig",
                "tak" => "setTAKConfig", "meshbeacon" => "setMeshBeaconConfig", _ => section.AdminCommand
            },
            ViewOnly = section.Key is "power" or "display" or "deviceui" || section.Module &&
                section.Key is not ("mqtt" or "neighborinfo" or "telemetry" or "statusmessage" or "trafficmanagement" or "tak" or "meshbeacon")
        }),
        .. Channels()
    ];
    internal string Field { get; init; } = Key;
    internal bool Module { get; init; }
    internal string? AdminCommand { get; init; }
    internal int? ChannelIndex { get; init; }
    internal string? MeshCoreReadPath { get; init; }
    internal bool ViewOnly { get; init; }
    internal bool ReadOnly => ViewOnly;
    internal string SavePath => Module && Key is not ("mqtt" or "neighborinfo") ? $"module/{Key}" : Key;

    internal static readonly RadioSection FixedPosition = new("fixed-position", "Fixed position coordinates", null,
        [new("latitude", "Latitude", "") { Kind = "double" }, new("longitude", "Longitude", "") { Kind = "double" },
         new("altitude", "Altitude (m)", "0") { Kind = "int32" }])
        { AdminCommand = "setFixedPosition" };

    internal static IEnumerable<RadioSection> Channels() => Enumerable.Range(0, 8).Select(index =>
        new RadioSection($"channel{index}", $"Channel {index}", null,
            [new("name", "Name", "", Maximum: 11), new("psk", "Pre-shared key (Base64)", "") { Secret = true, Kind = "bytes" },
             new("role", "Role", 0) { Kind = "enum", Options = new() { ["Disabled"] = 0, ["Primary"] = 1, ["Secondary"] = 2 } },
             new("uplinkEnabled", "Uplink enabled", false), new("downlinkEnabled", "Downlink enabled", false),
             new("positionPrecision", "Position precision", "32") { Kind = "uint32" },
             new("useAead", "Use AEAD", false)])
        { ChannelIndex = index, AdminCommand = "setChannel" });

    internal static readonly RadioSection[] MeshCore =
    [
        new("name", "Device name", null, [new("name", "Name", "", Maximum: 31)]),
        new("radio", "LoRa", null,
            [new("freq", "Frequency (MHz)", "") { Kind = "double" },
             new("bw", "Bandwidth (kHz)", "") { Kind = "double" },
             new("sf", "Spreading factor", "") { Kind = "uint32" },
             new("cr", "Coding rate", "") { Kind = "uint32" }]),
        new("tx-power", "Transmit power", null, [new("power", "Transmit power (dBm)", "") { Kind = "int32" }]),
        new("coords", "Position", null,
            [new("lat", "Latitude", "") { Kind = "double" }, new("lon", "Longitude", "") { Kind = "double" }]),
        new("discoverable", "Discovery", null, [new("enabled", "Respond to discovery", false)]) { MeshCoreReadPath = "discoverable" },
        new("default-scope", "Region scope", null, [new("scope", "Default scope", "", Maximum: 63)]) { MeshCoreReadPath = "default-scope" },
        new("ble-pin", "Bluetooth PIN", null, [new("pin", "New Bluetooth PIN", "") { Kind = "uint32", Secret = true }])
            { MeshCoreReadPath = "ble-pin" },
        new("advert-loc-policy", "Advert location", null,
            [new("policy", "Location in adverts", 0) { Kind = "enum", Options = new() { ["Disabled"] = 0, ["Enabled"] = 1 } }]),
        new("auto-add-contacts", "Contact discovery", null, [new("enabled", "Add contacts automatically", false)]),
        new("multi-acks", "Direct message acknowledgements", null,
            [new("value", "Extra acknowledgements", 0) { Kind = "enum", Options = new() { ["None"] = 0, ["One"] = 1 } }]),
        MeshCoreTelemetry("base", "Basic telemetry sharing"),
        MeshCoreTelemetry("loc", "Location telemetry sharing"),
        MeshCoreTelemetry("env", "Environment telemetry sharing")
    ];

    private static RadioSection MeshCoreTelemetry(string key, string label) =>
        new($"telemetry-mode-{key}", label, null, [new("mode", "Sharing mode", "") { Choices = ["always", "device", "never"] }]);

    private static RadioSection[] LoadCatalog()
    {
        using var stream = typeof(RadioSection).Assembly.GetManifestResourceStream("RadioCatalog.json")!;
        using var document = JsonDocument.Parse(stream);
        var owner = new RadioSection("owner", "Owner", null,
            [new("longName", "Long name", "", Maximum: 39), new("shortName", "Short name", "", Maximum: 4),
             new("isUnmessagable", "Unmessageable", false), new("isLicensed", "Licensed operator", false)]);
        return [owner, .. document.RootElement.EnumerateArray().Select(section =>
        {
            string key = section.String("id")!;
            return new RadioSection(key, section.String("label")!, (int)section.Number("adminType")!,
                section.GetProperty("fields").EnumerateArray().Where(field =>
                    field.String("key") is not ("privateKey" or "pinCode" or "calibrationData"))
                    .Select(field => CreateField(key, field)).ToArray())
            {
                Field = section.String("field")!,
                Module = section.String("kind") == "module",
                AdminCommand = key switch { "security" => "setSecurityConfig", "bluetooth" => "setBluetoothConfig", _ => null }
            };
        })];
    }

    private static RadioField CreateField(string section, JsonElement field)
    {
        string key = field.String("key")!;
        string kind = field.String("kind")!;
        var options = field.Property("options");
        var choices = options.ValueKind == JsonValueKind.Object
            ? options.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetInt32()) : null;
        object initial = kind switch
        {
            "bool" => false,
            "enum" => 0,
            "json" => field.Flag("array") == true ? "[]" : "{}",
            "string" or "bytes" => "",
            _ => "0"
        };
        return new(key, field.String("label")!, initial)
        {
            Kind = kind, Options = choices,
            ReadOnly = key is "transparentMode" or "publicKey" ||
                section == "deviceui" && key is not ("screenBrightness" or "screenTimeout" or "theme" or "alertEnabled" or "bannerEnabled" or "ringToneId" or "language" or "compassMode" or "screenRgbColor" or "isClockfaceAnalog" or "gpsFormat") ||
                section == "device" && key is "isManaged" or "serialEnabled" ||
                section == "position" && key is "gpsEnabled" or "gpsAttemptTime",
            Secret = key is "password" or "wifiPsk" or "fixedPin"
        };
    }

    internal JsonElement Settings(JsonElement snapshot) =>
        Key == "owner" ? snapshot.Property("localNodeInfo") :
            Key == "fixed-position" ? snapshot.Property("fixedPosition") :
            ChannelIndex is not null ? snapshot.Property("channel") :
            snapshot.Property(Module ? "moduleConfig" : "deviceConfig").Property(Field);

    internal static string? NodeId(JsonElement snapshot) => snapshot.Property("localNodeInfo").String("nodeId");

    internal bool CanSave(JsonElement snapshot)
    {
        var settings = Settings(snapshot);
        if (settings.ValueKind != JsonValueKind.Object || NodeId(snapshot) is null) return false;
        return Key != "owner" || settings.Flag("isUnmessagable") is not null && settings.Flag("isLicensed") is not null;
    }

    internal static object Read(JsonElement settings, RadioField field) => ReadValue(settings.Property(field.Key), field);

    internal static object ReadValue(JsonElement value, RadioField field)
    {
        if (field.Kind == "enum" && field.Options is { } options && value.ValueKind == JsonValueKind.String)
            return options.TryGetValue(value.GetString()!, out int option) ? option : value.GetString()!;
        if (field.Kind == "json" && value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            return JsonNode.Parse(value.GetRawText())!.ToJsonString();
        if (field.Kind is "float" or "double" && value.ValueKind == JsonValueKind.Number)
            return value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        if (field.Kind.Length > 0 && value.ValueKind == JsonValueKind.Number && field.Default is string)
            return value.GetRawText();
        return field.Default switch
        {
            string text => value.ValueKind == JsonValueKind.String ? value.GetString()! : text,
            int number => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) ? parsed : number,
            bool flag => value.ValueKind is JsonValueKind.False or JsonValueKind.True ? value.GetBoolean() : flag,
            _ => throw new InvalidOperationException("Unsupported radio setting.")
        };
    }

    internal JsonObject Prepare(JsonElement baseline, JsonElement current, Dictionary<string, object> changes)
    {
        string? node = NodeId(baseline);
        var latest = Settings(current);
        if (string.IsNullOrEmpty(node) || node != NodeId(current) || !CanSave(current))
            throw new InvalidOperationException("The connected radio or its configuration changed. Refresh before saving.");
        foreach (var field in Fields.Where(field => changes.ContainsKey(field.Key)))
            if (!Equals(Read(Settings(baseline), field), Read(latest, field)))
                throw new InvalidOperationException("A setting changed since it was loaded. Refresh before saving.");

        var body = Key == "owner"
            ? new JsonObject
            {
                ["longName"] = latest.String("longName"), ["shortName"] = latest.String("shortName"),
                ["isUnmessagable"] = latest.Flag("isUnmessagable") ?? false,
                ["isLicensed"] = latest.Flag("isLicensed") ?? false
            }
            : JsonNode.Parse(latest.GetRawText())!.AsObject();
        if (Key == "security")
        {
            body.Remove("privateKey");
            body.Remove("publicKey");
            body.Remove("packetSignaturePolicy");
            if (body["adminKey"] is { } adminKeys)
            {
                body["adminKeys"] = adminKeys.DeepClone();
                body.Remove("adminKey");
            }
        }
        foreach (var field in Fields.Where(field => Key != "deviceui" && field.Kind == "enum" && body.ContainsKey(field.Key)))
        {
            if (changes.ContainsKey(field.Key)) continue;
            if (Read(latest, field) is not int option)
                throw new InvalidOperationException($"The radio reported an unsupported value for {field.Label}.");
            body[field.Key] = option;
        }
        foreach (var (key, value) in changes)
            body[Key == "security" && key == "adminKey" ? "adminKeys" : key] = JsonSerializer.SerializeToNode(value);
        if (Key == "fixed-position" &&
            (body["latitude"] is null || body["longitude"] is null))
            throw new InvalidOperationException("Enter both latitude and longitude to set a fixed position.");
        if (Key == "security" && changes.ContainsKey("adminKey") && body["adminKeys"] is JsonArray keys &&
            (keys.Count > 3 || keys.Any(key => key is not JsonValue item || !item.TryGetValue<string>(out string? text) ||
                !Convert.TryFromBase64String(text, new byte[32], out int length) || length != 32)))
            throw new InvalidOperationException("Enter an array of up to three Base64 public keys of 32 bytes each.");
        foreach (var field in Fields.Where(field => field.Kind == "json" && field.Options is not null))
        {
            if (body[field.Key] is not JsonArray values) continue;
            for (int index = 0; index < values.Count; index++)
            {
                if (values[index] is not JsonValue item)
                    throw new InvalidOperationException($"Enter valid values for {field.Label}.");
                if (item.TryGetValue<int>(out _)) continue;
                if (!item.TryGetValue<string>(out var name) || !field.Options!.TryGetValue(name, out int option))
                    throw new InvalidOperationException($"Enter valid values for {field.Label}.");
                values[index] = option;
            }
        }
        return body;
    }
}

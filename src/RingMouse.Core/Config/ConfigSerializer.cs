using System.ComponentModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Core.Config;

/// <summary>JSON-Lesen/Schreiben der Config inkl. Kommentaren, Normalisierung und JSON-Schema-Export.</summary>
public static class ConfigSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            AllowOutOfOrderMetadataProperties = true,
            WriteIndented = true,
            IndentSize = 2,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Umlaute lesbar lassen
            RespectNullableAnnotations = false,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static RingMouseConfig Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<RingMouseConfig>(json, Options)
                     ?? throw new JsonException(L("The config is empty.", "Die Config ist leer."));
        return Normalize(config);
    }

    public static string Serialize(RingMouseConfig config) => JsonSerializer.Serialize(config, Options);

    public static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    public static RingMouseConfig Clone(RingMouseConfig config) => Normalize(Clone<RingMouseConfig>(config));

    /// <summary>Setzt fehlende Objekte und Groß-/Kleinschreibungs-unabhängige Dictionaries.</summary>
    public static RingMouseConfig Normalize(RingMouseConfig c)
    {
        c.General ??= new GeneralSettings();
        c.Ring ??= new RingSettings();
        c.Battery ??= new BatterySettings();
        c.Debug ??= new DebugSettings();
        c.Buttons = CaseInsensitive(c.Buttons);
        c.Rings = CaseInsensitive(c.Rings);
        c.Devices = CaseInsensitive(c.Devices);
        c.Profiles ??= [];
        foreach (var ring in c.Rings.Values) ring.Segments ??= [];
        foreach (var p in c.Profiles)
        {
            p.Processes ??= [];
            p.Buttons = CaseInsensitive(p.Buttons);
            p.Rings = CaseInsensitive(p.Rings);
        }
        c.Battery.Thresholds ??= [];
        return c;
    }

    private static Dictionary<string, T> CaseInsensitive<T>(Dictionary<string, T>? source) =>
        source is null
            ? new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, T>(source.Where(kv => kv.Value is not null), StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON-Schema für Editor-Unterstützung (VS Code: "$schema": "./config.schema.json").</summary>
    public static string ExportSchema()
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = false,
            TransformSchemaNode = static (context, schema) =>
            {
                var provider = context.PropertyInfo?.AttributeProvider ?? context.TypeInfo.Type;
                var description = provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
                    .OfType<DescriptionAttribute>().FirstOrDefault()?.Description;
                if (description is not null && schema is JsonObject obj && !obj.ContainsKey("description"))
                    obj.Insert(0, "description", description);

                if (context.TypeInfo.Type.IsEnum && schema is JsonObject enumObj)
                {
                    var names = Enum.GetNames(context.TypeInfo.Type)
                        .Select(n => JsonNamingPolicy.CamelCase.ConvertName(n)).ToArray();
                    enumObj["enum"] = new JsonArray(names.Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
                }
                return schema;
            },
        };
        var node = Options.GetJsonSchemaAsNode(typeof(RingMouseConfig), exporterOptions);
        if (node is JsonObject root)
        {
            root.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
            root.Insert(1, "title", "RingMouse configuration");
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

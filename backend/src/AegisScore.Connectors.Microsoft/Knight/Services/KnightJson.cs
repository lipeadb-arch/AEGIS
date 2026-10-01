using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AegisScore.Connectors.Microsoft.Knight.Services;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Leitura TOLERANTE de JSON das fontes: propriedade ausente ou de outro tipo devolve nulo,
/// nunca erro e nunca um valor inventado. A comparação de nomes ignora caixa (o Resource Manager e a API do SharePoint
/// não são uniformes nisso).
/// </summary>
public static class KnightJson
{
    public static JsonElement Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return default;
        if (e.TryGetProperty(name, out var v)) return v;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        return default;
    }

    /// <summary>Caminho de propriedades aninhadas (<c>"properties.siteConfig.minTlsVersion"</c>).</summary>
    public static JsonElement Path(JsonElement e, string path)
    {
        var current = e;
        foreach (var part in path.Split('.'))
        {
            current = Prop(current, part);
            if (current.ValueKind == JsonValueKind.Undefined) return default;
        }
        return current;
    }

    public static string? Str(JsonElement e, string name) => AsString(Prop(e, name));

    public static string? AsString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static bool? Bool(JsonElement e, string name) => AsBool(Prop(e, name));

    public static bool? AsBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "on" or "enabled" => true,
            "false" or "0" or "off" or "disabled" => false,
            _ => null,
        },
        JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : null,
        _ => null,
    };

    public static int? Int(JsonElement e, string name) => AsInt(Prop(e, name));

    public static int? AsInt(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out var n) => n,
        JsonValueKind.Number when v.TryGetInt64(out var l) => l > int.MaxValue ? int.MaxValue : (int)l,
        JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
        _ => null,
    };

    public static IReadOnlyList<string> Strings(JsonElement e, string name) => AsStrings(Prop(e, name));

    public static IReadOnlyList<string> AsStrings(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(AsString).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } single)
            return single.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return Array.Empty<string>();
    }

    public static IEnumerable<JsonElement> Items(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
}

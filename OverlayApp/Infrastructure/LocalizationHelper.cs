using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OverlayApp.Infrastructure;

public static class LocalizationHelper
{
    public static string CurrentLanguage { get; set; } = "en";

    public static string? ResolveName(Dictionary<string, string>? localized)
    {
        if (localized is null)
        {
            return null;
        }

        if (localized.TryGetValue(CurrentLanguage, out var val) && !string.IsNullOrWhiteSpace(val))
        {
            return val;
        }

        // Fallback to English
        if (localized.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en))
        {
            return en;
        }

        // Fallback to first available
        return localized.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    public static string? ResolveJsonName(Dictionary<string, JsonElement>? localized)
    {
        if (localized is null) return null;

        if (localized.TryGetValue(CurrentLanguage, out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }

        if (localized.TryGetValue("en", out var en) && en.ValueKind == JsonValueKind.String)
        {
            return en.GetString();
        }
        
        foreach (var kvp in localized)
        {
            if (kvp.Key.Equals("type", StringComparison.OrdinalIgnoreCase)) continue;
            if (kvp.Key.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            
            if (kvp.Value.ValueKind == JsonValueKind.String)
            {
                return kvp.Value.GetString();
            }
        }
        return null;
    }

    public static string? ResolveJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty(CurrentLanguage, out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }

        if (element.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
        {
            return en.GetString();
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals("type", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }
}

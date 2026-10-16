using System.Globalization;
using System.Text.Json;

namespace DevScope.Api.Integrations.Mcp;

internal static class JsonElementReader
{
    public static bool TryProperty(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static JsonElement? FirstProperty(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryProperty(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    public static string? StringValue(this JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.Value.ToString(),
            _ => null
        };
    }

    public static int? IntValue(this JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (element.Value.ValueKind == JsonValueKind.Number && element.Value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(element.StringValue(), CultureInfo.InvariantCulture, out number) ? number : null;
    }

    public static JsonElement? FindArray(this JsonElement element, params string[] preferredNames)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element;
        }

        foreach (var name in preferredNames)
        {
            if (element.TryProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }
}

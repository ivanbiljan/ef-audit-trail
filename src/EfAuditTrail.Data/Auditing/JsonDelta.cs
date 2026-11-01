using System.Globalization;
using System.Text.Json.Nodes;

namespace EfAuditTrail.Data.Auditing;

/// <summary>Structural diff of two JSON documents as a flat map of RFC 6901 pointer paths to the changed
/// leaf values. A leaf at the root yields the empty path, so a caller can prefix the property name.</summary>
public static class JsonDelta
{
    public static IReadOnlyDictionary<string, (JsonNode? Old, JsonNode? New)> Compute(string? oldJson, string? newJson)
    {
        var result = new Dictionary<string, (JsonNode? Old, JsonNode? New)>();
        Diff(string.Empty, Parse(oldJson), Parse(newJson), result);

        return result;
    }

    private static JsonNode? Parse(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);

    private static void Diff(string path, JsonNode? oldNode, JsonNode? newNode, Dictionary<string, (JsonNode? Old, JsonNode? New)> result)
    {
        if (oldNode is null && newNode is null)
        {
            return;
        }

        // A container against null is the same container with everything missing; against a scalar it is a type change.
        if (oldNode is JsonObject or null && newNode is JsonObject or null)
        {
            DiffObjects(path, oldNode as JsonObject, newNode as JsonObject, result);

            return;
        }

        if (oldNode is JsonArray or null && newNode is JsonArray or null)
        {
            DiffArrays(path, oldNode as JsonArray, newNode as JsonArray, result);

            return;
        }

        if (JsonNode.DeepEquals(oldNode, newNode))
        {
            return;
        }

        result[path] = (oldNode?.DeepClone(), newNode?.DeepClone());
    }

    private static void DiffObjects(string path, JsonObject? oldObject, JsonObject? newObject, Dictionary<string, (JsonNode? Old, JsonNode? New)> result)
    {
        var keys = new List<string>();
        if (oldObject is not null)
        {
            foreach (var property in oldObject)
            {
                keys.Add(property.Key);
            }
        }

        if (newObject is not null)
        {
            foreach (var property in newObject)
            {
                if (!keys.Contains(property.Key))
                {
                    keys.Add(property.Key);
                }
            }
        }

        foreach (var key in keys)
        {
            Diff($"{path}/{Escape(key)}", Child(oldObject, key), Child(newObject, key), result);
        }
    }

    private static void DiffArrays(string path, JsonArray? oldArray, JsonArray? newArray, Dictionary<string, (JsonNode? Old, JsonNode? New)> result)
    {
        var oldCount = oldArray?.Count ?? 0;
        var newCount = newArray?.Count ?? 0;

        for (var index = 0; index < Math.Max(oldCount, newCount); index++)
        {
            var oldItem = index < oldCount ? oldArray![index] : null;
            var newItem = index < newCount ? newArray![index] : null;

            Diff($"{path}/{index.ToString(CultureInfo.InvariantCulture)}", oldItem, newItem, result);
        }
    }

    private static JsonNode? Child(JsonObject? node, string key)
        => node is not null && node.TryGetPropertyValue(key, out var value) ? value : null;

    private static string Escape(string token)
        => token.Replace("~", "~0").Replace("/", "~1");
}

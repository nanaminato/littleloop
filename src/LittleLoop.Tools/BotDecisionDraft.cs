using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LittleLoop.Core;

namespace LittleLoop.Tools;

// Diagnostics are derived from the same serializer metadata that reads the decision.
internal static class BotDecisionDraft
{
    public static BotDecisionError DescribeShapeFailure(JsonElement root, JsonSerializerOptions json,
        JsonException failure)
    {
        var actual = FindValue(root, failure.Path);
        string field = failure.Path is { Length: > 1 } path ? Excerpt(path.TrimStart('$', '.'), 200) : "$";
        return CheckShape(root, typeof(BotDecision), "", json) ??
            new BotDecisionError("invalid-field-value", "field", "The value could not be read using the decision schema.", field)
            {
                Expected = "a value that conforms to the decision schema",
                Actual = actual
            };
    }

    public static string FindValue(JsonElement root, string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$") return Display(root);
        var current = root;
        foreach (var segment in path.TrimStart('$', '.').Replace("[", ".[", StringComparison.Ordinal).Split('.'))
        {
            if (segment.StartsWith('[') && segment.EndsWith(']') &&
                int.TryParse(segment.AsSpan(1, segment.Length - 2), out int index))
            {
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                    return "<missing>";
                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return "<missing>";
        }
        return Display(current);
    }

    public static string Display(JsonElement value) => Excerpt(value.GetRawText(), 400);

    public static string Excerpt(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[..maxCharacters] + " [excerpt]";

    private static BotDecisionError? CheckShape(JsonElement value, Type type, string path, JsonSerializerOptions json)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray && value.ValueKind == JsonValueKind.Array)
        {
            var itemType = type.GetElementType()!;
            int index = 0;
            foreach (var item in value.EnumerateArray())
            {
                string itemPath = $"{path}[{index++}]";
                if (item.ValueKind == JsonValueKind.Null)
                    return ShapeError("invalid-field-type", itemPath, Shape(itemType, json), "null");
                if (CheckShape(item, itemType, itemPath, json) is { } problem) return problem;
            }
            return null;
        }

        var metadata = json.GetTypeInfo(type);
        if (metadata.Kind == JsonTypeInfoKind.Object && value.ValueKind == JsonValueKind.Object)
            return CheckObject(value, path, metadata, json);

        var expectedKind = type switch
        {
            _ when type.IsEnum || type == typeof(Guid) || type == typeof(string) => JsonValueKind.String,
            _ when type == typeof(int) || type == typeof(long) => JsonValueKind.Number,
            _ when type == typeof(bool) => value.ValueKind == JsonValueKind.False ? JsonValueKind.False : JsonValueKind.True,
            _ when type.IsArray => JsonValueKind.Array,
            _ => JsonValueKind.Object
        };
        if (value.ValueKind != expectedKind)
            return ShapeError("invalid-field-type", path, Shape(type, json), Display(value));
        bool valid = type switch
        {
            _ when type.IsEnum => Enum.GetNames(type)
                .Any(name => string.Equals(name, value.GetString(), StringComparison.OrdinalIgnoreCase)),
            _ when type == typeof(Guid) => value.TryGetGuid(out _),
            _ when type == typeof(int) => value.TryGetInt32(out _),
            _ when type == typeof(long) => value.TryGetInt64(out _),
            _ => true
        };
        return valid ? null : ShapeError("invalid-field-value", path, Shape(type, json), Display(value));
    }

    private static BotDecisionError? CheckObject(JsonElement value, string path, JsonTypeInfo metadata,
        JsonSerializerOptions json)
    {
        string Field(string child) => path.Length == 0 ? child : path + "." + child;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
        {
            string field = Field(member.Name);
            var property = metadata.Properties.FirstOrDefault(p => p.Name == member.Name);
            if (property is null)
                return ShapeError("unknown-field", field, DescribeObject(metadata, json), Display(member.Value));
            if (!seen.Add(member.Name))
                return ShapeError("duplicate-field", field, "one " + Shape(property.PropertyType, json), Display(member.Value));
            if (member.Value.ValueKind == JsonValueKind.Null && property.IsSetNullable) continue;
            if (CheckShape(member.Value, property.PropertyType, field, json) is { } problem) return problem;
        }
        var missing = metadata.Properties.FirstOrDefault(p => p.IsRequired && !seen.Contains(p.Name));
        return missing is null ? null : ShapeError("missing-required-field", Field(missing.Name),
            Shape(missing.PropertyType, json), "<missing>");
    }

    private static string Shape(Type type, JsonSerializerOptions json)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray) return "array of " + Shape(type.GetElementType()!, json);
        if (type.IsEnum) return string.Join(" | ", Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName));
        if (type == typeof(Guid)) return "UUID string";
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long)) return "integer";
        return DescribeObject(json.GetTypeInfo(type), json);
    }

    private static string DescribeObject(JsonTypeInfo metadata, JsonSerializerOptions json) =>
        "object with " + string.Join(", ", metadata.Properties.Select(property =>
            property.Name + (property.IsRequired ? "" : "?") + ": " +
            (property.PropertyType.IsArray ? "array" : Shape(property.PropertyType, json))));

    private static BotDecisionError ShapeError(string code, string path, string expected, string actual) =>
        new(code, "field", code switch
        {
            "unknown-field" => "This member is not part of the decision schema.",
            "duplicate-field" => "Provide this member only once.",
            "missing-required-field" => "This member is required.",
            _ => "The value does not match the decision schema."
        }, Excerpt(path, 200))
        { Expected = expected, Actual = actual };
}

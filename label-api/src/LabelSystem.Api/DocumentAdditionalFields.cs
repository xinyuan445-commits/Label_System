using System.Text.Json;

namespace LabelSystem.Api;

internal static class DocumentAdditionalFields
{
    private const int MaxFields = 128;
    private const int MaxPayloadLength = 65_536;
    private const int MaxKeyLength = 100;
    private const int MaxStringLength = 4_000;

    public static void Validate(JsonElement? fields)
    {
        if (!fields.HasValue || fields.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;
        var value = fields.Value;
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > MaxPayloadLength)
            throw Invalid();

        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (count > MaxFields || string.IsNullOrWhiteSpace(property.Name) ||
                property.Name.Length > MaxKeyLength)
                throw Invalid();
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
                throw Invalid();
            if (property.Value.ValueKind == JsonValueKind.String &&
                (property.Value.GetString()?.Length ?? 0) > MaxStringLength)
                throw Invalid();
        }
    }

    public static void ValidateRequired(
        JsonElement? fields,
        IReadOnlyDictionary<string, bool> requiredFields,
        string prefix,
        IReadOnlySet<string> mappedFields)
    {
        foreach (var (key, required) in requiredFields)
        {
            if (!required || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                mappedFields.Contains(key)) continue;
            if (!HasValue(fields, key))
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "ADDITIONAL_FIELD_REQUIRED",
                    "存在未填写的必填字段。");
        }
    }

    private static bool HasValue(JsonElement? fields, string key)
    {
        if (!fields.HasValue || fields.Value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in fields.Value.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(property.Value.GetString()),
                _ => true,
            };
        }
        return false;
    }

    private static ApiException Invalid() => new(
        StatusCodes.Status400BadRequest,
        "ADDITIONAL_FIELDS_INVALID",
        "自定义字段内容无效。");
}

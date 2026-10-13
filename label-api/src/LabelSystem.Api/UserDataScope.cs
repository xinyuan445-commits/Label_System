using System.Text.Json;

namespace LabelSystem.Api;

public sealed record RdStyleAccessScope(bool IsRestricted, IReadOnlySet<string> Codes)
{
    public static RdStyleAccessScope All { get; } =
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool Allows(string code) =>
        !IsRestricted || Codes.Contains(code.Trim());
}

public sealed class UserDataScopeService(
    UserRepository users,
    ReferenceDataRepository referenceData)
{
    private static readonly IReadOnlyDictionary<string, string?> CategoryDirections =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["purchase-arrival"] = "IN",
            ["outsourcing-arrival"] = "IN",
            ["purchase-inbound"] = "IN",
            ["delivery-note"] = "OUT",
            ["sales-outbound"] = "OUT",
            ["material-outbound"] = "OUT",
            ["transfer-order"] = null,
            ["inventory-count"] = null,
        };

    public static string? CategoryDirection(string functionCode) =>
        CategoryDirections.TryGetValue(functionCode.Trim(), out var direction)
            ? direction
            : throw new ApiException(StatusCodes.Status400BadRequest,
                "DATA_SCOPE_NOT_SUPPORTED", "当前功能不支持业务类别范围。");

    public async Task<RdStyleAccessScope> GetRdStyleScopeAsync(
        string userId,
        string functionCode,
        CancellationToken cancellationToken)
    {
        var dataScope = await users.GetDataScopeAsync(userId, functionCode, cancellationToken);
        return ParseRdStyleScope(dataScope, strict: true);
    }

    public async Task ValidateForUpdateAsync(
        string functionCode,
        JsonElement? dataScope,
        CancellationToken cancellationToken)
    {
        if (!dataScope.HasValue || dataScope.Value.ValueKind == JsonValueKind.Null) return;
        var direction = CategoryDirection(functionCode);
        var scope = ParseRdStyleScope(dataScope, strict: true);
        if (!scope.IsRestricted) return;

        var active = await referenceData.ListRdStylesAsync(direction, true, cancellationToken);
        var available = active.Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (scope.Codes.Any(code => !available.Contains(code)))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "RD_STYLE_SCOPE_INVALID", "业务类别范围包含无效数据。");
    }

    public async Task<IReadOnlyList<RdStyleCacheItem>> FilterRdStylesAsync(
        string userId,
        string functionCode,
        IReadOnlyList<RdStyleCacheItem> items,
        CancellationToken cancellationToken)
    {
        var scope = await GetRdStyleScopeAsync(userId, functionCode, cancellationToken);
        return scope.IsRestricted
            ? items.Where(item => scope.Allows(item.Code)).ToArray()
            : items;
    }

    public async Task ValidateRdStyleAsync(
        string userId,
        string functionCode,
        string? categoryCode,
        bool required,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
        {
            if (required)
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "CATEGORY_REQUIRED", "请选择业务类别。");
            return;
        }

        var direction = CategoryDirection(functionCode);
        await ValidateRdStyleCoreAsync(
            userId, functionCode, categoryCode, direction, required, cancellationToken);
    }

    public Task ValidateRdStyleDirectionAsync(
        string userId,
        string functionCode,
        string? categoryCode,
        string direction,
        bool required,
        CancellationToken cancellationToken) =>
        ValidateRdStyleCoreAsync(
            userId, functionCode, categoryCode, direction, required, cancellationToken);

    private async Task ValidateRdStyleCoreAsync(
        string userId,
        string functionCode,
        string? categoryCode,
        string? direction,
        bool required,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
        {
            if (required)
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "CATEGORY_REQUIRED", "请选择业务类别。");
            return;
        }
        var styles = await referenceData.ListRdStylesAsync(direction, true, cancellationToken);
        var code = categoryCode.Trim();
        if (!styles.Any(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "RD_STYLE_INVALID", "业务类别无效。");

        var scope = await GetRdStyleScopeAsync(userId, functionCode, cancellationToken);
        if (!scope.Allows(code))
            throw new ApiException(StatusCodes.Status403Forbidden,
                "RD_STYLE_FORBIDDEN", "当前账号不可使用该业务类别。");
    }

    private static RdStyleAccessScope ParseRdStyleScope(JsonElement? dataScope, bool strict)
    {
        if (!dataScope.HasValue || dataScope.Value.ValueKind == JsonValueKind.Null)
            return RdStyleAccessScope.All;
        if (dataScope.Value.ValueKind != JsonValueKind.Object)
            return InvalidOrAll(strict);
        if (!dataScope.Value.TryGetProperty("rdStyles", out var rdStyles))
            return RdStyleAccessScope.All;
        if (rdStyles.ValueKind != JsonValueKind.Object ||
            !rdStyles.TryGetProperty("mode", out var modeElement) ||
            modeElement.ValueKind != JsonValueKind.String)
            return InvalidOrAll(strict);

        var mode = modeElement.GetString()?.Trim().ToUpperInvariant();
        if (mode == "ALL") return RdStyleAccessScope.All;
        if (mode != "SELECTED" ||
            !rdStyles.TryGetProperty("codes", out var codesElement) ||
            codesElement.ValueKind != JsonValueKind.Array)
            return InvalidOrAll(strict);

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in codesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return InvalidOrAll(strict);
            var code = item.GetString()!.Trim();
            if (code.Length > 50) return InvalidOrAll(strict);
            codes.Add(code);
        }
        if (codes.Count == 0) return InvalidOrAll(strict);
        return new RdStyleAccessScope(true, codes);
    }

    private static RdStyleAccessScope InvalidOrAll(bool strict)
    {
        if (strict)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DATA_SCOPE_INVALID", "业务范围格式无效。");
        return RdStyleAccessScope.All;
    }
}

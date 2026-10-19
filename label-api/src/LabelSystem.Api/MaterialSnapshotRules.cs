namespace LabelSystem.Api;

public static class MaterialSnapshotRules
{
    public static IReadOnlyList<MaterialSourceItem> Validate(
        MaterialSnapshotSource source, string accountId, string databaseName)
    {
        if (!string.Equals(source.AccountId, accountId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status409Conflict,
                "MATERIAL_ACCOUNT_MISMATCH", "物料来源账套不一致。");
        if (source.Items is null || source.Items.Count is < 1 or > 200000 ||
            source.ItemCount != source.Items.Count)
            throw Invalid();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<MaterialSourceItem>(source.Items.Count);
        foreach (var item in source.Items)
        {
            if (item is null) throw Invalid();
            var code = item.Code?.Trim() ?? "";
            var name = item.Name?.Trim() ?? "";
            var specification = item.Specification?.Trim() ?? "";
            var unit = item.UnitCode?.Trim() ?? "";
            if (code.Length is < 1 or > 100 || name.Length > 200 ||
                specification.Length > 500 || unit.Length > 50 || !codes.Add(code))
                throw Invalid();
            rows.Add(item with { Code = code, Name = name, Specification = specification, UnitCode = unit });
        }
        // 档案允许空名称/空单位，完整保存；制单实时校验仍拒绝缺少库存单位的料号。
        return rows;
    }

    private static ApiException Invalid() => new(StatusCodes.Status503ServiceUnavailable,
        "MATERIAL_SOURCE_INVALID", "U8 物料档案无效，请核对后重试。");
}

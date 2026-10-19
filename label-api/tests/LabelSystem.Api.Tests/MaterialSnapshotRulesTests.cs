using LabelSystem.Api;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class MaterialSnapshotRulesTests
{
    [Fact]
    public void CompleteSnapshot_NormalizesFieldsAndRetainsMissingUnitForLaterValidation()
    {
        var rows = MaterialSnapshotRules.Validate(Snapshot(
            new(" MAT-1 ", " 名称 ", " 规格 ", " EA ", true),
            new("MAT-2", "", "", "", false)), "008", "UFDATA_008_2021");
        Assert.Equal(new MaterialSourceItem("MAT-1", "名称", "规格", "EA", true), rows[0]);
        Assert.Equal("", rows[1].UnitCode);
    }

    [Theory]
    [InlineData("888", "UFDATA_008_2021")]
    [InlineData("008", "UFDATA_888_2021")]
    public void WrongAccountOrDatabase_RejectsSnapshot(string account, string database)
    {
        var error = Assert.Throws<ApiException>(() => MaterialSnapshotRules.Validate(
            Snapshot(new MaterialSourceItem("M1", "物料", "", "EA", false)), account, database));
        Assert.Equal("MATERIAL_ACCOUNT_MISMATCH", error.Code);
    }

    [Fact]
    public void EmptyTruncatedOrDuplicateSnapshot_IsRejected()
    {
        var row = new MaterialSourceItem("M1", "物料", "", "EA", true);
        foreach (var source in new[]
        {
            Snapshot(), Snapshot(row) with { ItemCount = 2 },
            Snapshot(row, row with { Code = " m1 " }),
            Snapshot(row with { Code = " " }),
            Snapshot(row with { Name = new string('名', 201) }),
            Snapshot(row) with { Items = null! }
        })
            Assert.Equal("MATERIAL_SOURCE_INVALID", Assert.Throws<ApiException>(() =>
                MaterialSnapshotRules.Validate(source, "008", "UFDATA_008_2021")).Code);
    }

    private static MaterialSnapshotSource Snapshot(params MaterialSourceItem[] rows) =>
        new("008", "UFDATA_008_2021", rows.Length, rows);
}

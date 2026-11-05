using System.Text.Json;
using LabelSystem.Api;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class InventoryCountUploadTests
{
    private static LabelSystemOptions Options() => new() { Environment = "development", ErpAccountId = "888",
        ErpDatabaseName = "UFDATA_888_2021", U8WritesEnabled = true, InventoryCountUploadsEnabled = true };
    private static InventoryCountSource Source() => new("888", "UFDATA_888_2021",
        new(12, "COUNT12", new DateTime(2026, 11, 5), "W1", "pending", null, null, "v1", 2,
            CountByLocation: true, WarehousePositionManaged: true),
        [new(41, 1, "M1", "物料", "B1", "W1", "L1", "0101", 100, 0, new string[10], false, "张"),
         new(42, 2, "M2", "未扫物料", "B2", "W1", "L2", "0101", 50, 8, new string[10], false, "张")]);
    private static DocumentPrepareRequest Request() => new("request1", "user", "User", null, null, null, "盘点",
        new(new DateOnly(2026, 11, 5), "BLUE", null, "W1", null, null, null, null, null, null,
            "inventory-count", "COUNT12", JsonSerializer.SerializeToElement(new Dictionary<string, string> {
                ["header.inCategoryCode"] = "107", ["header.outCategoryCode"] = "207" })),
        [new("PRODUCT_LABEL", "TAG1", null, "M1", "0101", 7, null, null, null, null, null, null, null, null)],
        new("888", "UFDATA_888_2021", true, Source()));
    private static MaterialOutboundExecutionLine Line(long id, string code, decimal before, decimal actual, string location = "L1") =>
        new(id, (int)id, "PRODUCT_LABEL", id, null, null, code, null, null, "M1", "B1", "张", before, actual,
            "W1", location, null, null, null, 100 + id);
    private static MaterialOutboundExecutionOperation Operation(params MaterialOutboundExecutionLine[] lines) =>
        new(1, 1, "request1", "request1", "user", "INVENTORY_COUNT", "inventory-count", "PENDING", "PENDING",
            null, null, "NOT_REQUIRED", "inventory-count", "", "COUNT12", "W1", null, null, "盘点", "archive", "hash", lines);
    private static ErpOperator Operator() => new("erp-user", "ERP User");

    [Fact]
    public void Only888WithBothWriteGatesCanUploadAndOldRequestsStillFail()
    {
        InventoryCountUploadRules.RequireContext(Request(), Options());
        Assert.Equal("COUNT_WRITE_NOT_READY", Assert.Throws<ApiException>(() => InventoryCountUploadRules.RequireContext(
            Request() with { InventoryCount = null }, Options())).Code);
        foreach (var options in new[] {
            new LabelSystemOptions { Environment = "production", ErpAccountId = "008", ErpDatabaseName = "UFDATA_008_2021", U8WritesEnabled = true, InventoryCountUploadsEnabled = true },
            new LabelSystemOptions { Environment = "development", ErpAccountId = "888", ErpDatabaseName = "wrong", U8WritesEnabled = true, InventoryCountUploadsEnabled = true } })
        {
            Assert.False(InventoryCountUploadRules.Enabled(options));
            Assert.Equal("COUNT_UPLOAD_TEST_ONLY", Assert.Throws<ApiException>(() => InventoryCountUploadRules.RequireTestAccount(options)).Code);
        }
        var disabled = Options(); disabled.InventoryCountUploadsEnabled = false;
        Assert.Equal("COUNT_UPLOAD_DISABLED", Assert.Throws<ApiException>(() => InventoryCountUploadRules.RequireTestAccount(disabled)).Code);
        disabled = Options(); disabled.U8WritesEnabled = false; Assert.False(InventoryCountUploadRules.Enabled(disabled));
    }

    [Fact]
    public async Task OldCountRequestsAreRejectedBeforeMirrorRefreshIncludingMixedCaseRoutes()
    {
        var service = new MaterialOutboundSubmissionService(null!, null!, null!, null!, null!, new TestOptions(Options()));
        var request = Request() with { InventoryCount = null };
        foreach (var kind in new[] { "inventory-count", "INVENTORY-COUNT", " Inventory-Count " })
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(kind,
                request, null!, "test", false, default));
            Assert.Equal("COUNT_WRITE_NOT_READY", error.Code);
        }
    }

    private sealed class TestOptions(LabelSystemOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<LabelSystemOptions>
    {
        public LabelSystemOptions CurrentValue => value;
        public LabelSystemOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LabelSystemOptions, string?> listener) => null;
    }

    [Fact]
    public void NativeRowUsesBookPlusScannedDifferencesAndDoesNotTouchUnscannedRow()
    {
        var result = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 7), Line(2, "TAG2", 20, 20)), Request(), Operator());
        Assert.Single(result.Lines);
        Assert.Equal(41L, result.Lines[0]["autoid"]);
        Assert.Equal(97m, result.Lines[0]["icvcquantity"]);
        Assert.False(result.Lines[0].ContainsKey("icvquantity"));
        Assert.Equal(12, InventoryCountUploadRules.SourceId(result));
        Assert.Equal("v1", result.ExpectedVersion);
        Assert.Equal("107", result.Header["cirdcode"]); Assert.Equal("207", result.Header["cordcode"]);
        Assert.True((bool)result.Options["countPartialSave"]!);
    }

    [Fact]
    public void ZeroActualAndGainAreDifferencesRatherThanRejectedOrClamped()
    {
        var zero = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 0)), Request(), Operator());
        Assert.Equal(90m, zero.Lines.Single()["icvcquantity"]);
        var gain = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 12)), Request(), Operator());
        Assert.Equal(102m, gain.Lines.Single()["icvcquantity"]);
        var source = Source() with { Lines = [Source().Lines[0] with { BookQuantity = 1 }] };
        Assert.Equal("COUNT_NEGATIVE_RESULT", Assert.Throws<ApiException>(() => InventoryCountUploadRules.Build(
            Operation(Line(1, "TAG1", 10, 0)), Request() with { InventoryCount = new("888", "UFDATA_888_2021", true, source) }, Operator())).Code);
        var alreadyCounted = Source() with { Lines = [Source().Lines[0] with { ActualQuantity = 90 }] };
        Assert.Equal("COUNT_ROW_ALREADY_COUNTED", Assert.Throws<ApiException>(() => InventoryCountUploadRules.Build(
            Operation(Line(1, "TAG1", 10, 9)), Request() with { InventoryCount = new("888", "UFDATA_888_2021", true, alreadyCounted) }, Operator())).Code);
    }

    [Fact]
    public void DirectCreateGroupsSameScopeAndPassesDifferencesForNativeStockBaseline()
    {
        var result = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 9), Line(2, "TAG2", 20, 22)),
            Request() with { InventoryCount = new("888", "UFDATA_888_2021", false) }, Operator());
        Assert.Null(InventoryCountUploadRules.SourceId(result)); Assert.Single(result.Lines);
        Assert.Equal("M1", result.Lines[0]["cinvcode"]); Assert.Equal("", result.Lines[0]["cposition"]);
        Assert.Equal(new decimal[] { 1m }, result.Options["countDeltas"]);
    }

    [Fact]
    public void MultipleTagsCanMapToSameConfirmedErpRowAndMissingRowsFail()
    {
        var op = Operation(Line(1, "TAG1", 10, 7), Line(2, "TAG2", 20, 20));
        var request = InventoryCountUploadRules.Build(op, Request(), Operator());
        var document = JsonSerializer.SerializeToElement(new { lines = new[] { new { autoid = 41, cinvcode = "M1", ccvbatch = "B1", cposition = "L1" } } });
        var result = new GatewayDocumentResult(true, true, "OK", "", "12", "COUNT12", "v2", document);
        var rows = InventoryCountUploadRules.ReadRows(op, result, request);
        Assert.Equal("41", rows[1].LineId); Assert.Equal("41", rows[2].LineId);
        Assert.Throws<InvalidOperationException>(() => InventoryCountUploadRules.ReadRows(op,
            result with { Document = JsonSerializer.SerializeToElement(new { lines = Array.Empty<object>() }) }, request));
    }
}

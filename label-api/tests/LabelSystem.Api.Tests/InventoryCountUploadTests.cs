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
    public void NativeRowUsesUploadedActualsAndDoesNotTouchBookOrUnscannedRow()
    {
        var result = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 7), Line(2, "TAG2", 20, 20)), Request(), Operator());
        Assert.Single(result.Lines);
        Assert.Equal(41L, result.Lines[0]["autoid"]);
        Assert.Equal(27m, result.Lines[0]["icvcquantity"]);
        Assert.False(result.Lines[0].ContainsKey("icvquantity"));
        Assert.Equal(12, InventoryCountUploadRules.SourceId(result));
        Assert.Equal("v1", result.ExpectedVersion);
        Assert.Equal("107", result.Header["cirdcode"]); Assert.Equal("207", result.Header["cordcode"]);
        Assert.True((bool)result.Options["countPartialSave"]!);
    }

    [Fact]
    public void ZeroActualAndGainAreSavedAsActualsIncludingPreviouslyCountedRows()
    {
        var zero = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 0)), Request(), Operator());
        Assert.Equal(0m, zero.Lines.Single()["icvcquantity"]);
        var gain = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 12)), Request(), Operator());
        Assert.Equal(12m, gain.Lines.Single()["icvcquantity"]);
        var alreadyCounted = Source() with { Lines = [Source().Lines[0] with { ActualQuantity = 90 }] };
        Assert.Equal(9m, InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 9)),
            Request() with { InventoryCount = new("888", "UFDATA_888_2021", true, alreadyCounted) }, Operator()).Lines.Single()["icvcquantity"]);
    }

    [Fact]
    public void DirectCreateGroupsActualsSeparatelyFromNativeStockBaseline()
    {
        var result = InventoryCountUploadRules.Build(Operation(Line(1, "TAG1", 10, 9), Line(2, "TAG2", 20, 22)),
            Request() with { InventoryCount = new("888", "UFDATA_888_2021", false) }, Operator());
        Assert.Null(InventoryCountUploadRules.SourceId(result)); Assert.Single(result.Lines);
        Assert.Equal("M1", result.Lines[0]["cinvcode"]); Assert.Equal("", result.Lines[0]["cposition"]);
        Assert.Equal(31m, result.Lines.Single()["icvcquantity"]);
        Assert.False(result.Options.ContainsKey("countDeltas"));
    }

    [Fact]
    public void MigratedAndNewCommonEventBaselinesKeepIdenticalCumulativePayloadsOnRetry()
    {
        const string migrated = """
            {"documentType":"inventory-count","sourceDocumentId":12,"previousLabels":[
              {"Code":"OLD","MaterialCode":"M1","BatchNo":"B1","WarehouseCode":"W1","LocationCode":"L1","ActualQuantity":14.000000}]}
            """;
        var previous = InventoryCountUploadRepository.ReadBaseline(migrated, 12);
        var savedAgain = JsonSerializer.Serialize(new DocumentUploadBaseline("inventory-count", 12, previous),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var op = Operation(Line(1, "NEW", 49, 50));
        var first = InventoryCountUploadRules.Build(op, Request(), Operator(), previous);
        var retry = InventoryCountUploadRules.Build(op, Request(), Operator(), InventoryCountUploadRepository.ReadBaseline(savedAgain, 12));
        Assert.Equal(64m, first.Lines.Single()["icvcquantity"]);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(retry));
        Assert.Empty(InventoryCountUploadRepository.ReadBaseline(
            """{"documentType":"inventory-count","sourceDocumentId":12,"previousLabels":[]}""", 12));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid json")]
    [InlineData("{\"documentType\":\"transfer\",\"sourceDocumentId\":12,\"previousLabels\":[]}")]
    [InlineData("{\"documentType\":\"inventory-count\",\"sourceDocumentId\":13,\"previousLabels\":[]}")]
    [InlineData("{\"documentType\":\"inventory-count\",\"sourceDocumentId\":12}")]
    public void InvalidOrWrongSourceFrozenEventsAreNeverRecomputedAsAnEmptyBaseline(string json)
    {
        Assert.Equal("UPLOAD_BASELINE_INVALID", Assert.Throws<ApiException>(() =>
            InventoryCountUploadRepository.ReadBaseline(json, 12)).Code);
    }

    [Fact]
    public void LaterUploadAccumulatesUniqueCodesAndCorrectionReplacesPreviousContribution()
    {
        InventoryCountContribution[] previous = [
            new("OLD", "M1", "B1", "W1", "L1", 14), new("TAG1", "M1", "B1", "W1", "L1", 49),
            new("OTHER_LOCATION", "M1", "B1", "W1", "L2", 80), new("OTHER_MATERIAL", "M2", "B1", "W1", "L1", 70),
            new("OTHER_WAREHOUSE", "M1", "B1", "W2", "L1", 60) ];
        var op = Operation(Line(1, "TAG1", 49, 50));
        var result = InventoryCountUploadRules.Build(op, Request(), Operator(), previous);
        Assert.Equal(64m, result.Lines.Single()["icvcquantity"]);
        Assert.Single((InventoryCountContribution[])result.Options["countPreviousLabels"]!);
        var repeated = InventoryCountUploadRules.Build(op, Request(), Operator(), previous);
        Assert.Equal(result.Lines.Single()["icvcquantity"], repeated.Lines.Single()["icvcquantity"]);
        var warehouseCount = Request() with { InventoryCount = new("888", "UFDATA_888_2021", false) };
        Assert.Equal(144m, InventoryCountUploadRules.Build(op, warehouseCount, Operator(), previous).Lines.Single()["icvcquantity"]);
    }

    [Fact]
    public void UserFourLabelsProduceFiveFiftyAndTwentyRegardlessOfBookAndPrintedQuantities()
    {
        var source = Source() with { Lines = [
            Source().Lines[0] with { BookQuantity = 8 },
            Source().Lines[0] with { Id = 42, MaterialCode = "M2", BookQuantity = 62 },
            Source().Lines[0] with { Id = 43, MaterialCode = "M3", BookQuantity = 77 } ] };
        var op = Operation(Line(1, "008|M1|B1|50.00|001", 5, 5),
            Line(2, "008|M2|B1|50.00|002", 49, 50) with { MaterialCode = "M2" },
            Line(3, "008|M3|B1|32.00|003", 14, 14) with { MaterialCode = "M3" },
            Line(4, "008|M3|B1|19|004", 6, 6) with { MaterialCode = "M3" });
        var request = Request() with { InventoryCount = new("888", "UFDATA_888_2021", true, source) };
        var result = InventoryCountUploadRules.Build(op, request, Operator());
        Assert.Equal(new decimal[] { 5, 50, 20 }, result.Lines.Select(l => (decimal)l["icvcquantity"]!));
    }

    [Fact]
    public void CrossWarehouseCountGroupsAtChosenPositionAndKeepsOriginalSnapshotForRecheck()
    {
        var op = Operation(
            Line(1, "TAG1", 49, 50, "OLD") with { WarehouseCode = "W2", TargetWarehouseCode = "W1", TargetLocationCode = "L1" },
            Line(2, "TAG2", 10, 7, "OTHER") with { WarehouseCode = "W3", TargetWarehouseCode = "W1", TargetLocationCode = "L1" })
            with { WarehouseCode = "W2" };
        var request = InventoryCountUploadRules.Build(op, Request(), Operator(), [new("PRIOR", "M1", "B1", "W1", "L1", 14)]);
        Assert.Single(request.Lines); Assert.Equal(71m, request.Lines[0]["icvcquantity"]);
        var labels = JsonSerializer.SerializeToElement(request.Options["countLabels"]);
        Assert.Equal("W2", labels[0].GetProperty("WarehouseCode").GetString());
        Assert.Equal("OLD", labels[0].GetProperty("LocationCode").GetString());
        Assert.Equal("W1", labels[0].GetProperty("TargetWarehouseCode").GetString());
        Assert.Equal("L1", labels[0].GetProperty("TargetLocationCode").GetString());
        var result = new GatewayDocumentResult(true, true, "OK", "", "12", "COUNT12", "v2",
            JsonSerializer.SerializeToElement(new { lines = new[] { new { autoid = 41, cinvcode = "M1", ccvbatch = "B1", cposition = "L1" } } }));
        var rows = InventoryCountUploadRules.ReadRows(op, result, request);
        var tasks = TraceBackSyncPlanner.Build(op, result, false, rows, "User", "888", request);
        var payload = JsonSerializer.Deserialize<TraceBackSyncEventRequest>(tasks[0].PayloadJson,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("W1", payload.TargetWarehouseCode); Assert.Equal("L1", payload.TargetLocationCode);
        Assert.Equal("W1", payload.CountContext!.WarehouseCode); Assert.Equal(1m, payload.Quantity);
    }

    [Fact]
    public void CountWritesStableSignedTraceBackDifferencesIncludingZero()
    {
        var op = Operation(Line(1, "GAIN", 49, 50), Line(2, "LOSS", 8, 5), Line(3, "SAME", 14, 14), Line(4, "ZERO", 6, 0));
        var result = new GatewayDocumentResult(true, true, "OK", "", "12", "COUNT12", "v2", null);
        var rows = op.Lines.ToDictionary(l => l.LineNo, _ => ((string?)"41", (string?)null));
        var countRequest = InventoryCountUploadRules.Build(op, Request(), Operator());
        var tasks = TraceBackSyncPlanner.Build(op, result, false, rows, "User", "888", countRequest);
        Assert.Equal(4, tasks.Count);
        Assert.All(tasks, t => Assert.Equal("COUNT_ADJUSTMENT", t.EventRole));
        var payloads = tasks.Select(t => JsonSerializer.Deserialize<TraceBackSyncEventRequest>(t.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.Equal(new decimal[] { 1, -3, 0, -6 }, payloads.Select(p => p.Quantity));
        Assert.Equal(new decimal[] { 50, 5, 14, 0 }, payloads.Select(p => p.ExpectedBalance + p.Quantity));
        Assert.All(payloads, p => { Assert.Equal("W1", p.TargetWarehouseCode); Assert.Equal("L1", p.TargetLocationCode); });
        Assert.All(payloads, p => {
            Assert.Equal("IC-1", p.PdaDocumentNo);
            Assert.Equal("2026-11-05", p.CountContext!.DocumentDate);
            Assert.Equal("W1", p.CountContext.WarehouseCode);
            Assert.True(p.CountContext.CountByLocation);
            Assert.Equal(12, p.CountContext.SourceDocumentId);
            Assert.Equal("COUNT12", p.CountContext.SourceDocumentNo);
        });
        Assert.Equal(tasks.Select(t => t.EventKey), TraceBackSyncPlanner.Build(op, result, false, rows, "User", "888", countRequest).Select(t => t.EventKey));
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

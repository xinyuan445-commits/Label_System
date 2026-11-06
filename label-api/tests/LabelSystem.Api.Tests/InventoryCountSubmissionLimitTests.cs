using System.Reflection;
using System.Text.Json;
using LabelSystem.Api;
using Microsoft.Extensions.Options;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class InventoryCountSubmissionLimitTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly IReadOnlyDictionary<string, DocumentPrepareDefinition> Definitions =
        (IReadOnlyDictionary<string, DocumentPrepareDefinition>)typeof(DocumentPrepareService)
            .GetField("Definitions", PrivateStatic)!.GetValue(null)!;
    private static readonly MethodInfo Validate = typeof(DocumentPrepareService).GetMethod("ValidateBasicRequest", PrivateStatic)!;
    private static LabelSystemOptions Options() => new() { Environment = "development", ErpAccountId = "888",
        ErpDatabaseName = "UFDATA_888_2021", U8WritesEnabled = true, InventoryCountUploadsEnabled = true };
    private static DocumentPrepareRequest Request(int size) => new("limit-test", "user", "User", null, null, null, "test",
        new(new(2026, 11, 6), "BLUE", "PARTNER", "W1", "W2", "L1", "L2", "D1", "202", null, null, null, null),
        Enumerable.Range(1, size).Select(i => new DocumentPrepareLine("PRODUCT_LABEL", $"TAG{i:D4}", null,
            "M1", "U1", i % 3, null, null, null, null, null, null, null, null,
            BatchNo: "B1", WarehouseCode: "W1", LocationCode: "L1", TargetWarehouseCode: "W2", TargetLocationCode: "L2")).ToArray(),
        new("888", "UFDATA_888_2021", false));
    private static void ValidateBasic(string type, DocumentPrepareRequest request) =>
        Validate.Invoke(null, [request, Definitions[type], new Dictionary<string, bool>(), false]);

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(1000)]
    public void CountReadValidationAndUploadAcceptTheSameLabelsWithoutDroppingSameMaterialRows(int size)
    {
        var request = Request(size);
        InventoryCountRules.RequireObservationList(InventoryCountUploadRules.Observations(request));
        InventoryCountUploadRules.RequireContext(request, Options());
        ValidateBasic("inventory-count", request); // Used to reject the 501st row after label validation had passed.
        var lines = request.Lines.Select((l, i) => new MaterialOutboundExecutionLine(i + 1, i + 1, "PRODUCT_LABEL",
            i + 1, null, null, l.LabelCode, null, null, "M1", "B1", "U1", 2, l.RequestedQty,
            "W1", "L1", null, null, null, i + 100)).ToArray();
        var operation = new MaterialOutboundExecutionOperation(1, 1, "limit-test", "limit-test", "user", "INVENTORY_COUNT",
            "inventory-count", "PENDING", "PENDING", null, null, "NOT_REQUIRED", "", "", "", "W1", null, null,
            "test", "archive", "hash", lines);
        var gateway = InventoryCountUploadRules.Build(operation, request, new("erp-user", "ERP User"));
        Assert.Single(gateway.Lines); // One U8 material/batch row, while retaining all unique labels for their own events.
        Assert.Equal(request.Lines.Sum(l => l.RequestedQty), gateway.Lines[0]["icvcquantity"]);
        var labels = JsonSerializer.SerializeToElement(gateway.Options["countLabels"]!);
        Assert.Equal(size, labels.GetArrayLength());
        Assert.Equal(size, labels.EnumerateArray().Select(l => l.GetProperty("Code").GetString()).Distinct().Count());
        var result = new GatewayDocumentResult(true, true, "OK", "", "12", "COUNT12", "v1",
            JsonSerializer.SerializeToElement(new { lines = new[] { new { autoid = 41, cinvcode = "M1", ccvbatch = "B1", cposition = "" } } }));
        var erpRows = InventoryCountUploadRules.ReadRows(operation, result, gateway);
        Assert.Equal(size, erpRows.Count);
        var tasks = TraceBackSyncPlanner.Build(operation, result, false, erpRows, "ERP User", "888", gateway);
        var retry = TraceBackSyncPlanner.Build(operation, result, false, erpRows, "ERP User", "888", gateway);
        Assert.Equal(size, tasks.Count);
        Assert.Equal(size, tasks.Select(t => t.EventKey).Distinct().Count());
        Assert.Equal(size, tasks.Select(t => t.PdaGoodsUuid).Distinct().Count());
        Assert.Equal(tasks.Select(t => t.EventKey), retry.Select(t => t.EventKey));
        Assert.Equal(tasks.Select(t => t.PdaGoodsUuid), retry.Select(t => t.PdaGoodsUuid));
        var payloads = tasks.Select(t => JsonSerializer.Deserialize<TraceBackSyncEventRequest>(t.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.Equal(request.Lines.Sum(l => l.RequestedQty) - 2m * size, payloads.Sum(t => t.Quantity));
    }

    [Fact]
    public async Task The1001stCountLabelIsRejectedBeforeMirrorDatabaseOrU8Access()
    {
        var request = Request(1001);
        Assert.Equal("COUNT_OBSERVATIONS_INVALID", Assert.Throws<ApiException>(() =>
            InventoryCountRules.RequireObservationList(InventoryCountUploadRules.Observations(request))).Code);
        var submission = new MaterialOutboundSubmissionService(null!, null!, null!, null!, null!, new TestOptions(Options()));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            submission.SubmitAsync("inventory-count", request, null!, "test", false, default));
        Assert.Equal("COUNT_OBSERVATIONS_INVALID", error.Code);
        var basic = Assert.Throws<TargetInvocationException>(() => ValidateBasic("inventory-count", request));
        Assert.Equal("LINES_INVALID", Assert.IsType<ApiException>(basic.InnerException).Code);
    }

    [Fact]
    public void DuplicateCodesStillFailAt1000EvenIfSpellingOrWhitespaceDiffers()
    {
        var request = Request(1000);
        request = request with { Lines = request.Lines.SkipLast(1).Append(request.Lines[0] with { LabelCode = " tag0001 " }).ToArray() };
        Assert.Equal("COUNT_OBSERVATIONS_INVALID", Assert.Throws<ApiException>(() =>
            InventoryCountUploadRules.RequireContext(request, Options())).Code);
        var basic = Assert.Throws<TargetInvocationException>(() => ValidateBasic("inventory-count", request));
        Assert.Equal("OBJECT_DUPLICATED", Assert.IsType<ApiException>(basic.InnerException).Code);
    }

    [Fact]
    public void OtherDocumentTypesKeepTheir500RowLimit()
    {
        var request = Request(500) with { InventoryCount = null };
        request = request with { Lines = request.Lines.Select(l => l with { RequestedQty = 1 }).ToArray() };
        foreach (var type in Definitions.Keys.Where(t => t != "inventory-count"))
        {
            ValidateBasic(type, request);
            var extra = request with { Lines = request.Lines.Append(request.Lines[0] with { LabelCode = "TAG0501" }).ToArray() };
            var error = Assert.Throws<TargetInvocationException>(() => ValidateBasic(type, extra));
            Assert.Equal("LINES_INVALID", Assert.IsType<ApiException>(error.InnerException).Code);
        }
    }

    private sealed class TestOptions(LabelSystemOptions value) : IOptionsMonitor<LabelSystemOptions>
    {
        public LabelSystemOptions CurrentValue => value;
        public LabelSystemOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LabelSystemOptions, string?> listener) => null;
    }
}

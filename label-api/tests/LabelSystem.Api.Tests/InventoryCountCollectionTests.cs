using System.Net;
using System.Text;
using System.Text.Json;
using LabelSystem.Api;
using Microsoft.Extensions.Options;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class InventoryCountCollectionTests
{
    private static InventoryCountSource Source() => new("888", "UFDATA_888_2021",
        new(12, "COUNT12", new DateTime(2026, 11, 4), "W1", "pending", null, null, "0x01", 1),
        [new(41, 1, "M1", "物料", "B1", "W1", "L1", "U1", 10m, null, new string[10], false)]);
    private static TraceBackLabelSnapshot Label(string code = "888|M1|B1|999.00|001") => new(
        code, "001", 10m, 25, "goods", "order", "M1", "物料", "B1", "U1", "个", "W1", "仓库",
        "L1", "货位", "采购入库", "P1", "ERP1", "identity", "AVAILABLE", []);
    private static InventoryCountObservation Observation(string? quantity = "9") => new(Label().Code, quantity);

    [Theory]
    [InlineData("0", true)] [InlineData("0.000001", true)] [InlineData("999999999999.999999", true)]
    [InlineData("", false)] [InlineData("-1", false)] [InlineData("NaN", false)] [InlineData("Infinity", false)]
    [InlineData("1e3", false)] [InlineData("0.0000001", false)] [InlineData("01", false)]
    [InlineData("1000000000000", false)]
    public void QuantityIsExplicitNonnegativeDecimal(string value, bool expected) =>
        Assert.Equal(expected, InventoryCountRules.TryQuantity(value, out _));

    [Fact]
    public void PrintedQuantityIsNotUsedAsBalanceOrActual()
    {
        var result = InventoryCountRules.Match(Source(), Observation(), Label());
        Assert.Equal("MATCHED", result.Status);
        Assert.Equal(10m, result.Balance);
        Assert.Equal(41, result.SourceLineId);
        Assert.Equal("QUANTITY_REQUIRED", InventoryCountRules.Match(Source(), Observation(null), Label()).Status);
    }

    [Fact]
    public void ZeroQuantityAndZeroBalanceAreNotUncounted()
    {
        var result = InventoryCountRules.Match(Source(), Observation("0"), Label() with { Status = "USED_UP", Balance = 0m });
        Assert.Equal("MATCHED", result.Status);
        Assert.Equal(0m, result.Balance);
    }

    [Fact]
    public void IdentityPositionUnitsAndAnomaliesDoNotGetGuessed()
    {
        Assert.Equal("OUTSIDE_SOURCE", InventoryCountRules.Match(Source(), Observation(), Label() with { LocationCode = "L2" }).Status);
        Assert.Equal("OUTSIDE_SOURCE", InventoryCountRules.Match(Source(), Observation(), Label() with { BatchNo = "B2" }).Status);
        Assert.Equal("UNIT_MISMATCH", InventoryCountRules.Match(Source(), Observation(), Label() with { UnitCode = "U2" }).Status);
        Assert.Equal("LABEL_INVALID", InventoryCountRules.Match(Source(), Observation(), Label() with { Balance = -1m }).Status);
        Assert.Equal("LABEL_INVALID", InventoryCountRules.Match(Source(), Observation(), Label() with { Status = "RECONCILIATION_REQUIRED" }).Status);
        Assert.Equal("LABEL_INVALID", InventoryCountRules.Match(Source(), Observation(), Label() with { Code = "other" }).Status);
        Assert.Equal("LABEL_INVALID", InventoryCountRules.Match(Source(), Observation(), Label() with {
            Anomalies = [new("TRACEBACK_GOODS_DUPLICATED", "duplicate")] }).Status);
        var duplicate = Source() with { Lines = [Source().Lines[0], Source().Lines[0] with { Id = 42 }] };
        Assert.Equal("AMBIGUOUS_SOURCE", InventoryCountRules.Match(duplicate, Observation(), Label()).Status);
        var free = Source() with { Lines = [Source().Lines[0] with { FreeItems = ["A"] }] };
        Assert.Equal("IDENTITY_NOT_SUPPORTED", InventoryCountRules.Match(free, Observation(), Label()).Status);
    }

    [Fact]
    public void SourceCompletenessAccountAndNativeStatusAreChecked()
    {
        InventoryCountRules.RequireSource(Source(), "888", "UFDATA_888_2021");
        Assert.Equal("COUNT_ACCOUNT_MISMATCH", Assert.Throws<ApiException>(() =>
            InventoryCountRules.RequireSource(Source(), "008", "UFDATA_008_2021")).Code);
        Assert.Equal("COUNT_SOURCE_UNAVAILABLE", Assert.Throws<ApiException>(() =>
            InventoryCountRules.RequireSource(Source() with { Document = Source().Document with { AuditedBy = "auditor" } }, "888", "UFDATA_888_2021")).Code);
        Assert.Equal("COUNT_SOURCE_INCOMPLETE", Assert.Throws<ApiException>(() =>
            InventoryCountRules.RequireSource(Source() with { Lines = [] }, "888", "UFDATA_888_2021")).Code);
    }

    [Fact]
    public void FingerprintIncludesNativeLineChangesEvenWithoutHeaderVersionChange()
    {
        var source = Source();
        Assert.NotEqual(InventoryCountRules.Fingerprint(source), InventoryCountRules.Fingerprint(source with {
            Lines = [source.Lines[0] with { ActualQuantity = 4m }] }));
        Assert.Equal(InventoryCountRules.Fingerprint(source), InventoryCountRules.Fingerprint(source with { Fingerprint = "ignored" }));
    }

    [Fact]
    public void SourceCategoriesSurviveGatewayJsonAndChangeFingerprint()
    {
        var source = Source() with { Document = Source().Document with {
            InCategoryCode = "107", InCategoryName = "盘盈入库",
            OutCategoryCode = "207", OutCategoryName = "盘亏出库" } };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<InventoryCountSource>(JsonSerializer.Serialize(source, options), options)!;
        Assert.Equal("107", restored.Document.InCategoryCode);
        Assert.Equal("盘盈入库", restored.Document.InCategoryName);
        Assert.Equal("207", restored.Document.OutCategoryCode);
        Assert.Equal("盘亏出库", restored.Document.OutCategoryName);
        Assert.NotEqual(InventoryCountRules.Fingerprint(source), InventoryCountRules.Fingerprint(source with {
            Document = source.Document with { InCategoryCode = "108" } }));
    }

    [Fact]
    public void DuplicateCodesIncludingCaseAndWhitespaceAreRejected()
    {
        var error = Assert.Throws<ApiException>(() => InventoryCountRules.RequireObservationList([
            new(" tag1 ", "1"), new("TAG1", "1")]));
        Assert.Equal("COUNT_OBSERVATIONS_INVALID", error.Code);
    }

    [Fact]
    public async Task LegacyCreateIsRejectedBeforeAnyDatabaseOrGatewayAccess()
    {
        var service = new DocumentPrepareService(null!, null!, null!, null!, null!, null!);
        var error = await Assert.ThrowsAsync<ApiException>(() => service.PrepareAsync(
            "inventory-count", null!, "req", CancellationToken.None));
        Assert.Equal("COUNT_WRITE_NOT_READY", error.Code);
        InventoryCountRules.RejectLegacyWrite("material-outbound");
        InventoryCountRules.RejectLegacyWrite("transfer");
    }

    [Fact]
    public async Task ValidationBatchesReadOnlySnapshotsAndNeverOpensWriteGate()
    {
        var fake = new ReadOnlyHandler();
        var service = Service(fake);
        var downloaded = await service.GetSourceAsync(12, default);
        var observations = Enumerable.Range(0, 101).Select(n => new InventoryCountObservation("TAG" + n, "1")).ToArray();
        var result = await service.ValidateAsync(12, new("888", "UFDATA_888_2021", downloaded.Fingerprint, observations), default);
        Assert.Equal(2, fake.SnapshotCalls);
        Assert.Equal(101, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal("MATCHED", item.Status));
        Assert.False(result.CanSubmit);
    }

    [Fact]
    public async Task SourceChangedDuringReadOnlyValidationPreservesCollectionAndRejectsResult()
    {
        var fake = new ReadOnlyHandler { ChangeAfterSnapshots = true };
        var service = Service(fake);
        var downloaded = await service.GetSourceAsync(12, default);
        var failure = await Assert.ThrowsAsync<ApiException>(() => service.ValidateAsync(12,
            new("888", "UFDATA_888_2021", downloaded.Fingerprint, [Observation()]), default));
        Assert.Equal("COUNT_SOURCE_CHANGED", failure.Code);
    }

    private static InventoryCountCollectionService Service(ReadOnlyHandler handler) => new(
        new U8GatewayClient(new Factory(handler), new Monitor<U8GatewayOptions>(new() { BaseUrl = "http://gateway.test", ApiKey = "test-only" })),
        new Monitor<LabelSystemOptions>(new() { ErpAccountId = "888", ErpDatabaseName = "UFDATA_888_2021", U8WritesEnabled = false }));

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private sealed class ReadOnlyHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public int SnapshotCalls { get; private set; }
        public bool ChangeAfterSnapshots { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object data;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/inventory-counts/12")
                data = ChangeAfterSnapshots && SnapshotCalls > 0 ? Source() with { Document = Source().Document with { Version = "0x02" } } : Source();
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/v1/traceback/labels/snapshots", request.RequestUri!.AbsolutePath);
                var input = JsonSerializer.Deserialize<TraceBackSnapshotRequest>(await request.Content!.ReadAsStringAsync(ct), Json)!;
                Assert.InRange(input.Codes.Count, 1, 100);
                SnapshotCalls++;
                data = input.Codes.Select(code => Label(code)).ToArray();
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { success = true, code = "OK", data }, Json), Encoding.UTF8, "application/json") };
        }
    }
}

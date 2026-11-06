using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LabelSystem.Api;
using Microsoft.Extensions.Options;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class SalesDocumentTests
{
    [Fact]
    public void SalesReturnCatalogMigrationParsesWithoutOpeningAnyDatabase()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "migrations", "055_sales_return_function.sql"));
        new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true).Parse(new StringReader(script), out var errors);
        Assert.Empty(errors);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static SourceDocumentLine Source(string type = "sales-order") => new(type, 12, "SO12", 1000016857, 1,
        new(2026, 11, 6), "M1", "Material", 243, "D1", 1, 400, 157, 243, "C1", "Customer", "01", "人民币",
        1, 13, 12, 13.56m, 1000016857, "SO12");
    private static DocumentPrepareRequest Request(string type = "sales-order", decimal qty = 2) => new("request1", "user", "User",
        "device", null, null, "test", new(new(2026, 11, 6), type == "sales-order" ? "BLUE" : "RED", "C1", "W1", null,
            null, null, "D1", null, null, type, "SO12", JsonSerializer.SerializeToElement(new Dictionary<string, object> {
                ["header.currencyName"] = "人民币", ["header.exchangeRate"] = 1, ["header.salesTypeCode"] = "01" })),
        [new("PRODUCT_LABEL", "TAG1", null, "M1", "U1", qty, null, type, 12, "SO12", 1000016857, 1, "M1", null,
            BatchNo: "B1", WarehouseCode: "W1", LocationCode: "L1")], SalesSourceLines: [Source(type)]);
    private static MaterialOutboundExecutionOperation Operation(DocumentPrepareRequest request) => new(1, 1, "group", "request1", "user",
        request.Header.DocumentDirection == "RED" ? "SALES_RETURN" : "SALES_DELIVERY", "sales-delivery", "PENDING", "PENDING", null, null,
        "NOT_REQUIRED", request.Header.SourceType!, "12", "SO12", "W1", "device", null, "test", "archive", "hash",
        [new(1, 1, "PRODUCT_LABEL", 1, null, null, "TAG1", 1000016857, 1, "M1", "B1", "U1", 4, request.Lines[0].RequestedQty,
            "W1", "L1", null, null, null, 41)]);
    private static GatewayDocumentRequest Build(DocumentPrepareRequest request)
    {
        var type = typeof(DocumentPrepareService).Assembly.GetType("LabelSystem.Api.DocumentGatewayRequestBuilder")!;
        return (GatewayDocumentRequest)type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [Operation(request), request, new ErpOperator("erp", "ERP")])!;
    }

    [Theory]
    [InlineData("delivery-note", "RED")]
    [InlineData("sales-return", "BLUE")]
    public async Task FixedDirectionCannotBeBypassedBeforeAnyIo(string kind, string direction)
    {
        var service = new MaterialOutboundSubmissionService(null!, null!, null!, null!, null!, null!);
        var request = Request() with { Header = Request().Header with { DocumentDirection = direction } };
        Assert.Equal("SALES_DIRECTION_INVALID", (await Assert.ThrowsAsync<ApiException>(() =>
            service.SubmitAsync(kind, request, null!, "test", false, default))).Code);
    }

    [Theory]
    [InlineData("sales-order", "delivery-note")]
    [InlineData("sales-order-return", "sales-return")]
    [InlineData("sales-delivery-return", "sales-return")]
    public void SourceChecksCurrentAvailabilityAndKeepsNativePriceAndCustomer(string type, string document)
    {
        var request = Request(type);
        SalesDocumentRules.ValidateSources(document, request, [Source(type)]);
        Assert.Equal("SOURCE_QUANTITY_EXCEEDED", Assert.Throws<ApiException>(() => SalesDocumentRules.ValidateSources(document,
            Request(type, 244), [Source(type)])).Code);
        Assert.Equal("SALES_SOURCE_CHANGED", Assert.Throws<ApiException>(() => SalesDocumentRules.ValidateSources(document,
            request, [Source(type) with { UnitPrice = 99 }])).Code);
        Assert.Equal("SALES_SOURCE_CHANGED", Assert.Throws<ApiException>(() => SalesDocumentRules.ValidateSources(document,
            request with { Header = request.Header with { PartnerCode = "OTHER" } }, [Source(type)])).Code);
        Assert.Equal("SALES_SOURCE_CHANGED", Assert.Throws<ApiException>(() => SalesDocumentRules.ValidateSources(document,
            request with { SalesSourceLines = null }, [Source(type)])).Code);
    }

    [Fact]
    public void ReturnUsesNetDispatchedAvailabilityWithoutSubtractingOldReturnsAgain()
    {
        var source = Source("sales-order-return") with { PlannedQuantity = 8, ProcessedQuantity = 3, RemainingQuantity = 5, OrderQuantity = 5 };
        var request = Request(source.SourceType, 5) with { SalesSourceLines = [source] };
        SalesDocumentRules.ValidateSources("sales-return", request, [source]);
        Assert.Equal("SOURCE_QUANTITY_EXCEEDED", Assert.Throws<ApiException>(() => SalesDocumentRules.ValidateSources("sales-return",
            request with { Lines = [request.Lines[0] with { RequestedQty = 6 }] }, [source])).Code);
    }

    [Fact]
    public void ShipmentUsesActualSalesOrderIdAndReturnPreservesOriginalDispatchLink()
    {
        var blue = Build(Request());
        Assert.Equal(0, blue.Header["breturnflag"]);
        Assert.Equal(2m, blue.Lines[0]["iquantity"]);
        Assert.Equal(1000016857, blue.Lines[0]["isosid"]);
        Assert.Equal(12m, blue.Lines[0]["iunitprice"]);
        Assert.Equal("L1", blue.Lines[0]["cposition"]);
        var returned = Build(Request("sales-delivery-return"));
        Assert.Equal(1, returned.Header["breturnflag"]);
        Assert.Equal(-2m, returned.Lines[0]["iquantity"]);
        Assert.Equal(1000016857, returned.Lines[0]["icorid"]);
        Assert.Equal("SO12", returned.Lines[0]["ccorcode"]);
        Assert.Equal(13.56m, returned.Lines[0]["itaxunitprice"]);
    }

    [Fact]
    public void MultipleOriginalDispatchesCannotTogetherExceedTheirSharedOrderReturnBalance()
    {
        var a = Source("sales-delivery-return") with { OrderQuantity = 3, RemainingQuantity = 3, SalesOrderAvailableQuantity = 3 };
        var b = a with { SourceLineId = a.SourceLineId + 1 };
        var request = Request(a.SourceType) with { SalesSourceLines = [a,b], Lines = [Request(a.SourceType).Lines[0],
            Request(a.SourceType).Lines[0] with { LabelCode = "TAG2", SourceLineId = b.SourceLineId }] };
        Assert.Equal("SOURCE_QUANTITY_EXCEEDED", Assert.Throws<ApiException>(() =>
            SalesDocumentRules.ValidateSources("sales-return", request, [a,b])).Code);
    }

    [Theory]
    [InlineData("BLUE")]
    [InlineData("RED")]
    public void ShipmentAndReturnDoNotCreateAnyTraceBackQuantityTask(string direction)
    {
        var request = Request() with { Header = Request().Header with { DocumentDirection = direction } };
        var result = new GatewayDocumentResult(true, true, "OK", "", "12", "DL12", "v1", null);
        Assert.Empty(TraceBackSyncPlanner.Build(Operation(request), result, direction == "RED",
            new Dictionary<int, (string?, string?)> { [1] = ("21", null) }, "ERP", "888"));
    }

    [Fact]
    public void MissingOptionalSalesSnapshotDoesNotChangeOldRequestHashes()
    {
        var request = Request() with { SalesSourceLines = null };
        Assert.DoesNotContain("salesSourceLines", JsonSerializer.Serialize(request, Json));
    }

    [Fact]
    public void NativeSalesLineIdIsPreferredOverTheXmlAutoNumber()
    {
        var document = JsonSerializer.SerializeToElement(new { lines = new[] { new { irowno = 1, autoid = 7, idlsid = 1000031711 } } });
        var method = typeof(MaterialOutboundExecutionService).GetMethod("ReadErpRows", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rows = (IReadOnlyDictionary<int, (string? LineId, string? PositionId)>)method.Invoke(null, ["sales-delivery", document])!;
        Assert.Equal("1000031711", rows[1].LineId);
    }

    [Fact]
    public async Task BeforeU8ShipmentRechecksLatestTraceBackFlowAndBlocksChangedLabel()
    {
        var snapshot = new TraceBackLabelSnapshot("TAG1", null, 4, 42, "g", "o", "M1", "Material", "B1", "U1", "Unit",
            "W1", "Warehouse", "L1", "Location", null, null, null, "identity", "AVAILABLE", []);
        var handler = new Snapshots(snapshot);
        var service = new TraceBackSnapshotService(new U8GatewayClient(new Factory(handler), new Monitor<U8GatewayOptions>(
            new() { BaseUrl = "http://test", ApiKey = "test-only" })), null!);
        await Assert.ThrowsAsync<ApiException>(() => service.ValidateBeforeU8Async("sales-delivery", Operation(Request()).Lines, false, default));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    { public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T, string?> listener) => null; }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Snapshots(TraceBackLabelSnapshot snapshot) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/v1/traceback/labels/snapshots", request.RequestUri!.AbsolutePath); Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { success = true, data = new[] { snapshot } }, Json), Encoding.UTF8, "application/json") });
        }
    }
}

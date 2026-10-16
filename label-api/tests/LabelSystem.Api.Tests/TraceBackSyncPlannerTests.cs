using System.Text.Json;
using LabelSystem.Api;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class TraceBackSyncPlannerTests
{
    [Fact]
    public void BuildsMaterialIssueAndReturnWithStableKeys()
    {
        var issue = TraceBackSyncPlanner.Build(
            Operation("material-outbound", 10, 4), Result(), false, Rows(), "操作员", "888").Single();
        var repeat = TraceBackSyncPlanner.Build(
            Operation("material-outbound", 10, 4), Result(), false, Rows(), "操作员", "888").Single();
        var returned = TraceBackSyncPlanner.Build(
            Operation("material-outbound", 10, 4), Result(), true, Rows(), "操作员", "888").Single();

        Assert.Equal("MATERIAL_ISSUE", issue.EventRole);
        Assert.Equal(-4m, Payload(issue).Quantity);
        Assert.Equal(issue.EventKey, repeat.EventKey);
        Assert.Equal(issue.PdaGoodsUuid, repeat.PdaGoodsUuid);
        Assert.Equal("MATERIAL_RETURN", returned.EventRole);
        Assert.Equal(4m, Payload(returned).Quantity);
    }

    [Fact]
    public void DistinguishesFullAndPartialMoves()
    {
        var full = TraceBackSyncPlanner.Build(
            Operation("transfer", 10, 10), Result(), false, Rows(), "操作员", "888").Single();
        var partial = TraceBackSyncPlanner.Build(
            Operation("location-adjustment", 10, 3), Result(), false, Rows(), "操作员", "888").Single();

        Assert.Equal("FULL_MOVE", full.EventRole);
        Assert.Equal(0m, Payload(full).Quantity);
        Assert.Equal("WH2", Payload(full).TargetWarehouseCode);
        Assert.Equal("P2", Payload(full).TargetLocationCode);
        Assert.Equal("PARTIAL_SOURCE", partial.EventRole);
        Assert.Equal(-3m, Payload(partial).Quantity);
        Assert.Null(Payload(partial).TargetWarehouseCode);
    }

    private static TraceBackSyncEventRequest Payload(TraceBackSyncTaskDraft task) =>
        JsonSerializer.Deserialize<TraceBackSyncEventRequest>(
            task.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static IReadOnlyDictionary<int, (string? LineId, string? PositionId)> Rows() =>
        new Dictionary<int, (string?, string?)> { [1] = ("9001", "9101") };

    private static GatewayDocumentResult Result() => new(
        true, true, "OK", "", "8001", "DOC001", "v1", (JsonElement?)null);

    private static MaterialOutboundExecutionOperation Operation(
        string documentType, decimal balance, decimal requested) => new(
        101, 100, "group", "pda-001", "user", "operation", documentType,
        "WRITING_ERP", "WRITING", null, null, "NOT_REQUIRED", "", "", "",
        "WH1", "device", null, null, "archive", "HASH",
        [new MaterialOutboundExecutionLine(
            201, 1, "PRODUCT_LABEL", 301, null, null, "008|MAT-01|B01|10|SN",
            null, null, "MAT-01", "B01", "EA", balance, requested,
            "WH1", "P1", "WH2", "P2", null, 5001)]);
}

using LabelSystem.Api;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class TraceBackSnapshotRulesTests
{
    [Fact]
    public void AvailableSnapshot_PassesBlueDocumentValidation()
    {
        TraceBackSnapshotRules.ValidateLine(
            Snapshot(12), "MAT-01", "B01", "EA", 8, "WH1", "P1",
            allowUsed: false, comparePosition: true, expectedBalance: 12);
    }

    [Fact]
    public void NegativeBalance_IsReconciliationRequired()
    {
        var error = Assert.Throws<ApiException>(() =>
            TraceBackSnapshotRules.RequireUsable(Snapshot(-1), allowUsed: false));

        Assert.Equal("TRACEBACK_RECONCILIATION_REQUIRED", error.Code);
    }

    [Fact]
    public void GatewayAnomaly_IsReconciliationRequired()
    {
        var snapshot = Snapshot(10) with
        {
            Status = "RECONCILIATION_REQUIRED",
            Anomalies = [new TraceBackAnomaly("TRACEBACK_GOODS_MISSING", "待核对")]
        };

        var error = Assert.Throws<ApiException>(() =>
            TraceBackSnapshotRules.RequireUsable(snapshot, allowUsed: false));

        Assert.Equal("TRACEBACK_RECONCILIATION_REQUIRED", error.Code);
    }

    [Fact]
    public void QuantityOverBalance_IsRejected()
    {
        var error = Assert.Throws<ApiException>(() =>
            TraceBackSnapshotRules.ValidateLine(
                Snapshot(5), "MAT-01", "B01", "EA", 6, "WH1", "P1",
                allowUsed: false, comparePosition: true));

        Assert.Equal("LABEL_QUANTITY_INSUFFICIENT", error.Code);
    }

    [Fact]
    public void ChangedPositionOrBalance_RequiresRescan()
    {
        var position = Assert.Throws<ApiException>(() =>
            TraceBackSnapshotRules.ValidateLine(
                Snapshot(10), "MAT-01", "B01", "EA", 5, "WH1", "P2",
                allowUsed: false, comparePosition: true));
        var balance = Assert.Throws<ApiException>(() =>
            TraceBackSnapshotRules.ValidateLine(
                Snapshot(10), "MAT-01", "B01", "EA", 5, "WH1", "P1",
                allowUsed: false, comparePosition: true, expectedBalance: 9));
        var version = Assert.Throws<ApiException>(() =>
            TraceBackSnapshotRules.ValidateLine(
                Snapshot(10), "MAT-01", "B01", "EA", 5, "WH1", "P1",
                allowUsed: false, comparePosition: true,
                expectedBalance: 10, expectedLatestFlowId: 19));

        Assert.Equal("TRACEBACK_SNAPSHOT_CHANGED", position.Code);
        Assert.Equal("TRACEBACK_SNAPSHOT_CHANGED", balance.Code);
        Assert.Equal("TRACEBACK_SNAPSHOT_CHANGED", version.Code);
    }

    [Fact]
    public void OriginalLabelRedReturn_AllowsUsedBalanceAndIgnoresOldPosition()
    {
        TraceBackSnapshotRules.ValidateLine(
            Snapshot(0) with { Status = "USED_UP" },
            "MAT-01", "B01", "EA", 3, "WH2", "P9",
            allowUsed: true, comparePosition: false);
    }

    private static TraceBackLabelSnapshot Snapshot(decimal balance) => new(
        "008|MAT-01|B01|10.00|2601010001", "2601010001", balance, 20,
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
        "MAT-01", "物料", "B01", "EA", "件",
        "WH1", "仓库", "P1", "货位", "材料出库", "PDA1", "MO1",
        new string('A', 64), balance == 0 ? "USED_UP" : "AVAILABLE", []);
}

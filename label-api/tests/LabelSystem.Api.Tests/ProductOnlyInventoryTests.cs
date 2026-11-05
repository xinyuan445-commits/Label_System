using System.Reflection;
using System.Text.Json;
using LabelSystem.Api;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class ProductOnlyInventoryTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static DocumentPrepareLine Line(string kind = "PRODUCT_LABEL", string? sourcePallet = null) =>
        new(kind, "TAG1", sourcePallet, "M1", "U1", 2, null, null, null, null, null, null, null, null,
            WarehouseCode: "W1", LocationCode: "L1", TargetWarehouseCode: "W2", TargetLocationCode: "L2");
    private static DocumentPrepareRequest Request(DocumentPrepareLine line) => new("request", "user", "User",
        null, null, null, null, new(new(2026, 11, 5), "BLUE", "PARTNER", "W1", "W2", null, "L2",
            "D1", "202", null, null, null, null), [line]);
    private static IReadOnlyDictionary<string, DocumentPrepareDefinition> Definitions =>
        (IReadOnlyDictionary<string, DocumentPrepareDefinition>)typeof(DocumentPrepareService)
            .GetField("Definitions", PrivateStatic)!.GetValue(null)!;

    [Fact]
    public void EveryDocumentRejectsPalletObjectsAndHistoricalPalletSelections()
    {
        var validate = typeof(DocumentPrepareService).GetMethod("ValidateBasicRequest", PrivateStatic)!;
        foreach (var definition in Definitions.Values)
        {
            foreach (var line in new[] { Line("PALLET"), Line(sourcePallet: "OLD-PALLET") })
            {
                var exception = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null,
                    [Request(line), definition, new Dictionary<string, bool>(), false]));
                var error = Assert.IsType<ApiException>(exception.InnerException);
                Assert.Contains(error.Code, new[] { "OBJECT_INVALID", "LEGACY_DETAIL_UNSUPPORTED" });
            }
        }
    }

    [Fact]
    public void OldMembershipDoesNotRequireWholePalletOrPreventPartialProductMoves()
    {
        var resolve = typeof(DocumentPrepareRepository).GetMethod("ResolveLines", PrivateStatic)!;
        var product = new ProductLabelSnapshot(1, "TAG1", 10, "ACTIVE", null, [1], "M1", "B1", "U1",
            "W1", "L1", "Material", "Unit", "Warehouse", "Location", 88, "OLD-PALLET", 101, "TRACEBACK");
        foreach (var type in new[] { "transfer-order", "location-adjustment" })
        {
            var result = (Array)resolve.Invoke(null, [Request(Line()), Definitions[type],
                new Dictionary<string, ProductLabelSnapshot> { ["TAG1"] = product }])!;
            Assert.Single(result.Cast<object>());
            Assert.Equal(1L, result.GetValue(0)!.GetType().GetProperty("LabelId")!.GetValue(result.GetValue(0)));
        }
    }

    [Theory]
    [InlineData("include_pallet_in_document_by_default")]
    [InlineData("allow_mixed_materials")]
    [InlineData("allow_mixed_batches")]
    [InlineData("allow_mixed_warehouses")]
    [InlineData("allow_mixed_locations")]
    [InlineData("allow_pallet_reuse")]
    [InlineData("allow_direct_use_of_palletized_label")]
    public async Task RemovedSettingsCannotBeReenabledByAnOldClient(string key)
    {
        var repository = new LabelSystemRepository(null!);
        var error = await Assert.ThrowsAsync<ApiException>(() => repository.UpsertSettingAsync("GLOBAL", key,
            new(null, "Old setting", JsonSerializer.SerializeToElement(true), true, null), "user", default));
        Assert.Equal("SETTING_RETIRED", error.Code);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using U8Gateway.U8;
using U8Gateway.Contracts.Approvals;
using U8Gateway.U8.Sources;
using U8Gateway.Contracts.Sources;
using U8Gateway.Metadata;
using U8Gateway.Core.Documents;
using U8Gateway.U8.Documents;
using U8Gateway.U8.Xml;

internal static class Program
{
    private static int Main()
    {
        try
        {
            LegacyClrActivation.Ensure();
            Equal("008", U8InventoryCountReader.NormalizeAccount("(default)@008"));
            Equal("888", U8InventoryCountReader.NormalizeAccount("888"));
            var header = U8InventoryCountReader.MapHeader(new ApprovalDocumentSummary
                { Id = 12, DocumentNo = "COUNT12", WarehouseCode = "W1", Status = "pending", Version = "0x01", LineCount = 1 },
                new Dictionary<string, object> { ["InCategoryCode"] = " 107 ", ["InCategoryName"] = "盘盈入库",
                    ["OutCategoryCode"] = "207", ["OutCategoryName"] = "盘亏出库",
                    ["CountByLocation"] = "0", ["WarehousePositionManaged"] = true });
            Equal((bool?)false, header.CountByLocation); Equal((bool?)true, header.WarehousePositionManaged);
            Equal((bool?)true, U8InventoryCountReader.ReadFlag(new Dictionary<string, object> { ["x"] = "1" }, "x"));
            Equal((bool?)false, U8InventoryCountReader.ReadFlag(new Dictionary<string, object> { ["x"] = false }, "x"));
            Equal(null, U8InventoryCountReader.ReadFlag(new Dictionary<string, object> { ["x"] = DBNull.Value }, "x"));
            Reject(() => U8InventoryCountReader.ReadFlag(new Dictionary<string, object> { ["x"] = "unknown" }, "x"));
            Equal("107", header.InCategoryCode); Equal("盘盈入库", header.InCategoryName);
            Equal("207", header.OutCategoryCode); Equal("盘亏出库", header.OutCategoryName);
            Equal(12L, header.Id); Equal("0x01", header.Version); Equal(1, header.LineCount);
            Equal("", U8InventoryCountReader.MapHeader(header,
                new Dictionary<string, object> { ["InCategoryCode"] = DBNull.Value }).InCategoryCode);
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["autoID"] = 41, ["irowno"] = 2, ["cInvCode"] = "M1", ["cCVBatch"] = "B1",
                ["cPosition"] = "L1", ["iCVQuantity"] = 10.5m, ["iCVCQuantity"] = DBNull.Value,
                ["_UnitCode"] = "U1", ["_UnitName"] = " 张 ", ["_StockUnitCode"] = "U1", ["cFree1"] = "blue"
            };
            var mapped = U8InventoryCountReader.MapLine(row, "W1", 1);
            Equal(41L, mapped.Id); Equal(10.5m, mapped.BookQuantity); Equal(null, mapped.ActualQuantity);
            Equal("W1", mapped.WarehouseCode); Equal("blue", mapped.FreeItems[0]);
            Equal(false, mapped.RequiresAuxiliaryUnit);
            Equal("U1", mapped.UnitCode); Equal("张", mapped.UnitName);
            row["iCVCQuantity"] = 0m;
            Equal((decimal?)0, U8InventoryCountReader.MapLine(row, "W1", 1).ActualQuantity);
            row["_StockUnitCode"] = "OTHER";
            Equal(true, U8InventoryCountReader.MapLine(row, "W1", 1).RequiresAuxiliaryUnit);
            row["iCVQuantity"] = "invalid";
            Reject(() => U8InventoryCountReader.MapLine(row, "W1", 1));
            U8InventoryCountReader.RequirePending(new ApprovalDocumentSummary { Status = "pending" });
            Reject(() => U8InventoryCountReader.RequirePending(new ApprovalDocumentSummary { Status = "audited" }));
            Reject(() => U8InventoryCountReader.RequirePending(new ApprovalDocumentSummary { Status = "pending", AuditedBy = "auditor" }));
            Reject(() => U8InventoryCountReader.RequirePending(null));
            SharedMutexContract();
            UploadSaveContract();
            Console.WriteLine("inventory count read/upload-save contracts passed (no SQL/U8 COM)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Equal(object expected, object actual)
    { if (!Equals(expected, actual)) throw new InvalidOperationException("Inventory count read contract mismatch."); }
    private static void UploadSaveContract()
    {
        CountJsonContract();
        foreach (var account in new[] { "008", "888" })
        foreach (var role in new[] { "COUNT_ADJUSTMENT", " count_adjustment " })
        {
            var writer = new U8Gateway.U8.TraceBack.TraceBackSyncWriter("unused", "UFDATA_" + account + "_2021", "unused", "unused",
                "production", true, account, "UFDATA_" + account + "_2021", account);
            try
            {
                writer.Apply(new U8Gateway.Contracts.TraceBack.TraceBackSyncEventRequest { EventKey = "count-test", EventRole = role,
                    Code = "TAG", ExpectedLatestFlowId = 1, ExpectedBalance = 49, Quantity = 1,
                    OrderUuid = Guid.NewGuid().ToString(), GoodsUuid = Guid.NewGuid().ToString() });
                throw new InvalidOperationException("Production count writes must be rejected.");
            }
            catch (U8Gateway.U8.TraceBack.TraceBackSyncException e) { Equal("COUNT_UPLOAD_TEST_ONLY", e.Code); }
        }
        var oldCounts = new[] { new CountSaveLabel { Code = "SAME", ActualQuantity = 49 }, new CountSaveLabel { Code = "OTHER", ActualQuantity = 14 } };
        var currentCounts = new[] { new CountSaveLabel { Code = "SAME", ActualQuantity = 50 }, new CountSaveLabel { Code = "NEW", ActualQuantity = 6 } };
        Equal(70m, InventoryCountSaveRules.AccumulatedActual(currentCounts, oldCounts));
        Equal(70m, InventoryCountSaveRules.AccumulatedActual(currentCounts, oldCounts));
        Equal(20m, InventoryCountSaveRules.AccumulatedActual(new[] { new CountSaveLabel { Code = "A", ActualQuantity = 14 },
            new CountSaveLabel { Code = "B", ActualQuantity = 6 } }, new CountSaveLabel[0]));
        Reject(() => InventoryCountSaveRules.AccumulatedActual(new CountSaveLabel[0], new[] {
            new CountSaveLabel { Code = "A", ActualQuantity = 1 }, new CountSaveLabel { Code = "a", ActualQuantity = 2 } }));
        var source = new InventoryCountSource { AccountId = "888", DatabaseName = "UFDATA_888_2021",
            Document = new InventoryCountHeader { Id = 12, DocumentNo = "COUNT12", WarehouseCode = "W1",
                Version = "v1", CountByLocation = true, InCategoryCode = "107", OutCategoryCode = "207" },
            Lines = new List<InventoryCountSourceLine> {
                new InventoryCountSourceLine { Id = 41, MaterialCode = "M1", BatchNo = "B1", WarehouseCode = "W1", LocationCode = "L1",
                    BookQuantity = 100, ActualQuantity = 0, UnitCode = "0101", UnitName = "张", FreeItems = new string[10] },
                new InventoryCountSourceLine { Id = 42, MaterialCode = "M2", BatchNo = "B2", WarehouseCode = "W1", LocationCode = "L2",
                    BookQuantity = 50, ActualQuantity = 8, UnitCode = "0101", FreeItems = new string[10] } } };
        var request = new DocumentOperationRequest { Header = new Dictionary<string, object> { ["cirdcode"] = "107", ["cordcode"] = "207" },
            Lines = new List<IDictionary<string, object>> { new Dictionary<string, object> { ["autoid"] = 41L, ["icvcquantity"] = 97m } } };
        var expected = InventoryCountSaveRules.ExpectedAfter(source, request);
        Equal((decimal?)97m, expected.Lines[0].ActualQuantity); Equal((decimal?)8m, expected.Lines[1].ActualQuantity);
        Equal((decimal?)0m, source.Lines[0].ActualQuantity); // Preparing a patch cannot mutate the saved baseline.
        InventoryCountSaveRules.CheckSource(source, source);
        source.Lines[0].ActualQuantity = 14;
        Equal((decimal?)97m, InventoryCountSaveRules.ExpectedAfter(source, request).Lines[0].ActualQuantity);
        source.Lines[0].ActualQuantity = 0;
        Reject(() => InventoryCountSaveRules.CheckSource(source, expected));
        expected.Lines[0].ActualQuantity = 0; expected.Lines[0].UnitName = "display changed";
        InventoryCountSaveRules.CheckSource(source, expected);
        expected.Document.Version = "v2"; Reject(() => InventoryCountSaveRules.CheckSource(source, expected));

        var definition = new ContractDocumentDefinitionStore().Get("inventory-count");
        Equal(true, definition.Capabilities.Create); Equal(true, definition.Capabilities.Update);
        var preparer = new DocumentWritePreparer();
        Equal(true, preparer.PreparePatch(definition, request, DateTimeOffset.Now).IsValid);
        var codec = new AdoPersistedXmlCodec();
        var original = codec.Encode(definition, new DocumentPayload { Header = new Dictionary<string, object> { ["id"] = 12, ["ccvmemo"] = "untouched" },
            Lines = new List<IDictionary<string, object>> {
                new Dictionary<string, object> { ["autoid"] = 41, ["cinvcode"] = "M1", ["icvquantity"] = 100m, ["icvcquantity"] = 0m },
                new Dictionary<string, object> { ["autoid"] = 42, ["cinvcode"] = "M2", ["icvquantity"] = 50m, ["icvcquantity"] = 8m } } });
        var merged = new AdoPersistedXmlPatchMerger(codec).Merge(definition, original,
            new DocumentPayload { Lines = request.Lines });
        var decoded = codec.Decode(definition, merged);
        Equal(2, decoded.Lines.Count); Equal("97", Convert.ToString(decoded.Lines[0]["icvcquantity"]));
        Equal("100", Convert.ToString(decoded.Lines[0]["icvquantity"]));
        Equal("8", Convert.ToString(decoded.Lines[1]["icvcquantity"])); Equal("untouched", decoded.Header["ccvmemo"]);
        request.Lines[0]["cinvcode"] = "WRONG";
        Equal(false, preparer.PreparePatch(definition, request, DateTimeOffset.Now).IsValid);
        request.Lines[0].Remove("cinvcode"); request.Lines[0]["_action"] = "delete";
        Equal(false, preparer.PreparePatch(definition, request, DateTimeOffset.Now).IsValid);
    }
    private static void CountJsonContract()
    {
        var request = new U8Gateway.Contracts.TraceBack.TraceBackSyncEventRequest {
            EventRole = "COUNT_ADJUSTMENT", PdaDocumentNo = "IC-159", OrderUuid = "test-order",
            ErpDocumentNo = "PDD1", ErpDetailId = "41", OperatorName = "测试盘点员", ExpectedBalance = 49, Quantity = 1,
            CountContext = new U8Gateway.Contracts.TraceBack.TraceBackCountContext {
                OperationId = 159, OperationLineId = 201, DocumentDate = "2026-11-05", WarehouseCode = "W1",
                InCategoryCode = "107", OutCategoryCode = "207", CountByLocation = true,
                SourceDocumentId = 12, SourceDocumentNo = "PDD1", TotalQuantity = 75 } };
        var source = new Newtonsoft.Json.Linq.JObject {
            ["code"] = "MAT1", ["pdaID"] = 999, ["quantity2"] = 1,
            ["Attribute"] = new Newtonsoft.Json.Linq.JObject { ["batchNumber"] = "B1", ["quantity2"] = 1, ["quantityD11"] = 50 },
            ["CurrencyGoodsData"] = new Newtonsoft.Json.Linq.JObject { ["uniquekeyGuid"] = "legacy-goods-identity",
                ["U8"] = new Newtonsoft.Json.Linq.JObject { ["bFree1"] = false, ["transferInWarehouse"] = "stale" } },
            ["inventory"] = new Newtonsoft.Json.Linq.JObject { ["storageLocationCode"] = "L1", ["onHandQuantity"] = 63 },
            ["SourceOrder"] = new Newtonsoft.Json.Linq.JObject(), ["mergeDataJson"] = "old" };
        var goods = U8Gateway.U8.TraceBack.TraceBackCountJson.Goods(request, source.ToString());
        Equal(50m, (decimal)goods["quantity"]); Equal(50m, (decimal)goods["quantity2"]);
        Equal(50m, (decimal)goods["Attribute"]["quantity2"]); Equal(49m, (decimal)goods["Attribute"]["quantityD11"]);
        Equal("legacy-goods-identity", (string)goods["CurrencyGoodsData"]["uniquekeyGuid"]);
        Equal(null, goods["CurrencyGoodsData"]["U8"]["transferInWarehouse"]);
        Equal(null, goods["SourceOrder"]); Equal(null, goods["mergeDataJson"]);
        Equal(null, goods["inventory"]["onHandQuantity"]); Equal("L1", (string)goods["inventory"]["storageLocationCode"]);
        Equal(201L, (long)goods["pdaID"]); Equal("IC-159", (string)goods["pdaOrderNumber"]);
        var order = U8Gateway.U8.TraceBack.TraceBackCountJson.Order(request, "仓库一", "盘盈入库", "盘亏出库", null);
        Equal("IC-159", (string)order["documentNumber"]); Equal("W1", (string)order["CurrencyOrderData"]["U8"]["cWhCode"]);
        Equal("1", (string)order["CurrencyOrderData"]["U8"]["bPosCheck"]["name"]);
        Equal("12", (string)order["CurrencyOrderData"]["SourceVoucherID"]); Equal("PDD1,", (string)order["SourceOrderList"]);
        Equal(Newtonsoft.Json.Linq.JTokenType.Array, order["showList"].Type);
        Equal(1793808000000L, (long)order["documentDateTs"]);
        request.CountContext.SourceDocumentId = null; request.CountContext.SourceDocumentNo = null;
        order = U8Gateway.U8.TraceBack.TraceBackCountJson.Order(request, "仓库一", "盘盈入库", "盘亏出库", null);
        Equal(null, order["CurrencyOrderData"]["SourceVoucherID"]); Equal(null, order["SourceOrderList"]);
    }
    private static void SharedMutexContract()
    {
        var name = @"Local\LabelSystem.QueueTest." + Guid.NewGuid().ToString("N");
        using (var queue = new StaOperationQueue(mutexName: name))
        using (var handle = Mutex.OpenExisting(name, MutexRights.ReadPermissions))
        {
            var rules = handle.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<MutexAccessRule>();
            Equal(true, rules.Any(rule => rule.IdentityReference.Value == "S-1-5-32-568" &&
                rule.AccessControlType == AccessControlType.Allow &&
                rule.MutexRights == (MutexRights.Synchronize | MutexRights.Modify)));
            Equal(42, queue.ExecuteAsync(() => 42).GetAwaiter().GetResult());
        }
        var limited = new MutexSecurity();
        limited.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User,
            MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        bool created;
        using (var existing = new Mutex(false, name, out created, limited))
        using (var queue = new StaOperationQueue(mutexName: name))
            Equal(43, queue.ExecuteAsync(() => 43).GetAwaiter().GetResult());
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected read contract rejection.");
    }
}

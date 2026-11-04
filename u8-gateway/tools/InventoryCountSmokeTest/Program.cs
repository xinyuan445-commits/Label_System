using System;
using System.Collections.Generic;
using U8Gateway.Contracts.Approvals;
using U8Gateway.U8.Sources;

internal static class Program
{
    private static int Main()
    {
        try
        {
            Equal("008", U8InventoryCountReader.NormalizeAccount("(default)@008"));
            Equal("888", U8InventoryCountReader.NormalizeAccount("888"));
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["autoID"] = 41, ["irowno"] = 2, ["cInvCode"] = "M1", ["cCVBatch"] = "B1",
                ["cPosition"] = "L1", ["iCVQuantity"] = 10.5m, ["iCVCQuantity"] = DBNull.Value,
                ["_UnitCode"] = "U1", ["_StockUnitCode"] = "U1", ["cFree1"] = "blue"
            };
            var mapped = U8InventoryCountReader.MapLine(row, "W1", 1);
            Equal(41L, mapped.Id); Equal(10.5m, mapped.BookQuantity); Equal(null, mapped.ActualQuantity);
            Equal("W1", mapped.WarehouseCode); Equal("blue", mapped.FreeItems[0]);
            Equal(false, mapped.RequiresAuxiliaryUnit);
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
            Console.WriteLine("inventory count read contract passed (no SQL/COM)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Equal(object expected, object actual)
    { if (!Equals(expected, actual)) throw new InvalidOperationException("Inventory count read contract mismatch."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected read contract rejection.");
    }
}

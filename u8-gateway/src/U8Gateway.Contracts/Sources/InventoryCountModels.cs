using System;
using System.Collections.Generic;
using U8Gateway.Contracts.Approvals;

namespace U8Gateway.Contracts.Sources
{
    // Read-only collection contract. Saving counts is deliberately a separate operation.
    public sealed class InventoryCountSource
    {
        public string AccountId { get; set; }
        public string DatabaseName { get; set; }
        public InventoryCountHeader Document { get; set; }
        public IList<InventoryCountSourceLine> Lines { get; set; } = new List<InventoryCountSourceLine>();
    }

    public sealed class InventoryCountHeader : ApprovalDocumentSummary
    {
        public string InCategoryCode { get; set; }
        public string InCategoryName { get; set; }
        public string OutCategoryCode { get; set; }
        public string OutCategoryName { get; set; }
        public bool? CountByLocation { get; set; }
        public bool? WarehousePositionManaged { get; set; }
    }

    public sealed class InventoryCountWarehouse
    {
        public string AccountId { get; set; }
        public string DatabaseName { get; set; }
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public bool PositionManaged { get; set; }
    }

    public sealed class InventoryCountSourceLine
    {
        public long Id { get; set; }
        public int LineNo { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public string BatchNo { get; set; }
        public string WarehouseCode { get; set; }
        public string LocationCode { get; set; }
        public string UnitCode { get; set; }
        public decimal BookQuantity { get; set; }
        public decimal? ActualQuantity { get; set; }
        public string[] FreeItems { get; set; } = new string[10];
        public bool RequiresAuxiliaryUnit { get; set; }
    }
}

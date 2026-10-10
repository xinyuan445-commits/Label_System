using System.Collections.Generic;

namespace U8Gateway.Contracts.Documents
{
    public static class DocumentTypeCodes
    {
        public const string PurchaseArrival = "purchase-arrival";
        public const string SubcontractArrival = "subcontract-arrival";
        public const string PurchaseReceipt = "purchase-receipt";
        public const string SalesDelivery = "sales-delivery";
        public const string SalesOutbound = "sales-outbound";
        public const string MaterialOutbound = "material-outbound";
        public const string Transfer = "transfer";
        public const string LocationAdjustment = "location-adjustment";
        public const string InventoryCount = "inventory-count";

        public static readonly IReadOnlyDictionary<string, string> Titles =
            new Dictionary<string, string>
            {
                [PurchaseArrival] = "采购到货",
                [SubcontractArrival] = "委外到货",
                [PurchaseReceipt] = "采购入库",
                [SalesDelivery] = "发货单",
                [SalesOutbound] = "销售出库单",
                [MaterialOutbound] = "材料出库单",
                [Transfer] = "调拨单",
                [LocationAdjustment] = "货位调整",
                [InventoryCount] = "盘点单"
            };
    }
}

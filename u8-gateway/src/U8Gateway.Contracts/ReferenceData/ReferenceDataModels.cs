using System.Collections.Generic;

namespace U8Gateway.Contracts.ReferenceData
{
    public sealed class RdStyleItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public byte Grade { get; set; }
        public string Direction { get; set; }
        public bool IsTerminal { get; set; }
        public string OppositeCode { get; set; }
        public bool IsRetail { get; set; }
        public string SourceRowVersion { get; set; }
    }

    public sealed class WarehousePositionItem
    {
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
    }

    public sealed class DepartmentItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
    }

    public sealed class MaterialItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Specification { get; set; }
        public string UnitCode { get; set; }
        public bool RequiresBatch { get; set; }
    }

    public sealed class DocumentTypeItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string RdStyleCode { get; set; }
        public bool IsDefault { get; set; }
        public string SourceRowVersion { get; set; }
    }

    public sealed class CurrencyItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public bool IsLocal { get; set; }
        public bool UsesExchangeRate { get; set; }
        public byte DecimalDigits { get; set; }
        public decimal? LatestRate { get; set; }
        public int? RateYear { get; set; }
        public byte? RatePeriod { get; set; }
        public string RateDate { get; set; }
        public string SourceRowVersion { get; set; }
    }

    public sealed class DocumentMasterData
    {
        public IList<DocumentTypeItem> PurchaseTypes { get; set; }
        public IList<DocumentTypeItem> SalesTypes { get; set; }
        public IList<CurrencyItem> Currencies { get; set; }
    }
}

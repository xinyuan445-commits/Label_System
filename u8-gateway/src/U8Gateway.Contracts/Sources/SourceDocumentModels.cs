using System;
using System.Collections.Generic;

namespace U8Gateway.Contracts.Sources
{
    public static class SourceDocumentTypeCodes
    {
        public const string ProductionOrder = "production-order";
        public const string SubcontractOrder = "subcontract-order";
        public const string OutsourcingOrder = "outsourcing-order";
        public const string PurchaseOrder = "purchase-order";
        public const string PurchaseArrival = "purchase-arrival";
        public const string SalesOrder = "sales-order";
        public const string SalesDelivery = "sales-delivery";
    }

    public sealed class SourceDocumentLine
    {
        public string SourceType { get; set; }
        public int SourceDocumentId { get; set; }
        public string SourceDocumentNo { get; set; }
        public int SourceLineId { get; set; }
        public int? SourceLineNo { get; set; }
        public DateTime? DocumentDate { get; set; }
        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public decimal OrderQuantity { get; set; }
        public string DepartmentCode { get; set; }
        public int MaterialCount { get; set; }
    }

    public sealed class SourceDocumentSearchResult
    {
        public IList<SourceDocumentLine> Items { get; set; } =
            new List<SourceDocumentLine>();
        public int Limit { get; set; }
        public bool HasMore { get; set; }
    }

    public sealed class MaterialRequirementRequest
    {
        public string SourceType { get; set; }
        public IList<int> SourceLineIds { get; set; } = new List<int>();
    }

    public sealed class SourceDocumentLinesRequest
    {
        public string SourceType { get; set; }
        public IList<int> SourceDocumentIds { get; set; } = new List<int>();
    }

    public sealed class MaterialRequirementLine
    {
        public string SourceType { get; set; }
        public int SourceDocumentId { get; set; }
        public string SourceDocumentNo { get; set; }
        public int SourceLineId { get; set; }
        public int? SourceLineNo { get; set; }
        public int RequirementId { get; set; }
        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public string DepartmentCode { get; set; }
        public string OperationSequence { get; set; }
        public bool RequiresBatch { get; set; }
        public decimal PlannedQuantity { get; set; }
        public decimal IssuedQuantity { get; set; }
        public decimal RemainingQuantity { get; set; }
    }
}

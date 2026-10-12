using System;
using System.Collections.Generic;

namespace U8Gateway.Contracts.Approvals
{
    public static class ApprovalDocumentStatuses
    {
        public const string Pending = "pending";
        public const string Audited = "audited";
    }

    public class ApprovalDocumentSummary
    {
        public long Id { get; set; }
        public string DocumentType { get; set; }
        public string DocumentNo { get; set; }
        public DateTime? DocumentDate { get; set; }
        public string PartnerLabel { get; set; }
        public string PartnerName { get; set; }
        public string WarehouseCode { get; set; }
        public string CreatedBy { get; set; }
        public string Status { get; set; }
        public string AuditedBy { get; set; }
        public DateTime? AuditedAt { get; set; }
        public string Version { get; set; }
        public int LineCount { get; set; }
        public decimal TotalQuantity { get; set; }
    }

    public sealed class ApprovalDocumentLine
    {
        public long Id { get; set; }
        public int? LineNo { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public string BatchNo { get; set; }
        public string WarehouseCode { get; set; }
        public string LocationCode { get; set; }
        public string TargetLocationCode { get; set; }
        public decimal Quantity { get; set; }
    }

    public sealed class ApprovalDocumentDetail : ApprovalDocumentSummary
    {
        public IList<ApprovalDocumentLine> Lines { get; set; } =
            new List<ApprovalDocumentLine>();
    }

    public sealed class ApprovalDocumentSearchResult
    {
        public IList<ApprovalDocumentSummary> Items { get; set; } =
            new List<ApprovalDocumentSummary>();
        public int Limit { get; set; }
        public bool HasMore { get; set; }
    }
}

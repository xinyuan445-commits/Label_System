using System.Collections.Generic;

namespace U8Gateway.Contracts.TraceBack
{
    public sealed class TraceBackSnapshotRequest
    {
        public IList<string> Codes { get; set; }
    }

    public sealed class TraceBackAnomaly
    {
        public string Code { get; set; }
        public string Message { get; set; }
    }

    public sealed class TraceBackLabelSnapshot
    {
        public string Code { get; set; }
        public string SerialNo { get; set; }
        public decimal? Balance { get; set; }
        public long? LatestFlowId { get; set; }
        public string GoodsUuid { get; set; }
        public string OrderUuid { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public string BatchNo { get; set; }
        public string UnitCode { get; set; }
        public string UnitName { get; set; }
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public string LocationCode { get; set; }
        public string LocationName { get; set; }
        public string SourceBusinessType { get; set; }
        public string PdaDocumentNo { get; set; }
        public string ErpDocumentNo { get; set; }
        public string IdentityHash { get; set; }
        public string Status { get; set; }
        public IList<TraceBackAnomaly> Anomalies { get; set; }
    }

    public sealed class TraceBackSyncEventRequest
    {
        public string EventKey { get; set; }
        public string EventRole { get; set; }
        public string Code { get; set; }
        public decimal Quantity { get; set; }
        public long ExpectedLatestFlowId { get; set; }
        public decimal ExpectedBalance { get; set; }
        public string OrderUuid { get; set; }
        public string GoodsUuid { get; set; }
        public string OrderType { get; set; }
        public string PdaDocumentNo { get; set; }
        public string ErpDocumentNo { get; set; }
        public string ErpDetailId { get; set; }
        public string OperatorName { get; set; }
        public string TargetWarehouseCode { get; set; }
        public string TargetLocationCode { get; set; }
        public bool ValidateOnly { get; set; }
    }

    public sealed class TraceBackSyncEventResult
    {
        public string EventKey { get; set; }
        public bool AlreadyApplied { get; set; }
        public long FlowId { get; set; }
        public decimal Balance { get; set; }
        public TraceBackLabelSnapshot Snapshot { get; set; }
    }

    public sealed class InitialLabelRegistrationRequest
    {
        public int ContractVersion { get; set; }
        public string EventKey { get; set; }
        public string PayloadHash { get; set; }
        public string Code { get; set; }
        public string SerialNo { get; set; }
        public decimal Quantity { get; set; }
        public string OrderUuid { get; set; }
        public string GoodsUuid { get; set; }
        public string OperatorName { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public string UnitCode { get; set; }
        public string BatchNo { get; set; }
        public string TargetWarehouseCode { get; set; }
        public string TargetLocationCode { get; set; }
        public string ManufactureDate { get; set; }
        public string ExpiryDate { get; set; }
        public string AttributesJson { get; set; }
        public string SourceDocumentType { get; set; }
        public string SourceDocumentId { get; set; }
        public string SourceDocumentNo { get; set; }
        public string ErpDetailId { get; set; }
        public bool ExpectedAbsent { get; set; }
        public long ExpectedLatestFlowId { get; set; }
        public decimal ExpectedBalance { get; set; }
    }
    public sealed class InitialLabelRegistrationResult
    {
        public string EventKey { get; set; }
        public string PayloadHash { get; set; }
        public bool AlreadyApplied { get; set; }
        public long FlowId { get; set; }
        public TraceBackLabelSnapshot Snapshot { get; set; }
    }
    public sealed class LegacyPrintEvidence
    {
        public string Code { get; set; }
        public long InitialFlowId { get; set; }
        public decimal InitialQuantity { get; set; }
        public string PrintedAt { get; set; }
        public string EvidenceHash { get; set; }
        public TraceBackLabelSnapshot Original { get; set; }
    }
}

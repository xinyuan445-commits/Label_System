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
}

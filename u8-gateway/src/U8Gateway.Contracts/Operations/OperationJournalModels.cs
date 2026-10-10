using System;
using System.Collections.Generic;

namespace U8Gateway.Contracts.Operations
{
    public sealed class OperationJournalStart
    {
        public string ClientRequestId { get; set; }
        public string RequestId { get; set; }
        public string Environment { get; set; }
        public string U8Database { get; set; }
        public string DocumentType { get; set; }
        public string DocumentTitle { get; set; }
        public string OperationType { get; set; }
        public string OperatorId { get; set; }
        public string OperatorName { get; set; }
        public string DeviceId { get; set; }
        public string DeviceModel { get; set; }
        public string ApkVersion { get; set; }
        public string H5Version { get; set; }
        public string BridgeVersion { get; set; }
        public string VoucherCode { get; set; }
        public string ExpectedVersion { get; set; }
        public string Reason { get; set; }
        public string ServiceVersion { get; set; }
        public string PayloadHash { get; set; }
        public object Payload { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }

    public sealed class OperationJournalEvent
    {
        public int RecordVersion { get; set; } = 1;
        public string Stage { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public IDictionary<string, object> Data { get; set; }
    }

    public sealed class OperationJournalHandle
    {
        public string ClientRequestId { get; set; }
        public string FilePath { get; set; }
    }
}

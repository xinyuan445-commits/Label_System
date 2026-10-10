using System;
using U8Gateway.Core.Documents;

namespace U8Gateway.Core.Operations
{
    public sealed class OperationRecord
    {
        public int RecordVersion { get; set; } = 1;
        public string ClientRequestId { get; set; }
        public string RequestId { get; set; }
        public string PayloadHash { get; set; }
        public string Environment { get; set; }
        public string DocumentType { get; set; }
        public string DocumentTitle { get; set; }
        public string OperationType { get; set; }
        public string TargetVoucherCode { get; set; }
        public string Status { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public DocumentOperationResult Result { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class OperationClaimResult
    {
        public string Outcome { get; set; }
        public OperationRecord Record { get; set; }
    }

    public static class OperationClaimOutcomes
    {
        public const string Claimed = "CLAIMED";
        public const string Replay = "REPLAY";
        public const string Pending = "PENDING";
        public const string Conflict = "CONFLICT";
    }
}

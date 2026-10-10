namespace U8Gateway.Core.Operations
{
    public static class OperationStages
    {
        public const string Received = "RECEIVED";
        public const string IdempotentReplay = "IDEMPOTENT_REPLAY";
        public const string DuplicateBlocked = "DUPLICATE_BLOCKED";
        public const string Validated = "VALIDATED";
        public const string U8Writing = "U8_WRITING";
        public const string U8Returned = "U8_RETURNED";
        public const string U8Created = "U8_CREATED";
        public const string U8Confirmed = "U8_CONFIRMED";
        public const string DatabaseCommitted = "DATABASE_COMMITTED";
        public const string Success = "SUCCESS";
        public const string Failed = "FAILED";
        public const string ResultUnknown = "RESULT_UNKNOWN";
        public const string ReconciliationRequired = "RECONCILIATION_REQUIRED";
    }
}

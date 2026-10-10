using System;
using U8Gateway.Core.Documents;

namespace U8Gateway.Core.Operations
{
    public interface IOperationRegistry
    {
        OperationClaimResult Claim(OperationRecord record);

        OperationRecord Get(string clientRequestId);

        OperationRecord Update(
            string clientRequestId,
            string status,
            string code,
            string message,
            DocumentOperationResult result,
            DateTimeOffset occurredAt);
    }
}

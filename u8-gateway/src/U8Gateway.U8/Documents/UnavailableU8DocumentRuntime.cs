using System.Threading;
using System.Threading.Tasks;
using U8Gateway.Core.Documents;

namespace U8Gateway.U8.Documents
{
    public sealed class UnavailableU8DocumentRuntime : IU8DocumentRuntime
    {
        public Task<DocumentOperationResult> ExecuteAsync(
            U8DocumentExecution execution,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DocumentOperationResult
            {
                Confirmed = false,
                Code = "U8_OPERATION_NOT_ENABLED",
                Message = "该单据操作尚未通过测试账套验证。",
                VoucherCode = execution?.VoucherCode
            });
        }
    }
}

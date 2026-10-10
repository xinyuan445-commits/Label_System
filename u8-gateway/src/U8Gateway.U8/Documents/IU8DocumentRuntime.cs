using System.Threading;
using System.Threading.Tasks;
using U8Gateway.Core.Documents;

namespace U8Gateway.U8.Documents
{
    public interface IU8DocumentRuntime
    {
        Task<DocumentOperationResult> ExecuteAsync(
            U8DocumentExecution execution,
            CancellationToken cancellationToken);
    }

    public sealed class U8DocumentExecution
    {
        public U8DocumentProfile Profile { get; set; }
        public string Operation { get; set; }
        public string VoucherCode { get; set; }
        public DocumentOperationRequest Request { get; set; }
    }
}

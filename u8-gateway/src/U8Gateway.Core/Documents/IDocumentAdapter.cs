using System.Threading;
using System.Threading.Tasks;

namespace U8Gateway.Core.Documents
{
    public interface IDocumentAdapter
    {
        string DocumentType { get; }
        Task<DocumentOperationResult> QueryAsync(
            string voucherCode,
            CancellationToken cancellationToken);
        Task<DocumentOperationResult> ValidateAsync(
            DocumentOperationRequest request,
            CancellationToken cancellationToken);
        Task<DocumentOperationResult> CreateAsync(
            DocumentOperationRequest request,
            CancellationToken cancellationToken);
        Task<DocumentOperationResult> UpdateAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken);
        Task<DocumentOperationResult> VerifyAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken);
        Task<DocumentOperationResult> UnverifyAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken);
        Task<DocumentOperationResult> DeleteAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken);
    }
}

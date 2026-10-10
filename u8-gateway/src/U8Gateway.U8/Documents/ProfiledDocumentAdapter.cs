using System;
using System.Threading;
using System.Threading.Tasks;
using U8Gateway.Contracts.Documents;
using U8Gateway.Core.Documents;

namespace U8Gateway.U8.Documents
{
    public abstract class ProfiledDocumentAdapter : IDocumentAdapter
    {
        private readonly IU8DocumentRuntime _runtime;

        protected ProfiledDocumentAdapter(
            U8DocumentProfile profile,
            IU8DocumentRuntime runtime)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        protected U8DocumentProfile Profile { get; }
        public string DocumentType => Profile.DocumentType;

        public Task<DocumentOperationResult> QueryAsync(
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return Execute(DocumentOperationCodes.Query, voucherCode, null, cancellationToken);
        }

        public Task<DocumentOperationResult> ValidateAsync(
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return Execute(DocumentOperationCodes.Validate, null, request, cancellationToken);
        }

        public Task<DocumentOperationResult> CreateAsync(
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return Execute(DocumentOperationCodes.Add, null, request, cancellationToken);
        }

        public Task<DocumentOperationResult> UpdateAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return Execute(DocumentOperationCodes.Edit, voucherCode, request, cancellationToken);
        }

        public Task<DocumentOperationResult> VerifyAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return Execute(DocumentOperationCodes.Verify, voucherCode, request, cancellationToken);
        }

        public Task<DocumentOperationResult> UnverifyAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return Execute(DocumentOperationCodes.Unverify, voucherCode, request, cancellationToken);
        }

        public Task<DocumentOperationResult> DeleteAsync(
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return Execute(DocumentOperationCodes.Delete, voucherCode, request, cancellationToken);
        }

        private Task<DocumentOperationResult> Execute(
            string operation,
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return _runtime.ExecuteAsync(new U8DocumentExecution
            {
                Profile = Profile,
                Operation = operation,
                VoucherCode = voucherCode,
                Request = request
            }, cancellationToken);
        }
    }

    public sealed class PurchaseDocumentAdapter : ProfiledDocumentAdapter
    {
        public PurchaseDocumentAdapter(U8DocumentProfile profile, IU8DocumentRuntime runtime)
            : base(profile, runtime) { }
    }

    public sealed class SalesDocumentAdapter : ProfiledDocumentAdapter
    {
        public SalesDocumentAdapter(U8DocumentProfile profile, IU8DocumentRuntime runtime)
            : base(profile, runtime) { }
    }

    public sealed class InventoryDocumentAdapter : ProfiledDocumentAdapter
    {
        public InventoryDocumentAdapter(U8DocumentProfile profile, IU8DocumentRuntime runtime)
            : base(profile, runtime) { }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using U8Gateway.Core.Documents;

namespace U8Gateway.U8.Documents
{
    public sealed class DocumentAdapterCatalog : IDocumentAdapterCatalog
    {
        private readonly IReadOnlyCollection<IDocumentAdapter> _adapters;

        public DocumentAdapterCatalog(
            U8DocumentProfileCatalog profiles,
            IU8DocumentRuntime runtime)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            _adapters = profiles.GetAll()
                .Select(profile => Create(profile, runtime))
                .ToArray();
        }

        public IReadOnlyCollection<IDocumentAdapter> GetAll()
        {
            return _adapters;
        }

        public IDocumentAdapter Get(string documentType)
        {
            if (string.IsNullOrWhiteSpace(documentType)) return null;
            return _adapters.FirstOrDefault(x => string.Equals(
                x.DocumentType,
                documentType.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }

        private static IDocumentAdapter Create(
            U8DocumentProfile profile,
            IU8DocumentRuntime runtime)
        {
            if (string.Equals(profile.Component, "VoucherCO_PU", StringComparison.OrdinalIgnoreCase))
            {
                return new PurchaseDocumentAdapter(profile, runtime);
            }
            if (string.Equals(profile.Component, "VoucherCO_SA", StringComparison.OrdinalIgnoreCase))
            {
                return new SalesDocumentAdapter(profile, runtime);
            }
            if (string.Equals(profile.Component, "USERPCO", StringComparison.OrdinalIgnoreCase))
            {
                return new InventoryDocumentAdapter(profile, runtime);
            }
            throw new InvalidOperationException("未识别的 U8 单据组件：" + profile.Component);
        }
    }
}

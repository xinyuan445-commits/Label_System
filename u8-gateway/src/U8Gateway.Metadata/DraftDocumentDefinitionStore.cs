using System;
using System.Collections.Generic;
using System.Linq;
using U8Gateway.Contracts.Documents;
using U8Gateway.Contracts.Metadata;
using U8Gateway.Core.Metadata;

namespace U8Gateway.Metadata
{
    public sealed class DraftDocumentDefinitionStore : IDocumentDefinitionStore
    {
        private readonly IReadOnlyCollection<DocumentDefinition> _definitions;

        public DraftDocumentDefinitionStore()
        {
            _definitions = DocumentTypeCodes.Titles
                .Select(x => new DocumentDefinition
                {
                    ContractVersion = 1,
                    DocumentType = x.Key,
                    Title = x.Value,
                    Status = "draft",
                    U8 = new U8DocumentMapping(),
                    Capabilities = new DocumentCapabilities()
                })
                .OrderBy(x => x.DocumentType, StringComparer.Ordinal)
                .ToArray();
        }

        public IReadOnlyCollection<DocumentDefinition> GetAll()
        {
            return _definitions;
        }

        public DocumentDefinition Get(string documentType)
        {
            if (string.IsNullOrWhiteSpace(documentType))
            {
                return null;
            }

            return _definitions.FirstOrDefault(x => string.Equals(
                x.DocumentType,
                documentType.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }
    }
}

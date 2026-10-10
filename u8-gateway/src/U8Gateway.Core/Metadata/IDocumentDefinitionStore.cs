using System.Collections.Generic;
using U8Gateway.Contracts.Metadata;

namespace U8Gateway.Core.Metadata
{
    public interface IDocumentDefinitionStore
    {
        IReadOnlyCollection<DocumentDefinition> GetAll();
        DocumentDefinition Get(string documentType);
    }
}

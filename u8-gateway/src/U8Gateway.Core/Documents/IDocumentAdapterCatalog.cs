using System.Collections.Generic;

namespace U8Gateway.Core.Documents
{
    public interface IDocumentAdapterCatalog
    {
        IReadOnlyCollection<IDocumentAdapter> GetAll();
        IDocumentAdapter Get(string documentType);
    }
}

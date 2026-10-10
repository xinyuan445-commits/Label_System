using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using U8Gateway.Contracts.Documents;
using U8Gateway.Contracts.Metadata;
using U8Gateway.Core.Metadata;

namespace U8Gateway.Metadata
{
    public sealed class ContractDocumentDefinitionStore : IDocumentDefinitionStore
    {
        private readonly IReadOnlyCollection<DocumentDefinition> _definitions;

        public ContractDocumentDefinitionStore()
        {
            var byType = new Dictionary<string, DocumentDefinition>(
                StringComparer.OrdinalIgnoreCase);
            Assembly assembly = typeof(ContractDocumentDefinitionStore).Assembly;
            foreach (string resourceName in assembly.GetManifestResourceNames()
                .Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (var reader = new StreamReader(stream))
                {
                    DocumentDefinition definition =
                        JsonConvert.DeserializeObject<DocumentDefinition>(
                            reader.ReadToEnd());
                    if (definition != null &&
                        !string.IsNullOrWhiteSpace(definition.DocumentType))
                    {
                        byType[definition.DocumentType] = definition;
                    }
                }
            }

            foreach (KeyValuePair<string, string> document in DocumentTypeCodes.Titles)
            {
                if (!byType.ContainsKey(document.Key))
                {
                    byType[document.Key] = new DocumentDefinition
                    {
                        ContractVersion = 1,
                        DocumentType = document.Key,
                        Title = document.Value,
                        Status = "draft",
                        U8 = new U8DocumentMapping(),
                        Capabilities = new DocumentCapabilities()
                    };
                }
            }

            _definitions = byType.Values
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

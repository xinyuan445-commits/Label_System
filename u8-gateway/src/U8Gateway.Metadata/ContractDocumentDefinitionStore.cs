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

            ApplyWritePolicies(assembly, byType);

            _definitions = byType.Values
                .OrderBy(x => x.DocumentType, StringComparer.Ordinal)
                .ToArray();
        }

        private static void ApplyWritePolicies(
            Assembly assembly,
            IDictionary<string, DocumentDefinition> definitions)
        {
            string resourceName = assembly.GetManifestResourceNames()
                .SingleOrDefault(x => x.EndsWith(
                    ".write-policies.json",
                    StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return;
            WritePolicyCatalog catalog;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (var reader = new StreamReader(stream))
                catalog = JsonConvert.DeserializeObject<WritePolicyCatalog>(reader.ReadToEnd());

            foreach (DocumentWritePolicy policy in catalog?.Documents ??
                new List<DocumentWritePolicy>())
            {
                DocumentDefinition definition;
                if (!definitions.TryGetValue(policy.DocumentType, out definition))
                    throw new InvalidDataException("写入策略对应的单据类型不存在：" + policy.DocumentType);
                definition.WritePolicyStatus = policy.Status;
                if (definition.Capabilities != null)
                    definition.Capabilities.Validate = true;
                definition.WriteScopes = new List<DocumentScopeWritePolicy>();
                foreach (ScopeWritePolicy source in policy.Scopes ??
                    new List<ScopeWritePolicy>())
                {
                    definition.WriteScopes.Add(new DocumentScopeWritePolicy
                    {
                        Scope = source.Scope,
                        MatchKeys = source.MatchKeys ?? new List<string>(),
                        AllowCreateRows = source.AllowCreateRows,
                        AllowDeleteRows = source.AllowDeleteRows
                    });
                    ApplyScopePolicy(definition, policy, source);
                }
            }
        }

        private static void ApplyScopePolicy(
            DocumentDefinition definition,
            DocumentWritePolicy policy,
            ScopeWritePolicy source)
        {
            Dictionary<string, DocumentFieldDefinition> fields = definition.Fields
                .Where(x => string.Equals(x.Scope, source.Scope,
                    StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            HashSet<string> create = Set(source.CreateWritable);
            HashSet<string> update = Set(source.UpdateWritable);
            HashSet<string> required = Set(source.RequiredOnCreate);
            HashSet<string> clearable = Set(source.Clearable);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.UnionWith(create);
            names.UnionWith(update);
            names.UnionWith(required);
            names.UnionWith(clearable);
            names.UnionWith(source.MatchKeys ?? new List<string>());
            names.UnionWith((source.Defaults ?? new Dictionary<string, object>()).Keys);
            names.UnionWith((source.DefaultRules ?? new Dictionary<string, string>()).Keys);
            names.UnionWith((source.RequiredWhen ?? new Dictionary<string, string>()).Keys);

            foreach (string name in names)
                if (!fields.ContainsKey(name))
                    throw new InvalidDataException(
                        definition.DocumentType + "/" + source.Scope +
                        " 写入策略字段不存在：" + name);

            foreach (DocumentFieldDefinition field in fields.Values)
            {
                field.CreateWritable = create.Contains(field.Name);
                field.UpdateWritable = update.Contains(field.Name);
                field.Clearable = clearable.Contains(field.Name);
                if (required.Contains(field.Name)) field.RequiredMode = "create";
                else if (source.RequiredWhen != null &&
                    source.RequiredWhen.ContainsKey(field.Name))
                {
                    field.RequiredMode = "conditional";
                    field.RequiredWhen = source.RequiredWhen[field.Name];
                }
                else if (field.CreateWritable || field.UpdateWritable)
                    field.RequiredMode = "optional";

                object defaultValue = null;
                string defaultRule = null;
                bool hasDefault = source.Defaults != null &&
                    source.Defaults.TryGetValue(field.Name, out defaultValue);
                bool hasRule = source.DefaultRules != null &&
                    source.DefaultRules.TryGetValue(field.Name, out defaultRule);
                if (hasDefault) field.DefaultValue = defaultValue;
                if (hasRule) field.DefaultRule = defaultRule;
                if (field.CreateWritable || field.UpdateWritable)
                    field.ValueSource = hasDefault || hasRule
                        ? "client-or-server-default"
                        : "client";
                else if ((source.MatchKeys ?? new List<string>()).Any(x =>
                    string.Equals(x, field.Name, StringComparison.OrdinalIgnoreCase)))
                    field.ValueSource = "u8-match-key";
                field.Evidence.Add("write-policy:" + policy.Status);
            }
        }

        private static HashSet<string> Set(IEnumerable<string> values)
        {
            return new HashSet<string>(values ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
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

    internal sealed class WritePolicyCatalog
    {
        public List<DocumentWritePolicy> Documents { get; set; } =
            new List<DocumentWritePolicy>();
    }

    internal sealed class DocumentWritePolicy
    {
        public string DocumentType { get; set; }
        public string Status { get; set; }
        public List<ScopeWritePolicy> Scopes { get; set; } =
            new List<ScopeWritePolicy>();
    }

    internal sealed class ScopeWritePolicy
    {
        public string Scope { get; set; }
        public List<string> MatchKeys { get; set; } = new List<string>();
        public bool AllowCreateRows { get; set; }
        public bool AllowDeleteRows { get; set; }
        public List<string> RequiredOnCreate { get; set; } = new List<string>();
        public List<string> CreateWritable { get; set; } = new List<string>();
        public List<string> UpdateWritable { get; set; } = new List<string>();
        public List<string> Clearable { get; set; } = new List<string>();
        public Dictionary<string, object> Defaults { get; set; } =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> DefaultRules { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RequiredWhen { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

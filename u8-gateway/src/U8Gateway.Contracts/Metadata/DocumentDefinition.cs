using System.Collections.Generic;

namespace U8Gateway.Contracts.Metadata
{
    public sealed class DocumentTypeSummary
    {
        public int ContractVersion { get; set; }
        public string DocumentType { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public string WritePolicyStatus { get; set; }
        public DocumentCapabilities Capabilities { get; set; }
    }

    public sealed class DocumentDefinition
    {
        public int ContractVersion { get; set; }
        public string DocumentType { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public U8DocumentMapping U8 { get; set; }
        public DocumentCapabilities Capabilities { get; set; }
        public string WritePolicyStatus { get; set; }
        public List<DocumentScopeWritePolicy> WriteScopes { get; set; } =
            new List<DocumentScopeWritePolicy>();
        public List<DocumentFieldDefinition> Fields { get; set; } =
            new List<DocumentFieldDefinition>();
    }

    public sealed class DocumentScopeWritePolicy
    {
        public string Scope { get; set; }
        public List<string> MatchKeys { get; set; } = new List<string>();
        public bool AllowCreateRows { get; set; }
        public bool AllowDeleteRows { get; set; }
    }

    public sealed class U8DocumentMapping
    {
        public string Version { get; set; }
        public string Component { get; set; }
        public string ComponentDocumentType { get; set; }
        public string VoucherType { get; set; }
        public string NumberType { get; set; }
        public string BusinessType { get; set; }
        public string HeaderTable { get; set; }
        public string LineTable { get; set; }
        public string PositionTable { get; set; }
    }

    public sealed class DocumentCapabilities
    {
        public bool Query { get; set; }
        public bool Validate { get; set; }
        public bool Create { get; set; }
        public bool Update { get; set; }
        public bool Verify { get; set; }
        public bool Unverify { get; set; }
        public bool Delete { get; set; }
    }

    public sealed class DocumentFieldDefinition
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Scope { get; set; }
        public string DataType { get; set; }
        public int? MaxLength { get; set; }
        public int? Precision { get; set; }
        public int? Scale { get; set; }
        public string AdoDbType { get; set; }
        public bool? AdoFixedLength { get; set; }
        public bool? AdoSchemaNullable { get; set; }
        public bool? AdoMayBeNull { get; set; }
        public bool? AdoKeyColumn { get; set; }
        public string AdoBaseTable { get; set; }
        public string AdoBaseColumn { get; set; }
        public string RequiredMode { get; set; }
        public string RequiredWhen { get; set; }
        public bool CreateWritable { get; set; }
        public bool UpdateWritable { get; set; }
        public bool Clearable { get; set; }
        public string ValueSource { get; set; }
        public object DefaultValue { get; set; }
        public string DefaultRule { get; set; }
        public string U8Field { get; set; }
        public string Validation { get; set; }
        public bool Returnable { get; set; }
        public List<string> Evidence { get; set; } = new List<string>();
    }
}

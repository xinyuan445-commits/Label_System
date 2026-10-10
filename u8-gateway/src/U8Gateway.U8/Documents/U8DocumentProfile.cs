namespace U8Gateway.U8.Documents
{
    public sealed class U8DocumentProfile
    {
        public string DocumentType { get; set; }
        public string Component { get; set; }
        public string ComponentDocumentType { get; set; }
        public string DatabaseVoucherType { get; set; }
        public string BusinessType { get; set; }
        public string HeaderTable { get; set; }
        public string LineTable { get; set; }
        public string PositionTable { get; set; }
        public string SchemaStatus { get; set; }
        public U8OperationBinding Operations { get; set; }
    }

    public sealed class U8OperationBinding
    {
        public string Initialize { get; set; }
        public string Query { get; set; }
        public string Create { get; set; }
        public string Update { get; set; }
        public string Verify { get; set; }
        public string Unverify { get; set; }
        public string Delete { get; set; }
        public string Readiness { get; set; }
    }
}

namespace U8Gateway.Contracts.ReferenceData
{
    public sealed class RdStyleItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public byte Grade { get; set; }
        public string Direction { get; set; }
        public bool IsTerminal { get; set; }
        public string OppositeCode { get; set; }
        public bool IsRetail { get; set; }
        public string SourceRowVersion { get; set; }
    }

    public sealed class WarehousePositionItem
    {
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
    }

    public sealed class DepartmentItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
    }
}

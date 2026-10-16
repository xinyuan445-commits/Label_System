namespace U8Gateway.Api.Models
{
    public sealed class BusinessSettingsInput
    {
        public string EnvironmentName { get; set; }
        public string LabelSqlServer { get; set; }
        public string LabelSqlDatabase { get; set; }
        public string LabelSqlUser { get; set; }
        public string LabelSqlPassword { get; set; }
        public string ErpAccountId { get; set; }
        public int ErpYear { get; set; }
        public string ErpDatabaseName { get; set; }
        public string GatewayBaseUrl { get; set; }
        public bool EnableDevelopmentLogin { get; set; }
        public bool U8WritesEnabled { get; set; }
        public string WriteConfirmation { get; set; }
    }

    public sealed class TraceBackCheckInput
    {
        public string Code { get; set; }
    }
}

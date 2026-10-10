namespace U8Gateway.U8.Documents
{
    public sealed class U8GatewayRuntimeOptions
    {
        public string EnvironmentName { get; set; }
        public bool EnableWrites { get; set; }
        public string WriteAllowedDatabase { get; set; }
        public string WriteAllowedAccountId { get; set; }
        public U8ConnectionOptions U8 { get; set; }
        public string SqlServer { get; set; }
        public string SqlDatabase { get; set; }
        public string SqlUser { get; set; }
        public string SqlPassword { get; set; }
    }
}

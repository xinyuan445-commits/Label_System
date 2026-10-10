namespace U8Gateway.Persistence.Configuration
{
    public sealed class GatewaySettings
    {
        public string EnvironmentName { get; set; } = "development";
        public string BusinessTimeZone { get; set; } = "China Standard Time";
        public string SqlServer { get; set; }
        public string SqlDatabase { get; set; }
        public string SqlUser { get; set; }
        public string SqlPassword { get; set; }
        public string U8Server { get; set; }
        public string U8AccountId { get; set; }
        public string U8Year { get; set; }
        public string U8Subsystem { get; set; } = "ST";
        public string U8User { get; set; }
        public string U8Password { get; set; }
        public string DefaultMaker { get; set; }
        public string OperationArchiveRoot { get; set; }
        public bool EnableTestWrites { get; set; }
        public string WriteAllowedDatabase { get; set; }
        public string WriteAllowedAccountId { get; set; }
        public string InternalApiKey { get; set; }
        public string AdminPasswordHash { get; set; }
        public string AdminPasswordSalt { get; set; }
        public int Revision { get; set; }
    }

    public sealed class GatewaySettingsView
    {
        public bool Configured { get; set; }
        public string EnvironmentName { get; set; }
        public string BusinessTimeZone { get; set; }
        public string SqlServer { get; set; }
        public string SqlDatabase { get; set; }
        public string SqlUser { get; set; }
        public bool HasSqlPassword { get; set; }
        public string U8Server { get; set; }
        public string U8AccountId { get; set; }
        public string U8Year { get; set; }
        public string U8Subsystem { get; set; }
        public string U8User { get; set; }
        public bool HasU8Password { get; set; }
        public string DefaultMaker { get; set; }
        public string OperationArchiveRoot { get; set; }
        public bool EnableTestWrites { get; set; }
        public string WriteAllowedDatabase { get; set; }
        public string WriteAllowedAccountId { get; set; }
        public bool HasInternalApiKey { get; set; }
        public int Revision { get; set; }
    }
}

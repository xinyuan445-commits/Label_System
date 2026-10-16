namespace U8Gateway.Api.Models
{
    public sealed class AdminSettingsInput
    {
        public string EnvironmentName { get; set; }
        public string BusinessTimeZone { get; set; }
        public string SqlServer { get; set; }
        public string SqlDatabase { get; set; }
        public string SqlUser { get; set; }
        public string SqlPassword { get; set; }
        public string U8Server { get; set; }
        public string U8AccountId { get; set; }
        public string U8Year { get; set; }
        public string U8Subsystem { get; set; }
        public string U8User { get; set; }
        public string U8Password { get; set; }
        public string DefaultMaker { get; set; }
        public string OperationArchiveRoot { get; set; }
        public bool EnableTestWrites { get; set; }
        public string WriteAllowedDatabase { get; set; }
        public string WriteAllowedAccountId { get; set; }
        public string WriteConfirmation { get; set; }
        public string NewAdminPassword { get; set; }
    }
}

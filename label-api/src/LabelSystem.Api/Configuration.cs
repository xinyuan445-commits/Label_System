namespace LabelSystem.Api;

public sealed class LabelSystemOptions
{
    public string Environment { get; set; } = "development";
    public string Version { get; set; } = "0.2.0";
    public string ErpAccountId { get; set; } = "888";
    public int ErpYear { get; set; } = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Year;
    public string ErpDatabaseName { get; set; } = "UFDATA_888_2021";
    public int LockMinutes { get; set; } = 15;
    public bool U8WritesEnabled { get; set; }
    public bool EnableDevelopmentLogin { get; set; } = true;
}

public sealed class U8GatewayOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class ArchiveOptions
{
    public string RootPath { get; set; } = "runtime-data/operation-archive";
}

public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class ManagementAccessOptions
{
    public string PasswordHash { get; set; } = string.Empty;
    public int ElevationMinutes { get; set; } = 10;
}

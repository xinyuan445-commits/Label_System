using System.Diagnostics;
using System.Text.Json;
using LabelSystem.Api;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// 运维工具只同步物料缓存。配置由现有外置文件加载，凭据从不输出。
if (args.Length != 4 || args[0] != "--sync")
    throw new ArgumentException("参数：--sync <账套> <U8数据库> <标签数据库>；外置配置通过 LABELSYSTEM_CONFIG_PATH 指定。");
var configPath = Environment.GetEnvironmentVariable("LABELSYSTEM_CONFIG_PATH")
    ?? throw new InvalidOperationException("缺少外置配置路径。");
var configuration = new ConfigurationBuilder().AddJsonFile(configPath, optional: false).Build();
var settings = configuration.GetSection("LabelSystem").Get<LabelSystemOptions>()!;
var sql = new SqlConnectionStringBuilder(configuration.GetConnectionString("LabelSystem"));
if (settings.ErpAccountId != args[1] || settings.ErpDatabaseName != args[2] || sql.InitialCatalog != args[3])
    throw new InvalidOperationException("当前账套或数据库与命令指定的目标不一致，已停止。");
var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.Configure<LabelSystemOptions>(configuration.GetSection("LabelSystem"));
services.Configure<U8GatewayOptions>(configuration.GetSection("U8Gateway"));
services.AddSingleton<SqlConnectionFactory>();
services.AddScoped<ReferenceDataRepository>();
services.AddScoped<ReferenceDataSyncService>();
services.AddScoped<U8GatewayClient>();
services.AddHttpClient(nameof(U8GatewayClient), client => client.Timeout = TimeSpan.FromSeconds(120));
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var sync = scope.ServiceProvider.GetRequiredService<ReferenceDataSyncService>();
var repository = scope.ServiceProvider.GetRequiredService<ReferenceDataRepository>();
var gateway = scope.ServiceProvider.GetRequiredService<U8GatewayClient>();
var timer = Stopwatch.StartNew();
var result = await sync.SyncMaterialsAsync("deployment-material-sync", CancellationToken.None);
var elapsed = timer.ElapsedMilliseconds;
var source = await gateway.GetMaterialsSnapshotAsync(CancellationToken.None);
if (result.ItemCount != source.ItemCount)
    throw new InvalidOperationException("同步后来源条数已经变化，请重新核对。");
var byCode = MaterialSnapshotRules.Validate(source, args[1], args[2])
    .ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
var verified = 0;
await using (var connection = new SqlConnection(sql.ConnectionString))
{
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT material_code,material_name,specification,unit_code,requires_batch
        FROM dbo.u8_material_cache WHERE is_active=1
            AND source_account_id=@account AND source_database_name=@database;
        """, connection);
    command.Parameters.AddWithValue("@account", args[1]);
    command.Parameters.AddWithValue("@database", args[2]);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var cached = new MaterialSourceItem(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
        if (!byCode.TryGetValue(cached.Code, out var expected) || cached != expected)
            throw new InvalidOperationException("本地物料档案与来源快照字段不一致。");
        verified++;
    }
}
if (verified != source.ItemCount) throw new InvalidOperationException("本地物料档案数量不一致。");
var sample = source.Items[source.Items.Count / 2];
var found = await repository.SearchMaterialsAsync(sample.Code, 1, args[1], args[2], CancellationToken.None);
if (found.Count != 1 || found[0] != sample)
    throw new InvalidOperationException("本地料号精确查询与来源不一致。");
// 保持原有上传前实时接口可用，并核对选中物料。
var current = await gateway.SearchMaterialsAsync(sample.Code, 1, CancellationToken.None);
if (current.Count != 1 || current[0] != sample)
    throw new InvalidOperationException("U8 实时复核接口结果不一致。");
Console.WriteLine(JsonSerializer.Serialize(new
{
    result.Status, result.ItemCount, ElapsedMilliseconds = elapsed,
    Account = args[1], U8Database = args[2], LabelDatabase = args[3],
    FullFieldMatches = verified, ExactSearchMatches = true, LiveValidationMatches = true
}));

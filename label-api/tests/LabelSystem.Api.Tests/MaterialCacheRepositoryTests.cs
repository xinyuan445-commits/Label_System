using LabelSystem.Api;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class MaterialDatabaseFactAttribute : FactAttribute
{
    public MaterialDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LABELSYSTEM_TEST_SQL")))
            Skip = "设置 LABELSYSTEM_TEST_SQL 后在独立临时数据库验证，测试结束自动删除该临时库。";
    }
}

public sealed class MaterialCacheRepositoryTests
{
    [MaterialDatabaseFact]
    public async Task SnapshotReplacement_IsAtomicScopedAndSerialized()
    {
        var database = "LabelSystem_MaterialTest_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("LABELSYSTEM_TEST_SQL"))
            { InitialCatalog = "master", Pooling = false };
        await using var admin = new SqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await Execute(admin, $"CREATE DATABASE [{database}] COLLATE Chinese_PRC_CI_AS;");
        try
        {
            builder.InitialCatalog = database;
            await using (var setup = new SqlConnection(builder.ConnectionString))
            {
                await setup.OpenAsync();
                foreach (var script in new[] { "001_label_business_schema_draft.sql",
                    "002_seed_settings_and_source_reservation_index.sql", "003_cache_u8_rd_style.sql",
                    "004_fix_reference_data_sync_time_constraint.sql" })
                    await Execute(setup, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "migrations", script)));
                // 016 的缓存只依赖 003/004；015 是迁移顺序门禁，不需要建立标签测试数据。
                await Execute(setup, "INSERT dbo.schema_migration(migration_id,description) VALUES ('015',N'测试前置门禁');");
                await Execute(setup, await File.ReadAllTextAsync(Path.Combine(
                    AppContext.BaseDirectory, "migrations", "049_cache_u8_materials.sql")));
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:LabelSystem"] = builder.ConnectionString }).Build();
            var repository = new ReferenceDataRepository(new SqlConnectionFactory(configuration));
            var token = CancellationToken.None;
            var notReady = await Assert.ThrowsAsync<ApiException>(() =>
                repository.SearchMaterialsAsync("", 50, "008", "UFDATA_008_2021", token));
            Assert.Equal("MATERIAL_CACHE_NOT_READY", notReady.Code);

            await using (var syncLock = await repository.AcquireMaterialSyncLockAsync(token))
            {
                var busy = await Assert.ThrowsAsync<ApiException>(() => repository.AcquireMaterialSyncLockAsync(token));
                Assert.Equal("MATERIAL_SYNC_IN_PROGRESS", busy.Code);
                await repository.BeginSyncAsync("materials", "test", token);
                await repository.ReplaceMaterialsAsync([
                    new("M1", "旧名称", "旧规格", "EA", false),
                    new("RETIRE", "停用料", "", "EA", false),
                    new("M%_[]", "特殊字符", "", "EA", false)
                ], "008", "UFDATA_008_2021", "test", token);
            }
            // Dispose 释放 Session 锁；第二轮可以立刻取得。
            await using (var syncLock = await repository.AcquireMaterialSyncLockAsync(token))
            {
                await repository.BeginSyncAsync("materials", "test", token);
                await repository.ReplaceMaterialsAsync([
                    new("M1", "新名称", "新规格", "BOX", true),
                    new("M%_[]", "特殊字符", "", "EA", false)
                ], "008", "UFDATA_008_2021", "test", token);
            }
            var found = await repository.SearchMaterialsAsync("M1", 1, "008", "UFDATA_008_2021", token);
            Assert.Equal(new MaterialSourceItem("M1", "新名称", "新规格", "BOX", true), Assert.Single(found));
            Assert.Empty(await repository.SearchMaterialsAsync("RETIRE", 50, "008", "UFDATA_008_2021", token));
            Assert.Single(await repository.SearchMaterialsAsync("%_[]", 50, "008", "UFDATA_008_2021", token));
            Assert.Equal("MATERIAL_CACHE_NOT_READY", (await Assert.ThrowsAsync<ApiException>(() =>
                repository.SearchMaterialsAsync("", 50, "888", "UFDATA_888_2021", token))).Code);
            var status = (await repository.ListSyncStatusAsync(token)).Single(x => x.Key == "materials");

            await repository.BeginSyncAsync("materials", "test", token);
            // SQL 拒绝暂存表重复键，事务整体回滚；上一次缓存及成功时间必须保留。
            await Assert.ThrowsAsync<SqlException>(() => repository.ReplaceMaterialsAsync([
                new("M1", "错误覆盖", "", "EA", false), new("M1", "重复", "", "EA", false)
            ], "008", "UFDATA_008_2021", "test", token));
            await repository.FailSyncAsync("materials", "test", token);
            Assert.Equal("新名称", Assert.Single(await repository.SearchMaterialsAsync(
                "M1", 1, "008", "UFDATA_008_2021", token)).Name);
            var failed = (await repository.ListSyncStatusAsync(token)).Single(x => x.Key == "materials");
            Assert.Equal("FAILED", failed.Status);
            Assert.Equal(status.LastSuccessAt, failed.LastSuccessAt);

            await repository.BeginSyncAsync("materials", "test", token);
            await repository.ReplaceMaterialsAsync([new("RETIRE", "重新启用", "", "EA", false)],
                "008", "UFDATA_008_2021", "test", token);
            Assert.Equal("重新启用", Assert.Single(await repository.SearchMaterialsAsync(
                "RETIRE", 1, "008", "UFDATA_008_2021", token)).Name);
            await using var check = new SqlConnection(builder.ConnectionString);
            await check.OpenAsync();
            await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.u8_material_cache WHERE material_code='M1' AND is_active=0;", check);
            Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
        }
        finally
        {
            // database 仅来自固定前缀和 GUID，绝不使用输入连接的业务库名称作为删除目标。
            await Execute(admin, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}

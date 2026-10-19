using System.Data;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed partial class ReferenceDataRepository
{
    public async Task<SqlConnection> AcquireMaterialSyncLockAsync(CancellationToken cancellationToken)
    {
        var connection = connections.Create();
        try
        {
            // 关闭池化，Dispose 真正断开 Session，可靠释放 applock。
            connection.ConnectionString = new SqlConnectionStringBuilder(connection.ConnectionString)
                { Pooling = false }.ConnectionString;
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=N'reference-data:materials',
                    @LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0;
                SELECT @result;
                """, connection);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "MATERIAL_SYNC_IN_PROGRESS", "物料正在同步，请稍后再试。");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<MaterialSourceItem>> SearchMaterialsAsync(
        string keyword, int limit, string accountId, string databaseName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF NOT EXISTS (
                SELECT 1 FROM dbo.reference_data_sync_state
                WHERE sync_key='materials' AND last_success_at IS NOT NULL
                    AND source_account_id=@account AND source_database_name=@database)
                SELECT CAST(0 AS bit);
            ELSE SELECT CAST(1 AS bit);

            SELECT TOP (@limit) material_code,material_name,specification,unit_code,requires_batch
            FROM dbo.u8_material_cache
            WHERE source_account_id=@account AND source_database_name=@database AND is_active=1
                AND (@keyword=N'' OR material_code LIKE @pattern ESCAPE N'~'
                    OR material_name LIKE @pattern ESCAPE N'~')
            ORDER BY CASE WHEN material_code=@keyword THEN 0 ELSE 1 END,material_code;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@account", SqlDbType.NVarChar, 20).Value = accountId;
        command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = databaseName;
        command.Parameters.Add("@keyword", SqlDbType.NVarChar, 100).Value = keyword;
        command.Parameters.Add("@pattern", SqlDbType.NVarChar, 204).Value = "%" +
            keyword.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_").Replace("[", "~[") + "%";
        command.Parameters.Add("@limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 100);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0))
            throw new ApiException(StatusCodes.Status409Conflict,
                "MATERIAL_CACHE_NOT_READY", "请先同步物料档案。");
        await reader.NextResultAsync(cancellationToken);
        var result = new List<MaterialSourceItem>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MaterialSourceItem(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return result;
    }

    public async Task<DataSyncResult> ReplaceMaterialsAsync(
        IReadOnlyList<MaterialSourceItem> rows, string accountId, string databaseName,
        string userId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var create = new SqlCommand("""
            CREATE TABLE #materials (
                material_code nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                material_name nvarchar(200) NOT NULL, specification nvarchar(500) NOT NULL,
                unit_code nvarchar(50) NOT NULL, requires_batch bit NOT NULL);
            """, connection, transaction))
            await create.ExecuteNonQueryAsync(cancellationToken);

        using var data = new DataTable();
        data.Columns.Add("material_code", typeof(string));
        data.Columns.Add("material_name", typeof(string));
        data.Columns.Add("specification", typeof(string));
        data.Columns.Add("unit_code", typeof(string));
        data.Columns.Add("requires_batch", typeof(bool));
        foreach (var row in rows)
            data.Rows.Add(row.Code, row.Name, row.Specification, row.UnitCode, row.RequiresBatch);
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction))
        {
            bulk.DestinationTableName = "#materials";
            bulk.BatchSize = 5000;
            bulk.BulkCopyTimeout = 60;
            foreach (DataColumn column in data.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(data, cancellationToken);
        }

        // 开始时间来自 SQL Server，完成时间也使用同一时钟，兼容跨主机部署。
        DateTimeOffset completedAt;
        await using (var clock = new SqlCommand(
            "SELECT CONVERT(datetime2(3),DATEADD(HOUR,8,SYSUTCDATETIME()));", connection, transaction))
            completedAt = ToBeijingOffset((DateTime)(await clock.ExecuteScalarAsync(cancellationToken))!);
        const string sql = """
            UPDATE c SET material_name=s.material_name,specification=s.specification,
                unit_code=s.unit_code,requires_batch=s.requires_batch,is_active=1,
                last_seen_at=@now,updated_at=@now
            FROM dbo.u8_material_cache c JOIN #materials s ON s.material_code=c.material_code
            WHERE c.source_account_id=@account AND c.source_database_name=@database;

            INSERT dbo.u8_material_cache
                (source_account_id,source_database_name,material_code,material_name,
                 specification,unit_code,requires_batch,is_active,last_seen_at,created_at,updated_at)
            SELECT @account,@database,s.material_code,s.material_name,s.specification,
                s.unit_code,s.requires_batch,1,@now,@now,@now
            FROM #materials s WHERE NOT EXISTS (
                SELECT 1 FROM dbo.u8_material_cache c WHERE c.source_account_id=@account
                    AND c.source_database_name=@database AND c.material_code=s.material_code);

            UPDATE c SET is_active=0,updated_at=@now FROM dbo.u8_material_cache c
            WHERE c.source_account_id=@account AND c.source_database_name=@database AND c.is_active=1
                AND NOT EXISTS (SELECT 1 FROM #materials s WHERE s.material_code=c.material_code);

            UPDATE dbo.reference_data_sync_state SET
                source_account_id=@account,source_database_name=@database WHERE sync_key='materials';
            """;
        await using (var command = new SqlCommand(sql, connection, transaction))
        {
            command.CommandTimeout = 60;
            command.Parameters.Add("@account", SqlDbType.NVarChar, 20).Value = accountId;
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = databaseName;
            command.Parameters.Add("@now", SqlDbType.DateTime2).Value = completedAt.DateTime;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await CompleteSyncAsync(connection, transaction, "materials", rows.Count,
            userId, completedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DataSyncResult("materials", "SUCCESS", rows.Count, completedAt);
    }
}

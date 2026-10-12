using System.Data;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class ReferenceDataRepository(SqlConnectionFactory connections)
{
    private const string RdStyleSyncKey = "rd-styles";
    private const string WarehousePositionSyncKey = "warehouse-positions";
    private const string DepartmentSyncKey = "departments";

    public async Task<IReadOnlyList<DataSyncStatusItem>> ListSyncStatusAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT sync_key, sync_name, status, item_count, last_started_at,
                   last_completed_at, last_success_at, synced_by, error_message
            FROM dbo.reference_data_sync_state
            ORDER BY sync_key;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DataSyncStatusItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DataSyncStatusItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                ReadDateTimeOffset(reader, 4), ReadDateTimeOffset(reader, 5), ReadDateTimeOffset(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return result;
    }

    public async Task<IReadOnlyList<RdStyleCacheItem>> ListRdStylesAsync(
        string? direction,
        bool terminalOnly,
        CancellationToken cancellationToken)
    {
        direction = string.IsNullOrWhiteSpace(direction) ? null : direction.Trim().ToUpperInvariant();
        if (direction is not null and not ("IN" or "OUT"))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "RD_STYLE_DIRECTION_INVALID", "出入库方向无效。");

        const string sql = """
            SELECT rd_style_code, rd_style_name, rd_grade, direction, is_terminal,
                   opposite_code, is_retail, is_active, updated_at
            FROM dbo.u8_rd_style_cache
            WHERE is_active = 1
              AND (@direction IS NULL OR direction = @direction)
              AND (@terminal_only = 0 OR is_terminal = 1)
            ORDER BY direction DESC, rd_style_code;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@direction", SqlDbType.VarChar, 10).Value =
            direction is null ? DBNull.Value : direction;
        command.Parameters.Add("@terminal_only", SqlDbType.Bit).Value = terminalOnly;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RdStyleCacheItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RdStyleCacheItem(
                reader.GetString(0), reader.GetString(1), reader.GetByte(2), reader.GetString(3),
                reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6), reader.GetBoolean(7), ToBeijingOffset(reader.GetDateTime(8))));
        }
        return result;
    }

    public async Task<IReadOnlyList<WarehousePositionCacheItem>> ListWarehousePositionsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT w.warehouse_code, w.warehouse_name, p.position_code, p.position_name,
                   CASE WHEN p.updated_at > w.updated_at THEN p.updated_at ELSE w.updated_at END
            FROM dbo.u8_warehouse_cache w
            LEFT JOIN dbo.u8_position_cache p
              ON p.warehouse_code = w.warehouse_code AND p.is_active = 1
            WHERE w.is_active = 1
            ORDER BY w.warehouse_code, p.position_code;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<WarehousePositionCacheItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WarehousePositionCacheItem(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ToBeijingOffset(reader.GetDateTime(4))));
        }
        return result;
    }

    public async Task<IReadOnlyList<DepartmentCacheItem>> ListDepartmentsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT department_code, department_name, updated_at
            FROM dbo.u8_department_cache
            WHERE is_active = 1
            ORDER BY department_code;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DepartmentCacheItem>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new DepartmentCacheItem(
                reader.GetString(0), reader.GetString(1), ToBeijingOffset(reader.GetDateTime(2))));
        return result;
    }

    public async Task BeginSyncAsync(
        string syncKey,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.reference_data_sync_state
            SET status = 'RUNNING', last_started_at = DATEADD(HOUR, 8, SYSUTCDATETIME()),
                last_completed_at = NULL, synced_by = @synced_by, error_message = NULL
            WHERE sync_key = @sync_key;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@sync_key", SqlDbType.VarChar, 50).Value = syncKey;
        command.Parameters.Add("@synced_by", SqlDbType.NVarChar, 100).Value = userId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "SYNC_STATE_MISSING", "同步配置尚未初始化。");
    }

    public async Task<DataSyncResult> ReplaceRdStylesAsync(
        IReadOnlyList<RdStyleSourceItem> rows,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        await using (var deactivate = new SqlCommand(
            "UPDATE dbo.u8_rd_style_cache SET is_active=0, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME());",
            connection, transaction))
            await deactivate.ExecuteNonQueryAsync(cancellationToken);

        const string upsertSql = """
            UPDATE dbo.u8_rd_style_cache
            SET rd_style_name=@name, rd_grade=@grade, direction=@direction,
                is_terminal=@is_terminal, opposite_code=@opposite_code,
                is_retail=@is_retail, source_row_version=@source_row_version,
                is_active=1, last_seen_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE rd_style_code=@code;
            IF @@ROWCOUNT=0
                INSERT INTO dbo.u8_rd_style_cache
                    (rd_style_code, rd_style_name, rd_grade, direction, is_terminal,
                     opposite_code, is_retail, source_row_version, is_active, last_seen_at)
                VALUES
                    (@code, @name, @grade, @direction, @is_terminal,
                     @opposite_code, @is_retail, @source_row_version, 1,
                     DATEADD(HOUR,8,SYSUTCDATETIME()));
            """;
        foreach (var row in rows)
        {
            await using var command = new SqlCommand(upsertSql, connection, transaction);
            command.Parameters.Add("@code", SqlDbType.NVarChar, 10).Value = row.Code;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 24).Value = row.Name;
            command.Parameters.Add("@grade", SqlDbType.TinyInt).Value = row.Grade;
            command.Parameters.Add("@direction", SqlDbType.VarChar, 10).Value = row.Direction;
            command.Parameters.Add("@is_terminal", SqlDbType.Bit).Value = row.IsTerminal;
            command.Parameters.Add("@opposite_code", SqlDbType.NVarChar, 80).Value =
                string.IsNullOrWhiteSpace(row.OppositeCode) ? DBNull.Value : row.OppositeCode;
            command.Parameters.Add("@is_retail", SqlDbType.Bit).Value = row.IsRetail;
            command.Parameters.Add("@source_row_version", SqlDbType.Binary, 8).Value =
                DecodeSourceRowVersion(row.SourceRowVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var completedAt = BusinessClock.Now;
        const string completeSql = """
            UPDATE dbo.reference_data_sync_state
            SET status='SUCCESS', item_count=@item_count,
                last_completed_at=@completed_at, last_success_at=@completed_at,
                synced_by=@synced_by, error_message=NULL
            WHERE sync_key=@sync_key;
            """;
        await using (var complete = new SqlCommand(completeSql, connection, transaction))
        {
            complete.Parameters.Add("@item_count", SqlDbType.Int).Value = rows.Count;
            complete.Parameters.Add("@completed_at", SqlDbType.DateTime2).Value = completedAt.DateTime;
            complete.Parameters.Add("@synced_by", SqlDbType.NVarChar, 100).Value = userId;
            complete.Parameters.Add("@sync_key", SqlDbType.VarChar, 50).Value = RdStyleSyncKey;
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DataSyncResult(RdStyleSyncKey, "SUCCESS", rows.Count, completedAt);
    }

    public async Task<DataSyncResult> ReplaceWarehousePositionsAsync(
        IReadOnlyList<WarehousePositionSourceItem> rows,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        await using (var deactivate = new SqlCommand("""
            UPDATE dbo.u8_warehouse_cache
            SET is_active=0, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME());
            UPDATE dbo.u8_position_cache
            SET is_active=0, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME());
            """, connection, transaction))
            await deactivate.ExecuteNonQueryAsync(cancellationToken);

        const string warehouseSql = """
            UPDATE dbo.u8_warehouse_cache
            SET warehouse_name=@name, is_active=1,
                last_seen_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE warehouse_code=@code;
            IF @@ROWCOUNT=0
                INSERT INTO dbo.u8_warehouse_cache
                    (warehouse_code, warehouse_name, is_active, last_seen_at)
                VALUES (@code, @name, 1, DATEADD(HOUR,8,SYSUTCDATETIME()));
            """;
        foreach (var warehouse in rows
                     .GroupBy(row => row.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            await using var command = new SqlCommand(warehouseSql, connection, transaction);
            command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = warehouse.WarehouseCode;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = warehouse.WarehouseName;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string positionSql = """
            UPDATE dbo.u8_position_cache
            SET warehouse_code=@warehouse_code, position_name=@name, is_active=1,
                last_seen_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE position_code=@code;
            IF @@ROWCOUNT=0
                INSERT INTO dbo.u8_position_cache
                    (position_code, warehouse_code, position_name, is_active, last_seen_at)
                VALUES (@code, @warehouse_code, @name, 1, DATEADD(HOUR,8,SYSUTCDATETIME()));
            """;
        foreach (var row in rows.Where(row => row.PositionCode is not null))
        {
            await using var command = new SqlCommand(positionSql, connection, transaction);
            command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = row.PositionCode!;
            command.Parameters.Add("@warehouse_code", SqlDbType.NVarChar, 100).Value = row.WarehouseCode;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = row.PositionName!;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var completedAt = BusinessClock.Now;
        await CompleteSyncAsync(connection, transaction, WarehousePositionSyncKey, rows.Count,
            userId, completedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DataSyncResult(WarehousePositionSyncKey, "SUCCESS", rows.Count, completedAt);
    }

    public async Task<DataSyncResult> ReplaceDepartmentsAsync(
        IReadOnlyList<DepartmentSourceItem> rows,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        await using (var deactivate = new SqlCommand(
            "UPDATE dbo.u8_department_cache SET is_active=0, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME());",
            connection, transaction))
            await deactivate.ExecuteNonQueryAsync(cancellationToken);

        const string upsertSql = """
            UPDATE dbo.u8_department_cache
            SET department_name=@name, is_active=1,
                last_seen_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE department_code=@code;
            IF @@ROWCOUNT=0
                INSERT INTO dbo.u8_department_cache
                    (department_code, department_name, is_active, last_seen_at)
                VALUES (@code, @name, 1, DATEADD(HOUR,8,SYSUTCDATETIME()));
            """;
        foreach (var row in rows)
        {
            await using var command = new SqlCommand(upsertSql, connection, transaction);
            command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = row.Code;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = row.Name;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var completedAt = BusinessClock.Now;
        await CompleteSyncAsync(connection, transaction, DepartmentSyncKey, rows.Count,
            userId, completedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DataSyncResult(DepartmentSyncKey, "SUCCESS", rows.Count, completedAt);
    }

    public async Task FailSyncAsync(
        string syncKey,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.reference_data_sync_state
            SET status='FAILED', last_completed_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                synced_by=@synced_by, error_message=N'同步失败'
            WHERE sync_key=@sync_key;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@synced_by", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@sync_key", SqlDbType.VarChar, 50).Value = syncKey;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteSyncAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string syncKey,
        int itemCount,
        string userId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.reference_data_sync_state
            SET status='SUCCESS', item_count=@item_count,
                last_completed_at=@completed_at, last_success_at=@completed_at,
                synced_by=@synced_by, error_message=NULL
            WHERE sync_key=@sync_key;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@item_count", SqlDbType.Int).Value = itemCount;
        command.Parameters.Add("@completed_at", SqlDbType.DateTime2).Value = completedAt.DateTime;
        command.Parameters.Add("@synced_by", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@sync_key", SqlDbType.VarChar, 50).Value = syncKey;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "SYNC_STATE_MISSING", "同步配置尚未初始化。");
    }

    private static object DecodeSourceRowVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DBNull.Value;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 8 ? bytes : DBNull.Value;
        }
        catch (FormatException)
        {
            return DBNull.Value;
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ToBeijingOffset(reader.GetDateTime(ordinal));

    private static DateTimeOffset ToBeijingOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), BusinessClock.BeijingOffset);
}

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class LabelSystemRepository(SqlConnectionFactory connections)
{
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        command.CommandTimeout = 5;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<FunctionItem>> ListFunctionsAsync(
        bool enabledOnly,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT function_code, function_name, function_kind, parent_function_code,
                   group_code, group_name, group_sort, function_sort, icon_code,
                   is_enabled, row_version
            FROM dbo.system_function
            WHERE @enabled_only = 0 OR is_enabled = 1
            ORDER BY group_sort, function_sort, function_code;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@enabled_only", SqlDbType.Bit).Value = enabledOnly;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FunctionItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FunctionItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9),
                RowVersions.Encode((byte[])reader[10])));
        }
        return result;
    }

    public async Task<FunctionItem> UpdateFunctionAsync(
        string code,
        UpdateFunctionRequest request,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.system_function
            SET is_enabled = @is_enabled,
                updated_by = @updated_by,
                updated_at = DATEADD(HOUR, 8, SYSUTCDATETIME())
            OUTPUT inserted.function_code, inserted.function_name, inserted.function_kind,
                   inserted.parent_function_code, inserted.group_code, inserted.group_name,
                   inserted.group_sort, inserted.function_sort, inserted.icon_code,
                   inserted.is_enabled, inserted.row_version
            WHERE function_code = @code AND row_version = @row_version;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = request.IsEnabled;
        command.Parameters.Add("@updated_by", SqlDbType.NVarChar, 100).Value = updatedBy;
        command.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = code;
        command.Parameters.Add("@row_version", SqlDbType.Timestamp).Value = RowVersions.Decode(request.ExpectedRowVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new FunctionItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9),
                RowVersions.Encode((byte[])reader[10]));
        }

        await reader.DisposeAsync();
        await using var existsCommand = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.system_function WHERE function_code=@code;", connection);
        existsCommand.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = code;
        var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) > 0;
        throw exists
            ? new ApiException(StatusCodes.Status409Conflict, "DATA_CHANGED", "数据已变化，请刷新后重试。")
            : new ApiException(StatusCodes.Status404NotFound, "FUNCTION_NOT_FOUND", "功能不存在。");
    }

    public async Task<IReadOnlyList<SettingItem>> ListSettingsAsync(
        string? documentType,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT setting_scope, document_type, setting_key, setting_name,
                   value_json, is_enabled, setting_version, row_version
            FROM dbo.system_setting
            WHERE setting_scope = 'GLOBAL'
               OR (setting_scope = 'DOCUMENT' AND document_type = @document_type)
            ORDER BY setting_scope, setting_key;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@document_type", SqlDbType.VarChar, 50).Value =
            string.IsNullOrWhiteSpace(documentType) ? DBNull.Value : documentType;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SettingItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(4));
            result.Add(new SettingItem(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), document.RootElement.Clone(),
                reader.GetBoolean(5), reader.GetInt32(6), RowVersions.Encode((byte[])reader[7])));
        }
        return result;
    }

    public async Task<bool> GetBooleanSettingAsync(
        string key,
        string? documentType,
        bool fallback,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) value_json
            FROM dbo.system_setting
            WHERE is_enabled = 1
              AND setting_key = @key
              AND
              (
                  (setting_scope = 'DOCUMENT' AND document_type = @document_type)
                  OR (setting_scope = 'GLOBAL' AND document_type IS NULL)
              )
            ORDER BY CASE WHEN setting_scope = 'DOCUMENT' THEN 0 ELSE 1 END;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@key", SqlDbType.VarChar, 100).Value = key;
        command.Parameters.Add("@document_type", SqlDbType.VarChar, 50).Value =
            string.IsNullOrWhiteSpace(documentType) ? DBNull.Value : documentType;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json) return fallback;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? document.RootElement.GetBoolean()
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetRequiredDocumentFieldsAsync(
        string documentType,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) value_json
            FROM dbo.system_setting
            WHERE setting_scope = 'DOCUMENT'
              AND document_type = @document_type
              AND setting_key = 'field_input_rules'
              AND is_enabled = 1;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@document_type", SqlDbType.VarChar, 50).Value = documentType;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json) return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object ||
                    !property.Value.TryGetProperty("required", out var required) ||
                    required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                result[property.Name] = required.GetBoolean();
            }
            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<SettingItem> UpsertSettingAsync(
        string scope,
        string key,
        UpdateSettingRequest request,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        scope = scope.Trim().ToUpperInvariant();
        key = key.Trim();
        if (scope is not ("GLOBAL" or "DOCUMENT"))
            throw new ApiException(StatusCodes.Status400BadRequest, "SETTING_SCOPE_INVALID", "设置范围无效。");
        if (scope == "DOCUMENT" && string.IsNullOrWhiteSpace(request.DocumentType))
            throw new ApiException(StatusCodes.Status400BadRequest, "DOCUMENT_TYPE_REQUIRED", "请选择单据类型。");
        if (scope == "GLOBAL" && !string.IsNullOrWhiteSpace(request.DocumentType))
            throw new ApiException(StatusCodes.Status400BadRequest, "DOCUMENT_TYPE_NOT_ALLOWED", "全局设置不能指定单据类型。");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || string.IsNullOrWhiteSpace(request.Name))
            throw new ApiException(StatusCodes.Status400BadRequest, "SETTING_INVALID", "设置内容无效。");

        var documentType = scope == "DOCUMENT" ? request.DocumentType!.Trim() : null;
        var valueJson = request.Value.GetRawText();

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        const string findSql = """
            SELECT id, row_version
            FROM dbo.system_setting WITH (UPDLOCK, HOLDLOCK)
            WHERE setting_scope = @scope
              AND ((@document_type IS NULL AND document_type IS NULL) OR document_type = @document_type)
              AND setting_key = @key;
            """;
        long? id = null;
        byte[]? currentVersion = null;
        await using (var find = new SqlCommand(findSql, connection, transaction))
        {
            AddSettingKeyParameters(find, scope, documentType, key);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetInt64(0);
                currentVersion = (byte[])reader[1];
            }
        }

        if (id.HasValue)
        {
            if (string.IsNullOrWhiteSpace(request.ExpectedRowVersion) ||
                !currentVersion!.SequenceEqual(RowVersions.Decode(request.ExpectedRowVersion)))
                throw new ApiException(StatusCodes.Status409Conflict, "DATA_CHANGED", "数据已变化，请刷新后重试。");

            const string updateSql = """
                UPDATE dbo.system_setting
                SET setting_name=@name, value_json=@value_json, is_enabled=@is_enabled,
                    setting_version=setting_version+1, updated_by=@updated_by,
                    updated_at=DATEADD(HOUR, 8, SYSUTCDATETIME())
                WHERE id=@id;
                """;
            await using var update = new SqlCommand(updateSql, connection, transaction);
            update.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = request.Name.Trim();
            update.Parameters.Add("@value_json", SqlDbType.NVarChar, -1).Value = valueJson;
            update.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = request.IsEnabled;
            update.Parameters.Add("@updated_by", SqlDbType.NVarChar, 100).Value = updatedBy;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = id.Value;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.ExpectedRowVersion))
                throw new ApiException(StatusCodes.Status409Conflict, "DATA_CHANGED", "设置不存在，请刷新后重试。");

            const string insertSql = """
                INSERT INTO dbo.system_setting
                    (setting_scope, document_type, setting_key, setting_name,
                     value_json, is_enabled, setting_version, updated_by)
                VALUES
                    (@scope, @document_type, @key, @name,
                     @value_json, @is_enabled, 1, @updated_by);
                """;
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            AddSettingKeyParameters(insert, scope, documentType, key);
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = request.Name.Trim();
            insert.Parameters.Add("@value_json", SqlDbType.NVarChar, -1).Value = valueJson;
            insert.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = request.IsEnabled;
            insert.Parameters.Add("@updated_by", SqlDbType.NVarChar, 100).Value = updatedBy;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        const string readSql = """
            SELECT setting_scope, document_type, setting_key, setting_name,
                   value_json, is_enabled, setting_version, row_version
            FROM dbo.system_setting
            WHERE setting_scope = @scope
              AND ((@document_type IS NULL AND document_type IS NULL) OR document_type = @document_type)
              AND setting_key = @key;
            """;
        SettingItem result;
        await using (var read = new SqlCommand(readSql, connection, transaction))
        {
            AddSettingKeyParameters(read, scope, documentType, key);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            using var document = JsonDocument.Parse(reader.GetString(4));
            result = new SettingItem(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), document.RootElement.Clone(),
                reader.GetBoolean(5), reader.GetInt32(6), RowVersions.Encode((byte[])reader[7]));
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<InventoryLookupResult> LookupInventoryObjectAsync(
        string code,
        CancellationToken cancellationToken)
    {
        code = code.Trim();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100)
            throw new ApiException(StatusCodes.Status400BadRequest, "BARCODE_INVALID", "条码无效。");

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        var product = await ReadProductLabelAsync(connection, null, code, cancellationToken);
        var pallet = await ReadPalletAsync(connection, null, code, cancellationToken);
        if (product is not null && pallet is not null)
            throw new ApiException(StatusCodes.Status409Conflict, "BARCODE_DUPLICATED", "条码数据异常。");
        if (product is not null)
            return new InventoryLookupResult([new InventoryObjectLine(
                product.Code, "PRODUCT_LABEL", product.MaterialCode,
                string.IsNullOrWhiteSpace(product.MaterialName) ? product.MaterialCode : product.MaterialName,
                product.BatchNo, product.UnitCode, product.UnitName,
                product.WarehouseCode, product.WarehouseName, product.LocationCode, product.LocationName, product.CurrentQty,
                product.PalletCode, product.Status, RowVersions.Encode(product.RowVersion))], null);
        if (pallet is null)
            throw new ApiException(StatusCodes.Status404NotFound, "LABEL_NOT_FOUND", "未找到标签。");

        const string contentsSql = """
            SELECT pl.label_code, f.material_code, f.batch_no, f.unit_code,
                   f.to_warehouse_code, f.to_location_code, pl.current_qty,
                   p.pallet_code, pl.status, pl.row_version,
                   JSON_VALUE(f.inventory_attributes_json, '$.materialName'),
                   JSON_VALUE(f.inventory_attributes_json, '$.unitName'),
                   w.warehouse_name, pos.position_name,
                   pl.traceback_latest_flow_id, pl.mirror_source
            FROM dbo.pallet_label_relation AS rel
            INNER JOIN dbo.product_label AS pl ON pl.id = rel.label_id
            INNER JOIN dbo.pallet AS p ON p.id = rel.pallet_id
            LEFT JOIN dbo.inventory_flow AS f ON f.id = pl.last_flow_id
            LEFT JOIN dbo.u8_warehouse_cache AS w
              ON w.warehouse_code = f.to_warehouse_code AND w.is_active = 1
            LEFT JOIN dbo.u8_position_cache AS pos
              ON pos.position_code = f.to_location_code
             AND pos.warehouse_code = f.to_warehouse_code AND pos.is_active = 1
            WHERE rel.pallet_id = @pallet_id AND rel.unloaded_at IS NULL
            ORDER BY pl.label_code;
            """;
        await using var command = new SqlCommand(contentsSql, connection);
        command.Parameters.Add("@pallet_id", SqlDbType.BigInt).Value = pallet.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var lines = new List<InventoryObjectLine>();
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new InventoryObjectLine(
                reader.GetString(0), "PRODUCT_LABEL", DbString(reader, 1),
                reader.IsDBNull(10) ? DbString(reader, 1) : reader.GetString(10),
                DbString(reader, 2), DbString(reader, 3), reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                DbString(reader, 4), reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                DbString(reader, 5), reader.IsDBNull(13) ? string.Empty : reader.GetString(13), reader.GetDecimal(6),
                reader.GetString(7), reader.GetString(8), RowVersions.Encode((byte[])reader[9])));
        }
        return new InventoryLookupResult(lines, pallet.Line);
    }

    internal static async Task<ProductLabelSnapshot?> ReadProductLabelAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string code,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        var hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT pl.id, pl.label_code, pl.current_qty, pl.status, pl.locked_operation_id,
                   pl.row_version, f.material_code, f.batch_no, f.unit_code,
                   COALESCE(NULLIF(f.to_warehouse_code,N''),f.from_warehouse_code),
                   CASE WHEN NULLIF(f.to_warehouse_code,N'') IS NULL
                        THEN f.from_location_code ELSE f.to_location_code END, p.id, p.pallet_code,
                   JSON_VALUE(f.inventory_attributes_json, '$.materialName'),
                   JSON_VALUE(f.inventory_attributes_json, '$.unitName'),
                   w.warehouse_name, pos.position_name
            FROM dbo.product_label AS pl{hint}
            LEFT JOIN dbo.inventory_flow AS f ON f.id = pl.last_flow_id
            LEFT JOIN dbo.u8_warehouse_cache AS w
              ON w.warehouse_code = COALESCE(NULLIF(f.to_warehouse_code,N''),f.from_warehouse_code)
             AND w.is_active = 1
            LEFT JOIN dbo.u8_position_cache AS pos
              ON pos.position_code = CASE WHEN NULLIF(f.to_warehouse_code,N'') IS NULL
                                          THEN f.from_location_code ELSE f.to_location_code END
             AND pos.warehouse_code = COALESCE(NULLIF(f.to_warehouse_code,N''),f.from_warehouse_code)
             AND pos.is_active = 1
            OUTER APPLY
            (
                SELECT TOP (1) pallet.id, pallet.pallet_code
                FROM dbo.pallet_label_relation AS rel
                INNER JOIN dbo.pallet AS pallet ON pallet.id = rel.pallet_id
                WHERE rel.label_id = pl.id AND rel.unloaded_at IS NULL
            ) AS p
            WHERE pl.label_code = @code;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = code;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ProductLabelSnapshot(
            reader.GetInt64(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), (byte[])reader[5], DbString(reader, 6),
            DbString(reader, 7), DbString(reader, 8), DbString(reader, 9), DbString(reader, 10),
            reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(17) ? null : reader.GetInt64(17), reader.GetString(18));
    }

    internal static async Task<PalletSnapshot?> ReadPalletAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string code,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        var hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT p.id, p.pallet_code, p.material_code, p.batch_no, p.unit_code,
                   p.current_warehouse_code, p.current_location_code, p.current_qty,
                   p.status, p.locked_operation_id, p.row_version,
                   w.warehouse_name, pos.position_name
            FROM dbo.pallet AS p{hint}
            LEFT JOIN dbo.u8_warehouse_cache AS w
              ON w.warehouse_code = p.current_warehouse_code AND w.is_active = 1
            LEFT JOIN dbo.u8_position_cache AS pos
              ON pos.position_code = p.current_location_code
             AND pos.warehouse_code = p.current_warehouse_code AND pos.is_active = 1
            WHERE p.pallet_code = @code;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = code;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var id = reader.GetInt64(0);
        var line = new InventoryObjectLine(
            reader.GetString(1), "PALLET", reader.GetString(2), reader.GetString(2),
            DbString(reader, 3), reader.GetString(4), string.Empty,
            DbString(reader, 5), reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            DbString(reader, 6), reader.IsDBNull(12) ? string.Empty : reader.GetString(12), reader.GetDecimal(7),
            reader.GetString(1), reader.GetString(8), RowVersions.Encode((byte[])reader[10]));
        return new PalletSnapshot(id, line, reader.IsDBNull(9) ? null : reader.GetInt64(9));
    }

    private static void AddSettingKeyParameters(
        SqlCommand command,
        string scope,
        string? documentType,
        string key)
    {
        command.Parameters.Add("@scope", SqlDbType.VarChar, 20).Value = scope;
        command.Parameters.Add("@document_type", SqlDbType.VarChar, 50).Value =
            documentType is null ? DBNull.Value : documentType;
        command.Parameters.Add("@key", SqlDbType.VarChar, 100).Value = key;
    }

    private static string DbString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
}

public sealed record ProductLabelSnapshot(
    long Id,
    string Code,
    decimal CurrentQty,
    string Status,
    long? LockedOperationId,
    byte[] RowVersion,
    string MaterialCode,
    string BatchNo,
    string UnitCode,
    string WarehouseCode,
    string LocationCode,
    string MaterialName,
    string UnitName,
    string WarehouseName,
    string LocationName,
    long? PalletId,
    string? PalletCode,
    long? TraceBackLatestFlowId,
    string MirrorSource);

public sealed record PalletSnapshot(long Id, InventoryObjectLine Line, long? LockedOperationId);

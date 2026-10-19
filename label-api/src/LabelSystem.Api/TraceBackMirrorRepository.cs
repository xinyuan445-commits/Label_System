using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed class TraceBackMirrorRepository(
    SqlConnectionFactory connections,
    IOptionsMonitor<LabelSystemOptions> options)
{
    public async Task<InventoryObjectLine> UpsertAsync(
        TraceBackLabelSnapshot snapshot,
        SessionUser user,
        bool rejectChangedSnapshot,
        CancellationToken cancellationToken,
        bool preserveTerminalState = false)
    {
        TraceBackSnapshotRules.RequireUsable(snapshot, allowUsed: true);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var existing = await ReadExistingAsync(connection, transaction, snapshot.Code, cancellationToken);
        var targetStatus = snapshot.Balance == 0 ? "USED" : "ACTIVE";
        if (existing is not null && existing.Status is "FROZEN" or "VOID")
            targetStatus = existing.Status;
        if (preserveTerminalState && existing?.Status is "SPLIT" or "MERGED")
            targetStatus = existing.Status;

        if (existing is not null &&
            existing.LatestTraceBackFlowId == snapshot.LatestFlowId &&
            string.Equals(existing.IdentityHash, snapshot.IdentityHash, StringComparison.OrdinalIgnoreCase) &&
            existing.CurrentQty == snapshot.Balance &&
            existing.SyncStatus == "SYNCED" &&
            (existing.Status == targetStatus || existing.Status is "FROZEN" or "VOID"))
        {
            await transaction.CommitAsync(cancellationToken);
            return ToLine(snapshot, existing.RowVersion, existing.Status, existing.PalletCode);
        }

        if (rejectChangedSnapshot && existing?.LatestTraceBackFlowId is not null)
            throw new ApiException(StatusCodes.Status409Conflict,
                "TRACEBACK_SNAPSHOT_CHANGED", "标签数据已变化，请重新扫描。");

        if (existing?.LockedOperationId is not null)
            throw new ApiException(StatusCodes.Status409Conflict,
                "LABEL_LOCKED", "标签正在处理中。");
        if (existing is not null && existing.LastFlowId.HasValue)
            EnsureLocalIdentity(existing, snapshot);

        var keyHash = Hashing.Sha256(
            $"{snapshot.Code}\u001f{snapshot.LatestFlowId}\u001f{snapshot.Balance}\u001f" +
            $"{snapshot.IdentityHash}\u001f{snapshot.SourceBusinessType}\u001f" +
            $"{snapshot.PdaDocumentNo}\u001f{snapshot.ErpDocumentNo}");
        var clientRequestId = $"traceback-mirror-{keyHash}";
        var operationNo = $"TBM-{keyHash[..32]}";
        var currentOptions = options.CurrentValue;
        var accountId = currentOptions.ErpAccountId;
        var operationId = await InsertOperationAsync(
            connection, transaction, operationNo, clientRequestId, currentOptions,
            snapshot, user, cancellationToken);

        long labelId;
        decimal qtyBefore;
        int sequence;
        string? fromWarehouse;
        string? fromLocation;
        if (existing is null)
        {
            const string insertLabel = """
                INSERT INTO dbo.product_label
                (label_code,current_qty,status,mirror_source,traceback_account_id,
                 traceback_latest_flow_id,traceback_balance,traceback_identity_hash,
                 traceback_sync_status,traceback_synced_at)
                OUTPUT INSERTED.id
                VALUES(@code,0,'USED','TRACEBACK',@account,@traceback_flow_id,@balance,
                       @identity_hash,'SYNCED',DATEADD(HOUR,8,SYSUTCDATETIME()));
                """;
            await using var command = new SqlCommand(insertLabel, connection, transaction);
            command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = snapshot.Code;
            command.Parameters.Add("@account", SqlDbType.NVarChar, 20).Value = accountId;
            command.Parameters.Add("@traceback_flow_id", SqlDbType.BigInt).Value = snapshot.LatestFlowId!.Value;
            AddDecimal(command, "@balance", snapshot.Balance!.Value);
            command.Parameters.Add("@identity_hash", SqlDbType.Char, 64).Value = snapshot.IdentityHash!;
            labelId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            qtyBefore = 0;
            sequence = 1;
            fromWarehouse = null;
            fromLocation = null;
        }
        else
        {
            labelId = existing.Id;
            qtyBefore = existing.CurrentQty;
            sequence = existing.LastSequence + 1;
            fromWarehouse = existing.WarehouseCode;
            fromLocation = existing.LocationCode;
        }

        var lineId = await InsertOperationLineAsync(
            connection, transaction, operationId, labelId, snapshot, qtyBefore, cancellationToken);
        var flowId = await InsertFlowAsync(
            connection, transaction, operationId, lineId, labelId, sequence,
            qtyBefore, snapshot, fromWarehouse, fromLocation, cancellationToken);

        const string updateLabel = """
            UPDATE dbo.product_label
            SET current_qty=@qty,status=@status,last_flow_id=@flow_id,
                mirror_source='TRACEBACK',traceback_account_id=@account,
                traceback_latest_flow_id=@traceback_flow_id,traceback_balance=@qty,
                traceback_identity_hash=@identity_hash,traceback_sync_status='SYNCED',
                traceback_synced_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id;
            """;
        await using (var command = new SqlCommand(updateLabel, connection, transaction))
        {
            command.Parameters.Add("@qty", SqlDbType.Decimal).Value = snapshot.Balance!.Value;
            command.Parameters["@qty"].Precision = 18;
            command.Parameters["@qty"].Scale = 6;
            command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = targetStatus;
            command.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = flowId;
            command.Parameters.Add("@account", SqlDbType.NVarChar, 20).Value = accountId;
            command.Parameters.Add("@traceback_flow_id", SqlDbType.BigInt).Value = snapshot.LatestFlowId!.Value;
            command.Parameters.Add("@identity_hash", SqlDbType.Char, 64).Value = snapshot.IdentityHash!;
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = labelId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        byte[] rowVersion;
        const string readVersion = "SELECT row_version FROM dbo.product_label WHERE id=@id;";
        await using (var command = new SqlCommand(readVersion, connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = labelId;
            rowVersion = (byte[])(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        await transaction.CommitAsync(cancellationToken);
        return ToLine(snapshot, rowVersion, targetStatus, existing?.PalletCode);
    }

    private static async Task<ExistingMirror?> ReadExistingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string code,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pl.id,pl.current_qty,pl.status,pl.last_flow_id,pl.locked_operation_id,
                   pl.row_version,pl.traceback_latest_flow_id,pl.traceback_identity_hash,
                   f.material_code,f.batch_no,f.unit_code,
                   COALESCE(NULLIF(f.to_warehouse_code,N''),f.from_warehouse_code),
                   CASE WHEN NULLIF(f.to_warehouse_code,N'') IS NULL
                        THEN f.from_location_code ELSE f.to_location_code END,
                   ISNULL(f.object_sequence_no,0),p.pallet_code,pl.traceback_sync_status
            FROM dbo.product_label pl WITH (UPDLOCK,HOLDLOCK)
            LEFT JOIN dbo.inventory_flow f ON f.id=pl.last_flow_id
            OUTER APPLY
            (
                SELECT TOP (1) pallet.pallet_code
                FROM dbo.pallet_label_relation rel
                JOIN dbo.pallet pallet ON pallet.id=rel.pallet_id
                WHERE rel.label_id=pl.id AND rel.unloaded_at IS NULL
            ) p
            WHERE pl.label_code=@code;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = code;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ExistingMirror(
            reader.GetInt64(0), reader.GetDecimal(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), (byte[])reader[5],
            reader.IsDBNull(6) ? null : reader.GetInt64(6), DbString(reader, 7),
            DbString(reader, 8), DbString(reader, 9), DbString(reader, 10),
            DbString(reader, 11), DbString(reader, 12), reader.GetInt32(13), DbString(reader, 14),
            reader.GetString(15));
    }

    private static void EnsureLocalIdentity(ExistingMirror existing, TraceBackLabelSnapshot snapshot)
    {
        if (!Same(existing.MaterialCode, snapshot.MaterialCode) ||
            !Same(existing.BatchNo, snapshot.BatchNo) ||
            !Same(existing.UnitCode, snapshot.UnitCode))
            throw new ApiException(StatusCodes.Status409Conflict,
                "TRACEBACK_LOCAL_IDENTITY_CONFLICT", "标签数据待核对。");
    }

    private async Task<long> InsertOperationAsync(
        SqlConnection connection, SqlTransaction transaction,
        string operationNo, string clientRequestId, LabelSystemOptions currentOptions,
        TraceBackLabelSnapshot snapshot,
        SessionUser user, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.business_operation
            (operation_no,operation_type,status,source_document_type,source_document_id,
             source_document_no,erp_environment,erp_account_id,erp_year,
             erp_database_name_snapshot,operator_id,operator_name_snapshot,
             client_request_id,request_payload_hash,erp_status,approval_status,
             archive_status,completed_at)
            OUTPUT INSERTED.id
            VALUES(@operation_no,'TRACEBACK_MIRROR','SUCCESS',@source_type,@source_id,
                   @source_no,@environment,@account,@year,@database,@operator_id,@operator_name,@client_request_id,
                   @payload_hash,'NOT_REQUIRED','NOT_REQUIRED','COMPLETE',
                   DATEADD(HOUR,8,SYSUTCDATETIME()));
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_no", SqlDbType.NVarChar, 50).Value = operationNo;
        command.Parameters.Add("@source_type", SqlDbType.VarChar, 50).Value =
            DbValue(snapshot.SourceBusinessType);
        command.Parameters.Add("@source_id", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.OrderUuid);
        command.Parameters.Add("@source_no", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.PdaDocumentNo);
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = currentOptions.Environment;
        command.Parameters.Add("@account", SqlDbType.NVarChar, 20).Value = currentOptions.ErpAccountId;
        command.Parameters.Add("@year", SqlDbType.SmallInt).Value = currentOptions.ErpYear;
        command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = currentOptions.ErpDatabaseName;
        command.Parameters.Add("@operator_id", SqlDbType.NVarChar, 100).Value = user.Id;
        command.Parameters.Add("@operator_name", SqlDbType.NVarChar, 100).Value = user.Name;
        command.Parameters.Add("@client_request_id", SqlDbType.VarChar, 100).Value = clientRequestId;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value = Hashing.Sha256(
            $"{snapshot.Code}\u001f{snapshot.LatestFlowId}\u001f{snapshot.Balance}\u001f" +
            $"{snapshot.IdentityHash}\u001f{snapshot.SourceBusinessType}\u001f" +
            $"{snapshot.PdaDocumentNo}\u001f{snapshot.ErpDocumentNo}");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertOperationLineAsync(
        SqlConnection connection, SqlTransaction transaction,
        long operationId, long labelId, TraceBackLabelSnapshot snapshot,
        decimal qtyBefore, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.business_operation_line
            (operation_id,line_no,line_status,label_id,selection_source,inventory_qty_snapshot,
             material_code,batch_no,serial_no,inventory_attributes_json,
             inventory_identity_hash,unit_code,requested_qty,
             from_warehouse_code,from_location_code,to_warehouse_code,to_location_code)
            OUTPUT INSERTED.id
            VALUES(@operation_id,1,'SUCCESS',@label_id,'LABEL_SCAN',@before,
                   @material,@batch,@serial,@attributes,@identity,@unit,@requested,
                   @warehouse,@location,@warehouse,@location);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
        command.Parameters.Add("@label_id", SqlDbType.BigInt).Value = labelId;
        AddDecimal(command, "@before", qtyBefore);
        command.Parameters.Add("@material", SqlDbType.NVarChar, 100).Value = snapshot.MaterialCode!;
        command.Parameters.Add("@batch", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.BatchNo);
        command.Parameters.Add("@serial", SqlDbType.NVarChar, 200).Value = DbValue(snapshot.SerialNo);
        command.Parameters.Add("@attributes", SqlDbType.NVarChar, -1).Value = Attributes(snapshot);
        command.Parameters.Add("@identity", SqlDbType.Char, 64).Value = snapshot.IdentityHash!;
        command.Parameters.Add("@unit", SqlDbType.NVarChar, 50).Value = snapshot.UnitCode!;
        AddDecimal(command, "@requested", snapshot.Balance!.Value);
        command.Parameters.Add("@warehouse", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.WarehouseCode);
        command.Parameters.Add("@location", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.LocationCode);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertFlowAsync(
        SqlConnection connection, SqlTransaction transaction,
        long operationId, long lineId, long labelId, int sequence,
        decimal qtyBefore, TraceBackLabelSnapshot snapshot,
        string? fromWarehouse, string? fromLocation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.inventory_flow
            (operation_id,operation_line_id,object_type,label_id,object_sequence_no,
             flow_role,operation_type,unit_code,qty_before,change_qty,qty_after,
             material_code,batch_no,serial_no,inventory_attributes_json,
             inventory_identity_hash,from_warehouse_code,from_location_code,
             to_warehouse_code,to_location_code,remark)
            OUTPUT INSERTED.id
            VALUES(@operation_id,@line_id,'PRODUCT_LABEL',@label_id,@sequence,
                   'NORMAL','TRACEBACK_MIRROR',@unit,@before,@change,@after,
                   @material,@batch,@serial,@attributes,@identity,@from_wh,@from_pos,
                   @to_wh,@to_pos,N'TraceBack 实时镜像');
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
        command.Parameters.Add("@line_id", SqlDbType.BigInt).Value = lineId;
        command.Parameters.Add("@label_id", SqlDbType.BigInt).Value = labelId;
        command.Parameters.Add("@sequence", SqlDbType.Int).Value = sequence;
        command.Parameters.Add("@unit", SqlDbType.NVarChar, 50).Value = snapshot.UnitCode!;
        AddDecimal(command, "@before", qtyBefore);
        AddDecimal(command, "@change", snapshot.Balance!.Value - qtyBefore);
        AddDecimal(command, "@after", snapshot.Balance.Value);
        command.Parameters.Add("@material", SqlDbType.NVarChar, 100).Value = snapshot.MaterialCode!;
        command.Parameters.Add("@batch", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.BatchNo);
        command.Parameters.Add("@serial", SqlDbType.NVarChar, 200).Value = DbValue(snapshot.SerialNo);
        command.Parameters.Add("@attributes", SqlDbType.NVarChar, -1).Value = Attributes(snapshot);
        command.Parameters.Add("@identity", SqlDbType.Char, 64).Value = snapshot.IdentityHash!;
        command.Parameters.Add("@from_wh", SqlDbType.NVarChar, 100).Value = DbValue(fromWarehouse);
        command.Parameters.Add("@from_pos", SqlDbType.NVarChar, 100).Value = DbValue(fromLocation);
        command.Parameters.Add("@to_wh", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.WarehouseCode);
        command.Parameters.Add("@to_pos", SqlDbType.NVarChar, 100).Value = DbValue(snapshot.LocationCode);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static InventoryObjectLine ToLine(
        TraceBackLabelSnapshot snapshot, byte[] rowVersion, string status, string? palletCode) =>
        new(snapshot.Code, "PRODUCT_LABEL", snapshot.MaterialCode!,
            string.IsNullOrWhiteSpace(snapshot.MaterialName) ? snapshot.MaterialCode! : snapshot.MaterialName,
            snapshot.BatchNo ?? string.Empty, snapshot.UnitCode!, snapshot.UnitName ?? string.Empty,
            snapshot.WarehouseCode ?? string.Empty, snapshot.WarehouseName ?? string.Empty,
            snapshot.LocationCode ?? string.Empty, snapshot.LocationName ?? string.Empty,
            snapshot.Balance!.Value, palletCode, status, RowVersions.Encode(rowVersion));

    private static string Attributes(TraceBackLabelSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        materialName = snapshot.MaterialName,
        unitName = snapshot.UnitName,
        warehouseName = snapshot.WarehouseName,
        locationName = snapshot.LocationName,
        traceBackGoodsUuid = snapshot.GoodsUuid,
        traceBackOrderUuid = snapshot.OrderUuid,
        traceBackLatestFlowId = snapshot.LatestFlowId,
        traceBackSourceBusinessType = snapshot.SourceBusinessType,
        traceBackPdaDocumentNo = snapshot.PdaDocumentNo,
        traceBackErpDocumentNo = snapshot.ErpDocumentNo
    });

    private static object DbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 6;
        parameter.Value = value;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

    private static string DbString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private sealed record ExistingMirror(
        long Id, decimal CurrentQty, string Status, long? LastFlowId,
        long? LockedOperationId, byte[] RowVersion, long? LatestTraceBackFlowId,
        string IdentityHash, string MaterialCode, string BatchNo, string UnitCode,
        string WarehouseCode, string LocationCode, int LastSequence, string PalletCode,
        string SyncStatus);
}

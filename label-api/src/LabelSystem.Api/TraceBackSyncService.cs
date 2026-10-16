using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed record TraceBackSyncEventRequest(
    string EventKey,
    string EventRole,
    string Code,
    decimal Quantity,
    long ExpectedLatestFlowId,
    decimal ExpectedBalance,
    string OrderUuid,
    string GoodsUuid,
    string OrderType,
    string PdaDocumentNo,
    string ErpDocumentNo,
    string? ErpDetailId,
    string OperatorName,
    string? TargetWarehouseCode,
    string? TargetLocationCode);

public sealed record TraceBackSyncEventResult(
    string EventKey, bool AlreadyApplied, long FlowId, decimal Balance,
    TraceBackLabelSnapshot Snapshot);

public sealed record TraceBackSyncTaskDraft(
    string EventKey,
    long OperationId,
    long OperationLineId,
    long LabelId,
    string EventRole,
    string TraceBackCode,
    Guid PdaOrderUuid,
    Guid PdaGoodsUuid,
    string ErpDocumentType,
    string ErpDocumentNo,
    string PayloadJson,
    string PayloadHash);

public sealed record TraceBackSyncTaskRow(
    long Id,
    long OperationId,
    long LabelId,
    string EventKey,
    TraceBackSyncEventRequest Payload);

public static class TraceBackSyncPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<TraceBackSyncTaskDraft> Build(
        MaterialOutboundExecutionOperation operation,
        GatewayDocumentResult result,
        bool redVoucher,
        IReadOnlyDictionary<int, (string? LineId, string? PositionId)> erpRows,
        string operatorName,
        string accountId)
    {
        if (string.IsNullOrWhiteSpace(result.VoucherCode))
            throw new InvalidOperationException("U8 单据号缺失。");
        var products = operation.Lines.Where(line =>
            line.ObjectType == "PRODUCT_LABEL" && line.LabelId.HasValue &&
            line.TraceBackLatestFlowId.HasValue).ToArray();
        if (products.Length == 0) return [];

        var orderUuid = StableGuid($"traceback-order|{accountId}|{operation.Id}|{result.VoucherCode}");
        var orderType = operation.ErpDocumentType switch
        {
            "material-outbound" => "材料出库",
            "transfer" => "调拨单",
            "location-adjustment" => "货位调整单",
            _ => throw new InvalidOperationException("单据类型不支持追溯同步。")
        };
        var tasks = new List<TraceBackSyncTaskDraft>(products.Length);
        foreach (var line in products)
        {
            string role;
            decimal quantity;
            string? targetWarehouse = null;
            string? targetLocation = null;
            if (operation.ErpDocumentType == "material-outbound")
            {
                role = redVoucher ? "MATERIAL_RETURN" : "MATERIAL_ISSUE";
                quantity = redVoucher ? line.RequestedQty : -line.RequestedQty;
                if (redVoucher)
                {
                    targetWarehouse = line.WarehouseCode;
                    targetLocation = line.LocationCode;
                }
            }
            else if (line.RequestedQty == line.InventoryQtySnapshot)
            {
                role = "FULL_MOVE";
                quantity = 0;
                targetWarehouse = line.TargetWarehouseCode ?? line.WarehouseCode;
                targetLocation = line.TargetLocationCode;
            }
            else
            {
                role = "PARTIAL_SOURCE";
                quantity = -line.RequestedQty;
            }
            var eventKey = $"tb:{accountId}:{operation.Id}:{line.Id}:{role}:{result.VoucherCode}";
            var goodsUuid = StableGuid("traceback-goods|" + eventKey);
            erpRows.TryGetValue(line.LineNo, out var erp);
            var payload = new TraceBackSyncEventRequest(
                eventKey, role, line.LabelCode, quantity,
                line.TraceBackLatestFlowId!.Value, line.InventoryQtySnapshot,
                orderUuid.ToString("D"), goodsUuid.ToString("D"), orderType,
                operation.ClientRequestId, result.VoucherCode!, erp.LineId,
                operatorName, targetWarehouse, targetLocation);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            tasks.Add(new TraceBackSyncTaskDraft(
                eventKey, operation.Id, line.Id, line.LabelId!.Value, role, line.LabelCode,
                orderUuid, goodsUuid, operation.ErpDocumentType, result.VoucherCode!, json, hash));
        }
        return tasks;
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var guid = bytes[..16];
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }
}

public sealed class TraceBackSyncService(
    TraceBackSyncRepository repository,
    U8GatewayClient gateway)
{
    public async Task<bool> TrySyncOperationAsync(
        MaterialOutboundExecutionOperation operation,
        string requestId,
        CancellationToken cancellationToken)
    {
        var tasks = await repository.GetPendingAsync(operation.Id, cancellationToken);
        if (tasks.Count == 0)
        {
            if (!await repository.HasAnyAsync(operation.Id, cancellationToken)) return false;
            await repository.FinalizeOperationAsync(operation, requestId, cancellationToken);
            return true;
        }
        foreach (var task in tasks)
        {
            try
            {
                await repository.MarkProcessingAsync(task.Id, cancellationToken);
                var result = await gateway.SyncTraceBackEventAsync(task.Payload, cancellationToken);
                await repository.MarkCompletedAsync(task, result, cancellationToken);
            }
            catch (Exception error)
            {
                var code = error is ApiException api ? api.Code : "TRACEBACK_SYNC_FAILED";
                await repository.MarkFailedAsync(
                    task.Id, operation.Id, code, error.Message, requestId, cancellationToken);
                return false;
            }
        }
        await repository.FinalizeOperationAsync(operation, requestId, cancellationToken);
        return true;
    }
}

public sealed class TraceBackSyncRepository(SqlConnectionFactory connections)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<TraceBackSyncTaskRow>> GetPendingAsync(
        long operationId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,operation_id,label_id,event_key,payload_json
            FROM dbo.traceback_sync_task
            WHERE operation_id=@operation_id AND status<>'COMPLETED'
            ORDER BY id;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TraceBackSyncTaskRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = JsonSerializer.Deserialize<TraceBackSyncEventRequest>(
                reader.GetString(4), JsonOptions)
                ?? throw new InvalidOperationException("追溯同步任务内容无效。");
            result.Add(new TraceBackSyncTaskRow(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), payload));
        }
        return result;
    }

    public async Task<bool> HasAnyAsync(long operationId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.traceback_sync_task WHERE operation_id=@id;", connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = operationId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task MarkProcessingAsync(long taskId, CancellationToken cancellationToken)
    {
        await ExecuteAsync("""
            UPDATE dbo.traceback_sync_task
            SET status='PROCESSING',attempt_count=attempt_count+1,
                last_attempt_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                last_error_code=NULL,last_error_message=NULL,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND status<>'COMPLETED';
            """, taskId, null, null, cancellationToken);
    }

    public async Task MarkCompletedAsync(
        TraceBackSyncTaskRow task, TraceBackSyncEventResult result,
        CancellationToken cancellationToken)
    {
        if (result.Snapshot.Status == "RECONCILIATION_REQUIRED" ||
            result.Snapshot.LatestFlowId != result.FlowId ||
            result.Snapshot.Balance != result.Balance ||
            string.IsNullOrWhiteSpace(result.Snapshot.IdentityHash))
            throw new InvalidOperationException("追溯写入结果待核对。");
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var updateTask = new SqlCommand("""
            UPDATE dbo.traceback_sync_task
            SET status='COMPLETED',completed_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                last_error_code=NULL,last_error_message=NULL,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND status<>'COMPLETED';
            """, connection, transaction))
        {
            updateTask.Parameters.Add("@id", SqlDbType.BigInt).Value = task.Id;
            await updateTask.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var updateLabel = new SqlCommand("""
            UPDATE dbo.product_label
            SET current_qty=@balance,status=CASE WHEN @balance=0 THEN 'USED' ELSE 'ACTIVE' END,
                traceback_latest_flow_id=@flow_id,traceback_balance=@balance,
                traceback_identity_hash=@identity,traceback_sync_status='SYNCED',
                traceback_synced_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@label_id AND mirror_source='TRACEBACK';
            """, connection, transaction))
        {
            updateLabel.Parameters.Add("@balance", SqlDbType.Decimal).ConfigureDecimal(result.Balance);
            updateLabel.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = result.FlowId;
            updateLabel.Parameters.Add("@identity", SqlDbType.Char, 64).Value = result.Snapshot.IdentityHash;
            updateLabel.Parameters.Add("@label_id", SqlDbType.BigInt).Value = task.LabelId;
            if (await updateLabel.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("本地标签镜像更新失败。");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        long taskId, long operationId, string code, string message,
        string requestId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var task = new SqlCommand("""
            UPDATE dbo.traceback_sync_task
            SET status='FAILED',last_error_code=@code,last_error_message=@message,
                next_retry_at=DATEADD(MINUTE,1,DATEADD(HOUR,8,SYSUTCDATETIME())),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND status<>'COMPLETED';
            """, connection, transaction))
        {
            task.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = Limit(code, 100);
            task.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = Limit(message, 2000);
            task.Parameters.Add("@id", SqlDbType.BigInt).Value = taskId;
            await task.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var operation = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status='NEEDS_RECONCILIATION',erp_status='SUCCESS',
                error_code=@code,error_message=@message,
                completed_at=NULL,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND erp_status='SUCCESS';
            """, connection, transaction))
        {
            operation.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = Limit(code, 100);
            operation.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = Limit(message, 2000);
            operation.Parameters.Add("@id", SqlDbType.BigInt).Value = operationId;
            await operation.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertEventAsync(connection, transaction, operationId, "TRACEBACK_SYNC_FAILED",
            "FAILED", requestId, "ERP 已生成，标签同步待恢复。", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinalizeOperationAsync(
        MaterialOutboundExecutionOperation operation,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var pending = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.traceback_sync_task WITH (UPDLOCK,HOLDLOCK)
            WHERE operation_id=@id AND status<>'COMPLETED';
            """, connection, transaction))
        {
            pending.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException("追溯同步任务尚未完成。");
        }
        bool approvalPending;
        await using (var approval = new SqlCommand("""
            SELECT approval_status FROM dbo.business_operation WITH (UPDLOCK,HOLDLOCK)
            WHERE id=@id;
            """, connection, transaction))
        {
            approval.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            approvalPending = !string.Equals(
                Convert.ToString(await approval.ExecuteScalarAsync(cancellationToken)),
                "NOT_REQUIRED", StringComparison.OrdinalIgnoreCase);
        }
        await using (var update = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status=@status,error_code=NULL,error_message=NULL,
                completed_at=CASE WHEN @approval=1 THEN NULL ELSE DATEADD(HOUR,8,SYSUTCDATETIME()) END,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND status='NEEDS_RECONCILIATION' AND erp_status='SUCCESS';
            """, connection, transaction))
        {
            update.Parameters.Add("@status", SqlDbType.VarChar, 30).Value =
                approvalPending ? "PENDING_APPROVAL" : "SUCCESS";
            update.Parameters.Add("@approval", SqlDbType.Bit).Value = approvalPending;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("业务状态已变化。");
        }
        await InsertEventAsync(connection, transaction, operation.Id, "TRACEBACK_SYNC_COMPLETED",
            "SUCCESS", requestId, "追溯标签已同步。", cancellationToken);
        if (!approvalPending)
        {
            foreach (var table in new[] { "product_label", "pallet" })
            {
                await using var release = new SqlCommand($"""
                    UPDATE dbo.{table}
                    SET locked_operation_id=NULL,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                    WHERE locked_operation_id=@root_id;
                    """, connection, transaction);
                release.Parameters.Add("@root_id", SqlDbType.BigInt).Value = operation.RootOperationId;
                await release.ExecuteNonQueryAsync(cancellationToken);
            }
            await CompleteDraftIfTerminalAsync(
                connection, transaction, operation.SubmissionGroupId,
                operation.OperatorId, requestId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteAsync(
        string sql, long id, string? code, string? message,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        if (sql.Contains("@code"))
            command.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = Limit(code, 100);
        if (sql.Contains("@message"))
            command.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = Limit(message, 2000);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteDraftIfTerminalAsync(
        SqlConnection connection, SqlTransaction transaction,
        string groupId, string userId, string requestId, CancellationToken cancellationToken)
    {
        await using var active = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.business_operation
            WHERE submission_group_id=@group_id AND status IN
                ('DRAFT','PENDING','WRITING_ERP','PENDING_APPROVAL','NEEDS_RECONCILIATION');
            """, connection, transaction);
        active.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken)) != 0) return;
        await DocumentDraftLifecycle.SetStatusAsync(
            connection, transaction, groupId, "COMPLETED", userId, requestId, cancellationToken);
    }

    private static async Task InsertEventAsync(
        SqlConnection connection, SqlTransaction transaction, long operationId,
        string eventType, string result, string requestId, string message,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO dbo.business_operation_event
                (operation_id,sequence_no,stage,source,event_type,result,request_id,message)
            SELECT @id,ISNULL(MAX(sequence_no),0)+1,@event_type,'BUSINESS_API',
                   @event_type,@result,@request_id,@message
            FROM dbo.business_operation_event WITH (UPDLOCK,HOLDLOCK)
            WHERE operation_id=@id;
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = operationId;
        command.Parameters.Add("@event_type", SqlDbType.VarChar, 50).Value = eventType;
        command.Parameters.Add("@result", SqlDbType.VarChar, 20).Value = result;
        command.Parameters.Add("@request_id", SqlDbType.VarChar, 100).Value = Limit(requestId, 100);
        command.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = Limit(message, 2000);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Limit(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty :
        value.Length <= length ? value : value[..length];
}

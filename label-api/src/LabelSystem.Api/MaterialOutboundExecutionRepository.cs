using System.Data;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class MaterialOutboundExecutionRepository(SqlConnectionFactory connections)
{
    public async Task<IReadOnlyList<MaterialOutboundExecutionOperation>> GetPlanAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT op.id, roots.root_id, op.submission_group_id, op.client_request_id,
                   op.operator_id, op.operation_type, op.erp_document_type, op.status, op.erp_status,
                   op.erp_document_no, op.erp_version_after, op.approval_status,
                   ISNULL(op.source_document_type,N''), ISNULL(op.source_document_id,N''), ISNULL(op.source_document_no,N''),
                   ISNULL(lines.warehouse_code,N''), op.device_id, op.device_model, op.reason,
                   op.archive_relative_path, op.archive_sha256,
                   line.id, line.line_no,
                   CASE WHEN line.label_id IS NOT NULL THEN 'PRODUCT_LABEL'
                        WHEN line.pallet_id IS NOT NULL THEN 'PALLET'
                        ELSE 'UNLABELED_MATERIAL' END,
                   line.label_id, line.pallet_id, line.source_pallet_id,
                   COALESCE(label.label_code,pallet.pallet_code,N''),
                   TRY_CONVERT(int,line.source_line_id), TRY_CONVERT(int,line.source_line_no),
                   line.material_code, line.batch_no, line.unit_code,
                   ISNULL(line.inventory_qty_snapshot,0), line.requested_qty,
                   ISNULL(line.from_warehouse_code,N''), line.from_location_code,
                   line.to_warehouse_code, line.to_location_code, line.remark,
                   TRY_CONVERT(bigint,JSON_VALUE(line.inventory_attributes_json,'$.traceBackLatestFlowId'))
            FROM dbo.business_operation op
            CROSS APPLY
            (
                SELECT MIN(root.id) AS root_id
                FROM dbo.business_operation root
                WHERE root.submission_group_id=op.submission_group_id
            ) roots
            OUTER APPLY
            (
                SELECT TOP (1) body.from_warehouse_code AS warehouse_code
                FROM dbo.business_operation_line body
                WHERE body.operation_id=op.id
                ORDER BY body.line_no
            ) lines
            LEFT JOIN dbo.business_operation_line line ON line.operation_id=op.id
            LEFT JOIN dbo.product_label label ON label.id=line.label_id
            LEFT JOIN dbo.pallet pallet ON pallet.id=line.pallet_id
            WHERE op.submission_group_id=@group_id
            ORDER BY op.id,line.line_no;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var builders = new Dictionary<long, OperationBuilder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var operatorId = reader.GetString(4);
            if (!string.Equals(operatorId, userId, StringComparison.OrdinalIgnoreCase))
                throw new ApiException(StatusCodes.Status403Forbidden,
                    "OPERATION_FORBIDDEN", "只能上传本人创建的单据。");
            var operationId = reader.GetInt64(0);
            if (!builders.TryGetValue(operationId, out var builder))
            {
                builder = new OperationBuilder(
                    operationId, reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    operatorId, reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11),
                    reader.GetString(12), reader.GetString(13), reader.GetString(14),
                    reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
                    reader.IsDBNull(20) ? string.Empty : reader.GetString(20));
                builders.Add(operationId, builder);
            }
            if (reader.IsDBNull(21)) continue;
            builder.Lines.Add(new MaterialOutboundExecutionLine(
                reader.GetInt64(21), reader.GetInt32(22), reader.GetString(23),
                reader.IsDBNull(24) ? null : reader.GetInt64(24),
                reader.IsDBNull(25) ? null : reader.GetInt64(25),
                reader.IsDBNull(26) ? null : reader.GetInt64(26), reader.GetString(27),
                reader.IsDBNull(28) ? null : reader.GetInt32(28),
                reader.IsDBNull(29) ? null : reader.GetInt32(29), reader.GetString(30),
                reader.IsDBNull(31) ? null : reader.GetString(31), reader.GetString(32),
                reader.GetDecimal(33), reader.GetDecimal(34), reader.GetString(35),
                reader.IsDBNull(36) ? null : reader.GetString(36),
                reader.IsDBNull(37) ? null : reader.GetString(37),
                reader.IsDBNull(38) ? null : reader.GetString(38),
                reader.IsDBNull(39) ? null : reader.GetString(39),
                reader.IsDBNull(40) ? null : reader.GetInt64(40)));
        }
        if (builders.Count == 0)
            throw new ApiException(StatusCodes.Status404NotFound,
                "OPERATION_NOT_FOUND", "业务操作不存在。");
        return builders.Values.Select(item => item.Build()).ToArray();
    }

    public async Task BeginAsync(
        MaterialOutboundExecutionOperation operation,
        int lockMinutes,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var command = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status='WRITING_ERP', erp_status='WRITING',
                heartbeat_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                lock_expires_at=DATEADD(MINUTE,@minutes,DATEADD(HOUR,8,SYSUTCDATETIME())),
                error_code=NULL,error_message=NULL,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND submission_group_id=@group_id AND operator_id=@user_id
              AND status='PENDING' AND erp_status='PENDING';
            """, connection, transaction))
        {
            command.Parameters.Add("@minutes", SqlDbType.Int).Value = Math.Clamp(lockMinutes, 1, 120);
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = operation.SubmissionGroupId;
            command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = operation.OperatorId;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "OPERATION_NOT_PENDING", "业务操作不能上传。");
        }
        foreach (var line in operation.Lines)
        {
            if (line.ObjectType == "UNLABELED_MATERIAL") continue;
            var table = line.ObjectType == "PRODUCT_LABEL" ? "product_label" : "pallet";
            var id = line.LabelId ?? line.PalletId ?? 0;
            await using var lockCheck = new SqlCommand(
                $"SELECT locked_operation_id FROM dbo.{table} WITH (UPDLOCK,HOLDLOCK) WHERE id=@id;",
                connection, transaction);
            lockCheck.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var lockedBy = await lockCheck.ExecuteScalarAsync(cancellationToken);
            if (lockedBy is null || lockedBy == DBNull.Value || Convert.ToInt64(lockedBy) != operation.RootOperationId)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "INVENTORY_LOCK_CHANGED", "标签占用状态已变化，需要核对。");
        }
        await InsertEventAsync(connection, transaction, operation.Id, "U8_WRITING", "INFO",
            requestId, "开始写入 U8。", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task BeginReconciliationAsync(
        MaterialOutboundExecutionOperation operation,
        int lockMinutes,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var command = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status='WRITING_ERP',erp_status='WRITING',
                heartbeat_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                lock_expires_at=DATEADD(MINUTE,@minutes,DATEADD(HOUR,8,SYSUTCDATETIME())),
                error_code=NULL,error_message=NULL,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND submission_group_id=@group_id AND operator_id=@user_id
              AND status='NEEDS_RECONCILIATION' AND erp_status='UNKNOWN';
            """, connection, transaction))
        {
            command.Parameters.Add("@minutes", SqlDbType.Int).Value = Math.Clamp(lockMinutes, 1, 120);
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = operation.SubmissionGroupId;
            command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = operation.OperatorId;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "OPERATION_NOT_RECONCILABLE", "业务操作当前不能核对。");
        }
        foreach (var line in operation.Lines)
        {
            if (line.ObjectType == "UNLABELED_MATERIAL") continue;
            var table = line.ObjectType == "PRODUCT_LABEL" ? "product_label" : "pallet";
            var id = line.LabelId ?? line.PalletId ?? 0;
            await using var lockCheck = new SqlCommand(
                $"SELECT locked_operation_id FROM dbo.{table} WITH (UPDLOCK,HOLDLOCK) WHERE id=@id;",
                connection, transaction);
            lockCheck.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var lockedBy = await lockCheck.ExecuteScalarAsync(cancellationToken);
            if (lockedBy is null || lockedBy == DBNull.Value ||
                Convert.ToInt64(lockedBy) != operation.RootOperationId)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "INVENTORY_LOCK_CHANGED", "标签占用状态已变化，需要人工核对。");
        }
        await InsertEventAsync(connection, transaction, operation.Id,
            "RECONCILIATION_STARTED", "INFO", requestId,
            "使用原幂等编号核对 U8 结果。", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        MaterialOutboundExecutionOperation operation,
        GatewayDocumentResult result,
        bool redVoucher,
        bool approvalRequired,
        IReadOnlyDictionary<int, (string? LineId, string? PositionId)> erpRows,
        IReadOnlyList<TraceBackSyncTaskDraft> traceBackTasks,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!result.Confirmed || string.IsNullOrWhiteSpace(result.VoucherId) ||
            string.IsNullOrWhiteSpace(result.VoucherCode))
            throw new InvalidOperationException("U8 结果未确认。");
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        foreach (var line in operation.Lines)
        {
            var erp = erpRows.TryGetValue(line.LineNo, out var row)
                ? row
                : ((string? LineId, string? PositionId))(null, null);
            if (operation.ErpDocumentType is "material-outbound" or "sales-outbound")
            {
                if (line.ObjectType == "PRODUCT_LABEL")
                    await CompleteLabelAsync(connection, transaction, operation, line, erp,
                        redVoucher, cancellationToken);
                else if (line.ObjectType == "PALLET")
                    await CompletePalletAsync(connection, transaction, operation, line, erp,
                        redVoucher, cancellationToken);
                else if (operation.ErpDocumentType == "material-outbound" && redVoucher)
                    await CompleteGeneratedLabelAsync(
                        connection, transaction, operation, line, erp, cancellationToken);
                else
                    throw new InvalidOperationException("无标签明细只能用于红字材料出库单。");
            }
            else if (operation.ErpDocumentType is "transfer" or "location-adjustment")
            {
                await CompleteMoveAsync(connection, transaction, operation, line, erp,
                    cancellationToken);
            }
            else if (operation.ErpDocumentType == "inventory-count")
            {
                await CompleteCountAsync(connection, transaction, operation, line, erp,
                    cancellationToken);
            }
            else
            {
                await ReleaseObjectAsync(connection, transaction, operation, line, cancellationToken);
                await UpdateLineAsync(connection, transaction, line, erp, cancellationToken);
            }
        }

        foreach (var task in traceBackTasks)
            await InsertTraceBackTaskAsync(connection, transaction, task, cancellationToken);
        var hasTraceBackTasks = traceBackTasks.Count > 0;

        await using (var update = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status=CASE WHEN @traceback_pending=1 THEN 'NEEDS_RECONCILIATION' ELSE @status END,
                erp_status='SUCCESS',erp_document_id=@document_id,
                erp_document_no=@document_no,erp_version_after=@version,
                approval_status=@approval_status,
                erp_confirmed_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                lock_acquired_at=NULL,heartbeat_at=NULL,lock_expires_at=NULL,
                error_code=CASE WHEN @traceback_pending=1 THEN 'TRACEBACK_SYNC_PENDING' ELSE NULL END,
                error_message=CASE WHEN @traceback_pending=1 THEN N'ERP 已生成，标签同步中。' ELSE NULL END,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                completed_at=CASE WHEN @approval_required=1 OR @traceback_pending=1
                                  THEN NULL ELSE DATEADD(HOUR,8,SYSUTCDATETIME()) END
            WHERE id=@id AND status='WRITING_ERP' AND erp_status='WRITING';
            """, connection, transaction))
        {
            update.Parameters.Add("@document_id", SqlDbType.NVarChar, 100).Value = result.VoucherId;
            update.Parameters.Add("@document_no", SqlDbType.NVarChar, 100).Value = result.VoucherCode;
            update.Parameters.Add("@version", SqlDbType.NVarChar, 100).Value = DbValue(result.Version, 100);
            update.Parameters.Add("@status", SqlDbType.VarChar, 30).Value =
                approvalRequired ? "PENDING_APPROVAL" : "SUCCESS";
            update.Parameters.Add("@approval_status", SqlDbType.VarChar, 20).Value =
                approvalRequired ? "PENDING" : "NOT_REQUIRED";
            update.Parameters.Add("@approval_required", SqlDbType.Bit).Value = approvalRequired;
            update.Parameters.Add("@traceback_pending", SqlDbType.Bit).Value = hasTraceBackTasks;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("业务操作状态已变化。");
        }
        await InsertEventAsync(connection, transaction, operation.Id, "U8_CONFIRMED", "SUCCESS",
            requestId, $"U8 单据已确认：{result.VoucherCode}", cancellationToken);
        if (hasTraceBackTasks)
        {
            await InsertEventAsync(connection, transaction, operation.Id,
                "TRACEBACK_SYNC_PENDING", "INFO", requestId,
                "ERP 已生成，标签同步中。", cancellationToken);
        }
        else if (approvalRequired)
        {
            await InsertEventAsync(connection, transaction, operation.Id, "APPROVAL_PENDING", "INFO",
                requestId, "U8 单据等待自动审核。", cancellationToken);
        }
        else
        {
            await ReleaseGroupLocksWhenSuccessfulAsync(
                connection, transaction, operation.SubmissionGroupId, operation.RootOperationId,
                cancellationToken);
            await SetDraftCompletedWhenTerminalAsync(
                connection, transaction, operation.SubmissionGroupId, operation.OperatorId,
                requestId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertTraceBackTaskAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TraceBackSyncTaskDraft task,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO dbo.traceback_sync_task
                (event_key,operation_id,operation_line_id,label_id,event_phase,event_role,
                 traceback_code,pda_order_uuid,pda_goods_uuid,erp_document_type,erp_document_no,
                 payload_json,payload_hash,status)
            VALUES
                (@event_key,@operation_id,@line_id,@label_id,'ERP_SOURCE',@event_role,
                 @code,@order_uuid,@goods_uuid,@document_type,@document_no,
                 @payload,@payload_hash,'PENDING');
            """, connection, transaction);
        command.Parameters.Add("@event_key", SqlDbType.VarChar, 200).Value = task.EventKey;
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = task.OperationId;
        command.Parameters.Add("@line_id", SqlDbType.BigInt).Value = task.OperationLineId;
        command.Parameters.Add("@label_id", SqlDbType.BigInt).Value = task.LabelId;
        command.Parameters.Add("@event_role", SqlDbType.VarChar, 40).Value = task.EventRole;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = task.TraceBackCode;
        command.Parameters.Add("@order_uuid", SqlDbType.UniqueIdentifier).Value = task.PdaOrderUuid;
        command.Parameters.Add("@goods_uuid", SqlDbType.UniqueIdentifier).Value = task.PdaGoodsUuid;
        command.Parameters.Add("@document_type", SqlDbType.VarChar, 50).Value = task.ErpDocumentType;
        command.Parameters.Add("@document_no", SqlDbType.NVarChar, 100).Value = task.ErpDocumentNo;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = task.PayloadJson;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value = task.PayloadHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteApprovalAsync(
        MaterialOutboundExecutionOperation operation,
        GatewayDocumentResult result,
        string approverName,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!result.Confirmed || string.IsNullOrWhiteSpace(result.VoucherCode))
            throw new InvalidOperationException("U8 审核结果未确认。");
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var update = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status='SUCCESS',approval_status='SUCCESS',approver_id=@approver_id,
                approver_name_snapshot=@approver_name,approved_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                erp_version_after=@version,error_code=NULL,error_message=NULL,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()),completed_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND status='PENDING_APPROVAL' AND erp_status='SUCCESS'
              AND erp_document_no=@document_no;
            """, connection, transaction))
        {
            update.Parameters.Add("@approver_id", SqlDbType.NVarChar, 100).Value = operation.OperatorId;
            update.Parameters.Add("@approver_name", SqlDbType.NVarChar, 100).Value = Limit(approverName, 100);
            update.Parameters.Add("@version", SqlDbType.NVarChar, 100).Value = DbValue(result.Version, 100);
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            update.Parameters.Add("@document_no", SqlDbType.NVarChar, 100).Value = result.VoucherCode;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("待审核业务状态已变化。");
        }
        await InsertEventAsync(connection, transaction, operation.Id, "APPROVAL_CONFIRMED", "SUCCESS",
            requestId, $"U8 单据已自动审核：{result.VoucherCode}", cancellationToken);
        await ReleaseGroupLocksWhenSuccessfulAsync(
            connection, transaction, operation.SubmissionGroupId, operation.RootOperationId,
            cancellationToken);
        await SetDraftCompletedWhenTerminalAsync(
            connection, transaction, operation.SubmissionGroupId, operation.OperatorId,
            requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> GetNextApprovalAttemptAsync(
        long operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT COUNT(*)+1
            FROM dbo.business_operation_event
            WHERE operation_id=@id
              AND event_type IN ('APPROVAL_FAILED','APPROVAL_RECONCILIATION');
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = operationId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task MarkApprovalFailureAsync(
        MaterialOutboundExecutionOperation operation,
        string code,
        string message,
        bool resultUnknown,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await using (var update = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status=CASE WHEN @unknown=1 THEN 'NEEDS_RECONCILIATION' ELSE 'PENDING_APPROVAL' END,
                approval_status='FAILED',error_code=@code,error_message=@message,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()),completed_at=NULL
            WHERE id=@id AND status='PENDING_APPROVAL' AND erp_status='SUCCESS';
            """, connection, transaction))
        {
            update.Parameters.Add("@unknown", SqlDbType.Bit).Value = resultUnknown;
            update.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = Limit(code, 100);
            update.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = Limit(message, 2000);
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertEventAsync(connection, transaction, operation.Id,
            resultUnknown ? "APPROVAL_RECONCILIATION" : "APPROVAL_FAILED",
            resultUnknown ? "UNKNOWN" : "FAILED", requestId, message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFailureAsync(
        MaterialOutboundExecutionOperation operation,
        string code,
        string message,
        bool resultUnknown,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var status = resultUnknown ? "NEEDS_RECONCILIATION" : "FAILED";
        var erpStatus = resultUnknown ? "UNKNOWN" : "FAILED";
        await using (var update = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status=@status,erp_status=@erp_status,error_code=@code,error_message=@message,
                lock_acquired_at=CASE WHEN @unknown=1 THEN lock_acquired_at ELSE NULL END,
                heartbeat_at=CASE WHEN @unknown=1 THEN heartbeat_at ELSE NULL END,
                lock_expires_at=CASE WHEN @unknown=1 THEN lock_expires_at ELSE NULL END,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                completed_at=CASE WHEN @unknown=1 THEN NULL ELSE DATEADD(HOUR,8,SYSUTCDATETIME()) END
            WHERE id=@id AND status='WRITING_ERP';
            """, connection, transaction))
        {
            update.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
            update.Parameters.Add("@erp_status", SqlDbType.VarChar, 30).Value = erpStatus;
            update.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = Limit(code, 100);
            update.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = Limit(message, 2000);
            update.Parameters.Add("@unknown", SqlDbType.Bit).Value = resultUnknown;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = operation.Id;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!resultUnknown)
        {
            await ReleaseRootLocksAsync(connection, transaction, operation.RootOperationId, cancellationToken);
            await using var failLines = new SqlCommand("""
                UPDATE dbo.business_operation_line
                SET line_status='FAILED',error_code=@code,error_message=@message,
                    updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE operation_id IN
                    (SELECT id FROM dbo.business_operation WHERE submission_group_id=@group_id)
                  AND line_status='PENDING';
                """, connection, transaction);
            failLines.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = Limit(code, 100);
            failLines.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = Limit(message, 2000);
            failLines.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = operation.SubmissionGroupId;
            await failLines.ExecuteNonQueryAsync(cancellationToken);
            await using var abortRemaining = new SqlCommand("""
                UPDATE dbo.business_operation
                SET status='FAILED',erp_status='FAILED',error_code='GROUP_ABORTED',
                    error_message=N'同组单据上传失败，后续拆组未执行。',
                    lock_acquired_at=NULL,heartbeat_at=NULL,lock_expires_at=NULL,
                    updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                    completed_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE submission_group_id=@group_id AND status='PENDING' AND erp_status='PENDING';
                """, connection, transaction);
            abortRemaining.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = operation.SubmissionGroupId;
            await abortRemaining.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertEventAsync(connection, transaction, operation.Id,
            resultUnknown ? "RECONCILIATION_REQUIRED" : "U8_FAILED",
            resultUnknown ? "UNKNOWN" : "FAILED", requestId, message, cancellationToken);
        if (!resultUnknown)
            await SetDraftCompletedWhenTerminalAsync(
                connection, transaction, operation.SubmissionGroupId, operation.OperatorId,
                requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteGeneratedLabelAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation,
        MaterialOutboundExecutionLine line,
        (string? LineId, string? PositionId) erp,
        CancellationToken cancellationToken)
    {
        var generatedLabelId = await CreatePendingProductLabelAsync(
            connection, transaction, line.RequestedQty, cancellationToken);

        long flowId;
        await using (var insertFlow = new SqlCommand("""
            INSERT INTO dbo.inventory_flow
                (operation_id,operation_line_id,object_type,label_id,object_sequence_no,
                 flow_role,operation_type,unit_code,qty_before,change_qty,qty_after,
                 material_code,batch_no,inventory_attributes_json,attribute_schema_version,
                 to_warehouse_code,to_location_code,remark)
            OUTPUT inserted.id
            VALUES
                (@operation_id,@line_id,'PRODUCT_LABEL',@label_id,1,
                 'NORMAL',@operation_type,@unit_code,0,@quantity,@quantity,
                 @material_code,@batch_no,@attributes,1,
                 @warehouse,@location,@remark);
            """, connection, transaction))
        {
            insertFlow.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operation.Id;
            insertFlow.Parameters.Add("@line_id", SqlDbType.BigInt).Value = line.Id;
            insertFlow.Parameters.Add("@label_id", SqlDbType.BigInt).Value = generatedLabelId;
            insertFlow.Parameters.Add("@operation_type", SqlDbType.VarChar, 50).Value = operation.OperationType;
            insertFlow.Parameters.Add("@unit_code", SqlDbType.NVarChar, 50).Value = line.UnitCode;
            insertFlow.Parameters.Add("@quantity", SqlDbType.Decimal).ConfigureDecimal(line.RequestedQty);
            insertFlow.Parameters.Add("@material_code", SqlDbType.NVarChar, 100).Value = line.MaterialCode;
            insertFlow.Parameters.Add("@batch_no", SqlDbType.NVarChar, 100).Value = DbValue(line.BatchNo, 100);
            insertFlow.Parameters.Add("@attributes", SqlDbType.NVarChar, -1).Value = DBNull.Value;
            insertFlow.Parameters.Add("@warehouse", SqlDbType.NVarChar, 100).Value = line.WarehouseCode;
            insertFlow.Parameters.Add("@location", SqlDbType.NVarChar, 100).Value = DbValue(line.LocationCode, 100);
            insertFlow.Parameters.Add("@remark", SqlDbType.NVarChar, 500).Value = DbValue(line.Remark, 500);
            flowId = Convert.ToInt64(await insertFlow.ExecuteScalarAsync(cancellationToken));
        }

        await using (var updateLabel = new SqlCommand(
                         "UPDATE dbo.product_label SET last_flow_id=@flow_id,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@label_id;",
                         connection, transaction))
        {
            updateLabel.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = flowId;
            updateLabel.Parameters.Add("@label_id", SqlDbType.BigInt).Value = generatedLabelId;
            await updateLabel.ExecuteNonQueryAsync(cancellationToken);
        }
        await UpdateLineAsync(connection, transaction, line, erp, cancellationToken);
        await LinkGeneratedLabelAsync(
            connection, transaction, operation.Id, line.Id, generatedLabelId, cancellationToken);
    }

    private static async Task<long> CreatePendingProductLabelAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        await using (var insertLabel = new SqlCommand("""
            INSERT INTO dbo.product_label(label_code,current_qty,status)
            OUTPUT inserted.id
            VALUES(NULL,@quantity,'PENDING_PRINT');
            """, connection, transaction))
        {
            insertLabel.Parameters.Add("@quantity", SqlDbType.Decimal).ConfigureDecimal(quantity);
            return Convert.ToInt64(await insertLabel.ExecuteScalarAsync(cancellationToken));
        }
    }

    private static async Task CompleteLabelAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation, MaterialOutboundExecutionLine line,
        (string? LineId, string? PositionId) erp, bool redVoucher,
        CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand("""
            SELECT current_qty,status,locked_operation_id,last_flow_id
            FROM dbo.product_label WITH (UPDLOCK,HOLDLOCK) WHERE id=@id;
            """, connection, transaction);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = line.LabelId!.Value;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("标签不存在。");
        var before = reader.GetDecimal(0);
        var lockedBy = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        var previousFlowId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
        await reader.CloseAsync();
        if (lockedBy != operation.RootOperationId) throw new InvalidOperationException("标签锁已变化。");
        var change = redVoucher ? line.RequestedQty : -line.RequestedQty;
        var after = before + change;
        if (after < 0) throw new InvalidOperationException("标签数量不足。");
        var flowId = await InsertFlowAsync(connection, transaction, operation, line,
            "PRODUCT_LABEL", line.LabelId, null, before, change, after, redVoucher,
            previousFlowId, cancellationToken);
        await using (var update = new SqlCommand("""
            UPDATE dbo.product_label
            SET current_qty=@qty,status=@status,last_flow_id=@flow_id,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND locked_operation_id=@root_id;
            """, connection, transaction))
        {
            update.Parameters.Add("@qty", SqlDbType.Decimal).ConfigureDecimal(after);
            update.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = after == 0 ? "USED" : "ACTIVE";
            update.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = flowId;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = line.LabelId.Value;
            update.Parameters.Add("@root_id", SqlDbType.BigInt).Value = operation.RootOperationId;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("标签更新冲突。");
        }
        await UpdateLineAsync(connection, transaction, line, erp, cancellationToken);
    }

    private static async Task CompletePalletAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation, MaterialOutboundExecutionLine line,
        (string? LineId, string? PositionId) erp, bool redVoucher,
        CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand("""
            SELECT current_qty,locked_operation_id,last_flow_id
            FROM dbo.pallet WITH (UPDLOCK,HOLDLOCK) WHERE id=@id;
            """, connection, transaction);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = line.PalletId!.Value;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("托盘不存在。");
        var before = reader.GetDecimal(0);
        var lockedBy = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var previousFlowId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        await reader.CloseAsync();
        var change = redVoucher ? 1m : -1m;
        var after = before + change;
        if (lockedBy != operation.RootOperationId || after is < 0 or > 1)
            throw new InvalidOperationException("托盘状态已变化。");
        var flowId = await InsertFlowAsync(connection, transaction, operation, line,
            "PALLET", null, line.PalletId, before, change, after, redVoucher,
            previousFlowId, cancellationToken);
        await using (var update = new SqlCommand("""
            UPDATE dbo.pallet
            SET current_qty=@qty,status=@status,last_flow_id=@flow_id,last_operation_id=@operation_id,
                current_warehouse_code=CASE WHEN @qty=0 THEN NULL ELSE @warehouse END,
                current_location_code=CASE WHEN @qty=0 THEN NULL ELSE @location END,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND locked_operation_id=@root_id;
            """, connection, transaction))
        {
            update.Parameters.Add("@qty", SqlDbType.Decimal).ConfigureDecimal(after);
            update.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = after == 0 ? "OUT_OF_STOCK" : "ACTIVE";
            update.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = flowId;
            update.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operation.Id;
            update.Parameters.Add("@warehouse", SqlDbType.NVarChar, 100).Value = line.WarehouseCode;
            update.Parameters.Add("@location", SqlDbType.NVarChar, 100).Value = DbValue(line.LocationCode, 100);
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = line.PalletId.Value;
            update.Parameters.Add("@root_id", SqlDbType.BigInt).Value = operation.RootOperationId;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("托盘更新冲突。");
        }
        await UpdateLineAsync(connection, transaction, line, erp, cancellationToken);
    }

    private static async Task<long> InsertFlowAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation, MaterialOutboundExecutionLine line,
        string objectType, long? labelId, long? palletId,
        decimal before, decimal change, decimal after, bool redVoucher,
        long? previousFlowId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.inventory_flow
                (operation_id,operation_line_id,object_type,label_id,pallet_id,object_sequence_no,
                 flow_role,operation_type,unit_code,qty_before,change_qty,qty_after,
                 material_code,batch_no,manufacture_date,expiry_date,serial_no,
                 inventory_attributes_json,attribute_schema_version,inventory_identity_hash,
                 from_warehouse_code,from_location_code,
                 to_warehouse_code,to_location_code,remark)
            OUTPUT inserted.id
            SELECT @operation_id,@line_id,@object_type,@label_id,@pallet_id,
                   (SELECT ISNULL(MAX(sequence_flow.object_sequence_no),0)+1
                    FROM dbo.inventory_flow sequence_flow WITH (UPDLOCK,HOLDLOCK)
                    WHERE (@label_id IS NOT NULL AND sequence_flow.label_id=@label_id)
                       OR (@pallet_id IS NOT NULL AND sequence_flow.pallet_id=@pallet_id)),
                   'NORMAL',@operation_type,@unit_code,@before,@change,@after,
                   @material_code,@batch_no,previous.manufacture_date,previous.expiry_date,
                   previous.serial_no,previous.inventory_attributes_json,
                   ISNULL(previous.attribute_schema_version,1),previous.inventory_identity_hash,
                   CASE WHEN @red=0 THEN @warehouse ELSE NULL END,
                   CASE WHEN @red=0 THEN @location ELSE NULL END,
                   CASE WHEN @red=1 OR @after>0 THEN @warehouse ELSE NULL END,
                   CASE WHEN @red=1 OR @after>0 THEN @location ELSE NULL END,@remark
            FROM (SELECT 1 AS marker) seed
            LEFT JOIN dbo.inventory_flow previous ON previous.id=@previous_flow_id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operation.Id;
        command.Parameters.Add("@line_id", SqlDbType.BigInt).Value = line.Id;
        command.Parameters.Add("@object_type", SqlDbType.VarChar, 20).Value = objectType;
        command.Parameters.Add("@label_id", SqlDbType.BigInt).Value = labelId.HasValue ? labelId.Value : DBNull.Value;
        command.Parameters.Add("@pallet_id", SqlDbType.BigInt).Value = palletId.HasValue ? palletId.Value : DBNull.Value;
        command.Parameters.Add("@previous_flow_id", SqlDbType.BigInt).Value =
            previousFlowId.HasValue ? previousFlowId.Value : DBNull.Value;
        command.Parameters.Add("@operation_type", SqlDbType.VarChar, 50).Value = operation.OperationType;
        command.Parameters.Add("@unit_code", SqlDbType.NVarChar, 50).Value = line.UnitCode;
        command.Parameters.Add("@before", SqlDbType.Decimal).ConfigureDecimal(before);
        command.Parameters.Add("@change", SqlDbType.Decimal).ConfigureDecimal(change);
        command.Parameters.Add("@after", SqlDbType.Decimal).ConfigureDecimal(after);
        command.Parameters.Add("@material_code", SqlDbType.NVarChar, 100).Value = line.MaterialCode;
        command.Parameters.Add("@batch_no", SqlDbType.NVarChar, 100).Value = DbValue(line.BatchNo, 100);
        command.Parameters.Add("@warehouse", SqlDbType.NVarChar, 100).Value = line.WarehouseCode;
        command.Parameters.Add("@location", SqlDbType.NVarChar, 100).Value = DbValue(line.LocationCode, 100);
        command.Parameters.Add("@remark", SqlDbType.NVarChar, 500).Value = DbValue(line.Remark, 500);
        command.Parameters.Add("@red", SqlDbType.Bit).Value = redVoucher;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task CompleteMoveAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation, MaterialOutboundExecutionLine line,
        (string? LineId, string? PositionId) erp,
        CancellationToken cancellationToken)
    {
        var table = line.ObjectType == "PRODUCT_LABEL" ? "product_label" : "pallet";
        var id = line.LabelId ?? line.PalletId ?? throw new InvalidOperationException("库存对象不存在。");
        await using var read = new SqlCommand(
            $"SELECT current_qty,locked_operation_id,last_flow_id FROM dbo.{table} WITH (UPDLOCK,HOLDLOCK) WHERE id=@id;",
            connection, transaction);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("库存对象不存在。");
        var quantity = reader.GetDecimal(0);
        var lockedBy = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var previousFlowId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        await reader.CloseAsync();
        if (lockedBy != operation.RootOperationId) throw new InvalidOperationException("库存对象锁已变化。");
        if (line.RequestedQty <= 0 || line.RequestedQty > quantity)
            throw new InvalidOperationException("移动数量无效。");
        if ((line.ObjectType == "PALLET" || line.SourcePalletId.HasValue) && line.RequestedQty != quantity)
            throw new InvalidOperationException("托盘必须整体移动。");

        var targetWarehouse = line.TargetWarehouseCode ?? line.WarehouseCode;
        var targetLocation = line.TargetLocationCode;
        if (line.ObjectType == "PRODUCT_LABEL" && line.RequestedQty < quantity)
        {
            await CompletePartialMoveAsync(
                connection, transaction, operation, line, erp, quantity,
                targetWarehouse, targetLocation, previousFlowId, cancellationToken);
            return;
        }
        var flowId = await InsertStateFlowAsync(
            connection, transaction, operation, line, quantity, 0m, quantity,
            line.WarehouseCode, line.LocationCode, targetWarehouse, targetLocation,
            previousFlowId, cancellationToken);
        var updateSql = line.ObjectType == "PRODUCT_LABEL"
            ? "UPDATE dbo.product_label SET last_flow_id=@flow_id,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id AND locked_operation_id=@root_id;"
            : "UPDATE dbo.pallet SET last_flow_id=@flow_id,last_operation_id=@operation_id,current_warehouse_code=@warehouse,current_location_code=@location,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id AND locked_operation_id=@root_id;";
        await using var update = new SqlCommand(updateSql, connection, transaction);
        update.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = flowId;
        update.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operation.Id;
        update.Parameters.Add("@warehouse", SqlDbType.NVarChar, 100).Value = targetWarehouse;
        update.Parameters.Add("@location", SqlDbType.NVarChar, 100).Value = DbValue(targetLocation, 100);
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        update.Parameters.Add("@root_id", SqlDbType.BigInt).Value = operation.RootOperationId;
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("库存对象更新冲突。");
        await UpdateLineAsync(connection, transaction, line, erp, cancellationToken);
    }

    private async Task CompletePartialMoveAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation,
        MaterialOutboundExecutionLine line,
        (string? LineId, string? PositionId) erp,
        decimal before,
        string targetWarehouse,
        string? targetLocation,
        long? previousFlowId,
        CancellationToken cancellationToken)
    {
        var remaining = before - line.RequestedQty;
        var generatedLabelId = await CreatePendingProductLabelAsync(
            connection, transaction, line.RequestedQty, cancellationToken);

        const string sourceFlowSql = """
            INSERT INTO dbo.inventory_flow
                (operation_id,operation_line_id,object_type,label_id,object_sequence_no,
                 relation_group_no,flow_role,operation_type,unit_code,
                 qty_before,change_qty,qty_after,material_code,batch_no,
                 manufacture_date,expiry_date,serial_no,inventory_attributes_json,
                 attribute_schema_version,inventory_identity_hash,
                 from_warehouse_code,from_location_code,to_warehouse_code,to_location_code,remark)
            OUTPUT inserted.id
            SELECT @operation_id,@line_id,'PRODUCT_LABEL',@label_id,
                   (SELECT ISNULL(MAX(sequence_flow.object_sequence_no),0)+1
                    FROM dbo.inventory_flow sequence_flow WITH (UPDLOCK,HOLDLOCK)
                    WHERE sequence_flow.label_id=@label_id),
                   @relation_group_no,'SOURCE','SPLIT',@unit_code,
                   @before,-@quantity,@remaining,@material_code,@batch_no,
                   previous.manufacture_date,previous.expiry_date,previous.serial_no,
                   previous.inventory_attributes_json,ISNULL(previous.attribute_schema_version,1),
                   previous.inventory_identity_hash,
                   @source_warehouse,@source_location,@source_warehouse,@source_location,@remark
            FROM (SELECT 1 AS marker) seed
            LEFT JOIN dbo.inventory_flow previous ON previous.id=@previous_flow_id;
            """;
        long sourceFlowId;
        await using (var sourceFlow = new SqlCommand(sourceFlowSql, connection, transaction))
        {
            AddSplitFlowParameters(
                sourceFlow, operation, line, line.LabelId!.Value, line.LineNo,
                before, line.RequestedQty, remaining, line.WarehouseCode, line.LocationCode,
                targetWarehouse, targetLocation, previousFlowId);
            sourceFlowId = Convert.ToInt64(await sourceFlow.ExecuteScalarAsync(cancellationToken));
        }

        const string targetFlowSql = """
            INSERT INTO dbo.inventory_flow
                (operation_id,operation_line_id,object_type,label_id,object_sequence_no,
                 relation_group_no,flow_role,operation_type,unit_code,
                 qty_before,change_qty,qty_after,material_code,batch_no,
                 manufacture_date,expiry_date,serial_no,inventory_attributes_json,
                 attribute_schema_version,inventory_identity_hash,
                 from_warehouse_code,from_location_code,to_warehouse_code,to_location_code,remark)
            OUTPUT inserted.id
            SELECT @operation_id,@line_id,'PRODUCT_LABEL',@label_id,1,
                   @relation_group_no,'TARGET','SPLIT',@unit_code,
                   0,@quantity,@quantity,@material_code,@batch_no,
                   previous.manufacture_date,previous.expiry_date,previous.serial_no,
                   previous.inventory_attributes_json,ISNULL(previous.attribute_schema_version,1),
                   previous.inventory_identity_hash,
                   @source_warehouse,@source_location,@target_warehouse,@target_location,@remark
            FROM (SELECT 1 AS marker) seed
            LEFT JOIN dbo.inventory_flow previous ON previous.id=@previous_flow_id;
            """;
        long targetFlowId;
        await using (var targetFlow = new SqlCommand(targetFlowSql, connection, transaction))
        {
            AddSplitFlowParameters(
                targetFlow, operation, line, generatedLabelId, line.LineNo,
                before, line.RequestedQty, remaining, line.WarehouseCode, line.LocationCode,
                targetWarehouse, targetLocation, previousFlowId);
            targetFlowId = Convert.ToInt64(await targetFlow.ExecuteScalarAsync(cancellationToken));
        }

        await using (var updateSource = new SqlCommand("""
            UPDATE dbo.product_label
            SET current_qty=@quantity,status='ACTIVE',last_flow_id=@flow_id,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@label_id AND locked_operation_id=@root_id;
            """, connection, transaction))
        {
            updateSource.Parameters.Add("@quantity", SqlDbType.Decimal).ConfigureDecimal(remaining);
            updateSource.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = sourceFlowId;
            updateSource.Parameters.Add("@label_id", SqlDbType.BigInt).Value = line.LabelId!.Value;
            updateSource.Parameters.Add("@root_id", SqlDbType.BigInt).Value = operation.RootOperationId;
            if (await updateSource.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("原标签拆分更新冲突。");
        }
        await using (var updateTarget = new SqlCommand(
                         "UPDATE dbo.product_label SET last_flow_id=@flow_id,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@label_id AND status='PENDING_PRINT';",
                         connection, transaction))
        {
            updateTarget.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = targetFlowId;
            updateTarget.Parameters.Add("@label_id", SqlDbType.BigInt).Value = generatedLabelId;
            if (await updateTarget.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("新标签拆分更新冲突。");
        }

        await UpdateLineAsync(connection, transaction, line, erp, cancellationToken);
        await LinkGeneratedLabelAsync(
            connection, transaction, operation.Id, line.Id, generatedLabelId, cancellationToken);
    }

    private static void AddSplitFlowParameters(
        SqlCommand command,
        MaterialOutboundExecutionOperation operation,
        MaterialOutboundExecutionLine line,
        long labelId,
        int relationGroupNo,
        decimal before,
        decimal quantity,
        decimal remaining,
        string sourceWarehouse,
        string? sourceLocation,
        string targetWarehouse,
        string? targetLocation,
        long? previousFlowId)
    {
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operation.Id;
        command.Parameters.Add("@line_id", SqlDbType.BigInt).Value = line.Id;
        command.Parameters.Add("@label_id", SqlDbType.BigInt).Value = labelId;
        command.Parameters.Add("@relation_group_no", SqlDbType.Int).Value = relationGroupNo;
        command.Parameters.Add("@unit_code", SqlDbType.NVarChar, 50).Value = line.UnitCode;
        command.Parameters.Add("@before", SqlDbType.Decimal).ConfigureDecimal(before);
        command.Parameters.Add("@quantity", SqlDbType.Decimal).ConfigureDecimal(quantity);
        command.Parameters.Add("@remaining", SqlDbType.Decimal).ConfigureDecimal(remaining);
        command.Parameters.Add("@material_code", SqlDbType.NVarChar, 100).Value = line.MaterialCode;
        command.Parameters.Add("@batch_no", SqlDbType.NVarChar, 100).Value = DbValue(line.BatchNo, 100);
        command.Parameters.Add("@source_warehouse", SqlDbType.NVarChar, 100).Value = sourceWarehouse;
        command.Parameters.Add("@source_location", SqlDbType.NVarChar, 100).Value = DbValue(sourceLocation, 100);
        command.Parameters.Add("@target_warehouse", SqlDbType.NVarChar, 100).Value = targetWarehouse;
        command.Parameters.Add("@target_location", SqlDbType.NVarChar, 100).Value = DbValue(targetLocation, 100);
        command.Parameters.Add("@previous_flow_id", SqlDbType.BigInt).Value = previousFlowId.HasValue
            ? previousFlowId.Value
            : DBNull.Value;
        command.Parameters.Add("@remark", SqlDbType.NVarChar, 500).Value = DbValue(line.Remark, 500);
    }

    private static async Task CompleteCountAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation, MaterialOutboundExecutionLine line,
        (string? LineId, string? PositionId) erp,
        CancellationToken cancellationToken)
    {
        var table = line.ObjectType == "PRODUCT_LABEL" ? "product_label" : "pallet";
        var id = line.LabelId ?? line.PalletId ?? throw new InvalidOperationException("库存对象不存在。");
        await using var read = new SqlCommand(
            $"SELECT current_qty,locked_operation_id,last_flow_id FROM dbo.{table} WITH (UPDLOCK,HOLDLOCK) WHERE id=@id;",
            connection, transaction);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("库存对象不存在。");
        var before = reader.GetDecimal(0);
        var lockedBy = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var previousFlowId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        await reader.CloseAsync();
        if (lockedBy != operation.RootOperationId) throw new InvalidOperationException("库存对象锁已变化。");
        var after = line.RequestedQty;
        if (line.ObjectType == "PALLET" && after is not (0m or 1m))
            throw new InvalidOperationException("托盘盘点数量无效。");
        var flowId = await InsertStateFlowAsync(
            connection, transaction, operation, line, before, after - before, after,
            line.WarehouseCode, line.LocationCode, line.WarehouseCode, line.LocationCode,
            previousFlowId, cancellationToken);
        var status = line.ObjectType == "PRODUCT_LABEL"
            ? (after == 0 ? "USED" : "ACTIVE")
            : (after == 0 ? "OUT_OF_STOCK" : "ACTIVE");
        var updateSql = line.ObjectType == "PRODUCT_LABEL"
            ? "UPDATE dbo.product_label SET current_qty=@qty,status=@status,last_flow_id=@flow_id,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id AND locked_operation_id=@root_id;"
            : "UPDATE dbo.pallet SET current_qty=@qty,status=@status,last_flow_id=@flow_id,last_operation_id=@operation_id,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id AND locked_operation_id=@root_id;";
        await using var update = new SqlCommand(updateSql, connection, transaction);
        update.Parameters.Add("@qty", SqlDbType.Decimal).ConfigureDecimal(after);
        update.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        update.Parameters.Add("@flow_id", SqlDbType.BigInt).Value = flowId;
        update.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operation.Id;
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        update.Parameters.Add("@root_id", SqlDbType.BigInt).Value = operation.RootOperationId;
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("库存对象更新冲突。");
        await UpdateLineAsync(connection, transaction, line, erp, cancellationToken);
    }

    private static async Task<long> InsertStateFlowAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation, MaterialOutboundExecutionLine line,
        decimal before, decimal change, decimal after,
        string? fromWarehouse, string? fromLocation,
        string? toWarehouse, string? toLocation,
        long? previousFlowId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.inventory_flow
                (operation_id,operation_line_id,object_type,label_id,pallet_id,object_sequence_no,
                 flow_role,operation_type,unit_code,qty_before,change_qty,qty_after,
                 material_code,batch_no,manufacture_date,expiry_date,serial_no,
                 inventory_attributes_json,attribute_schema_version,inventory_identity_hash,
                 from_warehouse_code,from_location_code,to_warehouse_code,to_location_code,remark)
            OUTPUT inserted.id
            SELECT @operation_id,@line_id,@object_type,@label_id,@pallet_id,
                   (SELECT ISNULL(MAX(sequence_flow.object_sequence_no),0)+1
                    FROM dbo.inventory_flow sequence_flow WITH (UPDLOCK,HOLDLOCK)
                    WHERE (@label_id IS NOT NULL AND sequence_flow.label_id=@label_id)
                       OR (@pallet_id IS NOT NULL AND sequence_flow.pallet_id=@pallet_id)),
                   'NORMAL',@operation_type,@unit_code,@before,@change,@after,
                   @material_code,@batch_no,previous.manufacture_date,previous.expiry_date,
                   previous.serial_no,previous.inventory_attributes_json,
                   ISNULL(previous.attribute_schema_version,1),previous.inventory_identity_hash,
                   @from_warehouse,@from_location,@to_warehouse,@to_location,@remark
            FROM (SELECT 1 AS marker) seed
            LEFT JOIN dbo.inventory_flow previous ON previous.id=@previous_flow_id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operation.Id;
        command.Parameters.Add("@line_id", SqlDbType.BigInt).Value = line.Id;
        command.Parameters.Add("@object_type", SqlDbType.VarChar, 20).Value = line.ObjectType;
        command.Parameters.Add("@label_id", SqlDbType.BigInt).Value = line.LabelId.HasValue ? line.LabelId.Value : DBNull.Value;
        command.Parameters.Add("@pallet_id", SqlDbType.BigInt).Value = line.PalletId.HasValue ? line.PalletId.Value : DBNull.Value;
        command.Parameters.Add("@previous_flow_id", SqlDbType.BigInt).Value = previousFlowId.HasValue ? previousFlowId.Value : DBNull.Value;
        command.Parameters.Add("@operation_type", SqlDbType.VarChar, 50).Value = operation.OperationType;
        command.Parameters.Add("@unit_code", SqlDbType.NVarChar, 50).Value = line.UnitCode;
        command.Parameters.Add("@before", SqlDbType.Decimal).ConfigureDecimal(before);
        command.Parameters.Add("@change", SqlDbType.Decimal).ConfigureDecimal(change);
        command.Parameters.Add("@after", SqlDbType.Decimal).ConfigureDecimal(after);
        command.Parameters.Add("@material_code", SqlDbType.NVarChar, 100).Value = line.MaterialCode;
        command.Parameters.Add("@batch_no", SqlDbType.NVarChar, 100).Value = DbValue(line.BatchNo, 100);
        command.Parameters.Add("@from_warehouse", SqlDbType.NVarChar, 100).Value = DbValue(fromWarehouse, 100);
        command.Parameters.Add("@from_location", SqlDbType.NVarChar, 100).Value = DbValue(fromLocation, 100);
        command.Parameters.Add("@to_warehouse", SqlDbType.NVarChar, 100).Value = DbValue(toWarehouse, 100);
        command.Parameters.Add("@to_location", SqlDbType.NVarChar, 100).Value = DbValue(toLocation, 100);
        command.Parameters.Add("@remark", SqlDbType.NVarChar, 500).Value = DbValue(line.Remark, 500);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ReleaseObjectAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionOperation operation, MaterialOutboundExecutionLine line,
        CancellationToken cancellationToken)
    {
        var table = line.ObjectType == "PRODUCT_LABEL" ? "product_label" : "pallet";
        var id = line.LabelId ?? line.PalletId ?? throw new InvalidOperationException("库存对象不存在。");
        await using var release = new SqlCommand(
            $"UPDATE dbo.{table} SET updated_at=updated_at WHERE id=@id AND locked_operation_id=@root_id;",
            connection, transaction);
        release.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        release.Parameters.Add("@root_id", SqlDbType.BigInt).Value = operation.RootOperationId;
        if (await release.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("库存对象锁已变化。");
    }

    private static async Task ReleaseGroupLocksWhenSuccessfulAsync(
        SqlConnection connection, SqlTransaction transaction,
        string groupId, long rootOperationId,
        CancellationToken cancellationToken)
    {
        await using var pending = new SqlCommand("""
            SELECT COUNT(*) FROM dbo.business_operation
            WHERE submission_group_id=@group_id AND status<>'SUCCESS';
            """, connection, transaction);
        pending.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        if (Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken)) != 0) return;
        await ReleaseRootLocksAsync(connection, transaction, rootOperationId, cancellationToken);
    }

    private static async Task ReleaseRootLocksAsync(
        SqlConnection connection, SqlTransaction transaction,
        long rootOperationId,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "product_label", "pallet" })
        {
            await using var release = new SqlCommand(
                $"UPDATE dbo.{table} SET locked_operation_id=NULL,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE locked_operation_id=@root_id;",
                connection, transaction);
            release.Parameters.Add("@root_id", SqlDbType.BigInt).Value = rootOperationId;
            await release.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdateLineAsync(
        SqlConnection connection, SqlTransaction transaction,
        MaterialOutboundExecutionLine line,
        (string? LineId, string? PositionId) erp,
        CancellationToken cancellationToken)
    {
        await using var update = new SqlCommand("""
            UPDATE dbo.business_operation_line
            SET line_status='SUCCESS',erp_line_id=@erp_line_id,erp_position_id=@erp_position_id,
                error_code=NULL,error_message=NULL,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@id AND line_status='PENDING';
            """, connection, transaction);
        update.Parameters.Add("@erp_line_id", SqlDbType.NVarChar, 100).Value = DbValue(erp.LineId, 100);
        update.Parameters.Add("@erp_position_id", SqlDbType.NVarChar, 100).Value = DbValue(erp.PositionId, 100);
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = line.Id;
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("业务明细状态已变化。");
    }

    private static async Task LinkGeneratedLabelAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long operationId,
        long lineId,
        long labelId,
        CancellationToken cancellationToken)
    {
        await using var update = new SqlCommand(
            "UPDATE dbo.business_operation_line SET generated_label_id=@label_id WHERE id=@line_id AND operation_id=@operation_id;",
            connection, transaction);
        update.Parameters.Add("@label_id", SqlDbType.BigInt).Value = labelId;
        update.Parameters.Add("@line_id", SqlDbType.BigInt).Value = lineId;
        update.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("新标签与业务明细关联失败。");
    }

    private static async Task SetDraftCompletedWhenTerminalAsync(
        SqlConnection connection, SqlTransaction transaction,
        string groupId, string userId, string requestId,
        CancellationToken cancellationToken)
    {
        await using var active = new SqlCommand("""
            SELECT COUNT_BIG(1),
                   SUM(CASE WHEN status IN
                       ('DRAFT','PENDING','WRITING_ERP','PENDING_APPROVAL','NEEDS_RECONCILIATION')
                       THEN 1 ELSE 0 END),
                   SUM(CASE WHEN status='FAILED' THEN 1 ELSE 0 END)
            FROM dbo.business_operation
            WHERE submission_group_id=@group_id;
            """, connection, transaction);
        active.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        long total;
        long activeCount;
        long failedCount;
        await using (var reader = await active.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return;
            total = reader.GetInt64(0);
            activeCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            failedCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        }
        if (total == 0 || activeCount != 0) return;
        await DocumentDraftLifecycle.SetStatusAsync(
            connection, transaction, groupId,
            total == failedCount ? "DRAFT" : "COMPLETED",
            userId, requestId, cancellationToken);
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

    private static object DbValue(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : Limit(value.Trim(), maxLength);

    private static string Limit(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty :
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed class OperationBuilder(
        long id, long rootOperationId, string groupId, string clientRequestId,
        string operatorId, string operationType, string erpDocumentType, string status, string erpStatus,
        string? erpDocumentNo, string? erpVersionAfter, string approvalStatus,
        string sourceDocumentType, string sourceDocumentId, string sourceDocumentNo,
        string warehouseCode, string? deviceId, string? deviceModel,
        string? reason, string archiveRelativePath, string archiveSha256)
    {
        public List<MaterialOutboundExecutionLine> Lines { get; } = [];

        public MaterialOutboundExecutionOperation Build() => new(
            id, rootOperationId, groupId, clientRequestId, operatorId, operationType,
            erpDocumentType, status, erpStatus, erpDocumentNo, erpVersionAfter, approvalStatus, sourceDocumentType,
            sourceDocumentId, sourceDocumentNo, warehouseCode, deviceId, deviceModel,
            reason, archiveRelativePath, archiveSha256, Lines);
    }
}

internal static class SqlParameterDecimalExtensions
{
    public static SqlParameter ConfigureDecimal(this SqlParameter parameter, decimal value)
    {
        parameter.Precision = 18;
        parameter.Scale = 6;
        parameter.Value = value;
        return parameter;
    }
}

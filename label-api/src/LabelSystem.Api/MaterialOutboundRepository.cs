using System.Data;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class MaterialOutboundRepository(SqlConnectionFactory connections)
{
    private sealed record ResolvedLine(
        MaterialOutboundAllocationRequest Request,
        long? LabelId,
        long? PalletId,
        long? SourcePalletId,
        decimal InventoryQty,
        string BatchNo,
        string WarehouseCode,
        string LocationCode);

    public async Task<MaterialOutboundPrepareResult> PrepareAsync(
        MaterialOutboundPrepareRequest request,
        string payloadHash,
        ArchiveResult archive,
        bool allowPalletizedDirectUse,
        LabelSystemOptions options,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var existing = await ReadExistingAsync(connection, transaction, request.ClientRequestId, payloadHash, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            await using var draftTransaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await DocumentDraftLifecycle.SetStatusAsync(
                connection, draftTransaction, request.ClientRequestId.Trim(),
                DocumentDraftLifecycle.FromOperationStatus(existing.Status),
                request.OperatorId.Trim(), requestId, cancellationToken);
            await draftTransaction.CommitAsync(cancellationToken);
            return existing;
        }

        await ReserveSourceLinesAsync(connection, transaction, request, cancellationToken);

        var productSnapshots = new Dictionary<string, ProductLabelSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in request.Lines
                     .Where(line => line.ObjectType == "PRODUCT_LABEL")
                     .Select(line => line.LabelCode.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = await LabelSystemRepository.ReadProductLabelAsync(
                connection, transaction, code, cancellationToken, true)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "LABEL_NOT_FOUND", $"未找到标签：{code}");
            if (snapshot.Status != "ACTIVE")
                throw new ApiException(StatusCodes.Status409Conflict, "LABEL_NOT_ACTIVE", $"标签不可用：{code}");
            if (snapshot.LockedOperationId.HasValue)
                throw new ApiException(StatusCodes.Status409Conflict, "LABEL_LOCKED", $"标签处理中：{code}");
            productSnapshots[code] = snapshot;
        }

        var palletCodes = request.Lines
            .Where(line => line.ObjectType == "PALLET")
            .Select(line => line.LabelCode.Trim())
            .Concat(productSnapshots.Values.Where(item => item.PalletCode is not null).Select(item => item.PalletCode!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var palletSnapshots = new Dictionary<string, PalletSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in palletCodes)
        {
            var snapshot = await LabelSystemRepository.ReadPalletAsync(
                connection, transaction, code, cancellationToken, true)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "PALLET_NOT_FOUND", $"未找到托盘：{code}");
            if (snapshot.Line.Status != "ACTIVE")
                throw new ApiException(StatusCodes.Status409Conflict, "PALLET_NOT_ACTIVE", $"托盘不可用：{code}");
            if (snapshot.LockedOperationId.HasValue)
                throw new ApiException(StatusCodes.Status409Conflict, "PALLET_LOCKED", $"托盘处理中：{code}");
            palletSnapshots[code] = snapshot;
        }

        var resolved = ResolveLines(request.Lines, productSnapshots, palletSnapshots, allowPalletizedDirectUse);
        ValidateObjectQuantities(resolved);

        var now = BusinessClock.NowDatabase;
        var lockExpiresAt = now.AddMinutes(Math.Clamp(options.LockMinutes, 1, 120));
        var submissionGroupId = request.ClientRequestId.Trim();
        var groups = resolved
            .GroupBy(line => new
            {
                line.Request.SourceDocumentId,
                line.Request.SourceDocumentNo,
                line.WarehouseCode
            })
            .OrderBy(group => group.Key.SourceDocumentId)
            .ThenBy(group => group.Key.SourceDocumentNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var created = new List<(long Id, string OperationNo, string SourceDocumentNo, string WarehouseCode, ResolvedLine[] Lines)>();

        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var operationNo = $"MO-{now:yyMMddHHmmss}-{Guid.NewGuid():N}"[..32];
            var clientRequestId = groups.Length == 1 ? submissionGroupId : $"{submissionGroupId}:{index + 1}";
            var sourceDocumentId = group.First().Request.SourceDocumentId.ToString();
            var operationId = await InsertOperationAsync(
                connection, transaction, operationNo, submissionGroupId, clientRequestId,
                sourceDocumentId, group.Key.SourceDocumentNo, request, payloadHash, archive,
                options, now, lockExpiresAt, cancellationToken);
            created.Add((operationId, operationNo, group.Key.SourceDocumentNo, group.Key.WarehouseCode, group.ToArray()));
        }

        var rootOperationId = created.Min(item => item.Id);
        foreach (var operation in created)
        {
            await InsertLinesAsync(connection, transaction, operation.Id, operation.Lines, cancellationToken);
            await InsertEventAsync(connection, transaction, operation.Id, 1, "REQUEST_RECEIVED", "SUCCESS",
                requestId, "已接收材料出库请求。", cancellationToken);
            await InsertEventAsync(connection, transaction, operation.Id, 2, "DATABASE_COMMITTED", "SUCCESS",
                requestId, "已创建待处理业务，U8 尚未写入。", cancellationToken);
        }

        await ApplyLocksAsync(
            connection, transaction, rootOperationId,
            productSnapshots.Values.Select(item => item.Id),
            palletSnapshots.Values.Select(item => item.Id),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await using (var draftTransaction = (SqlTransaction)await connection.BeginTransactionAsync(
                         IsolationLevel.ReadCommitted, cancellationToken))
        {
            await DocumentDraftLifecycle.SetStatusAsync(
                connection, draftTransaction, submissionGroupId, "PREPARED",
                request.OperatorId.Trim(), requestId, cancellationToken);
            await draftTransaction.CommitAsync(cancellationToken);
        }
        return new MaterialOutboundPrepareResult(
            submissionGroupId,
            "PENDING",
            options.U8WritesEnabled,
            created.Select(item => new PreparedOperation(
                item.Id, item.OperationNo, item.SourceDocumentNo, item.WarehouseCode,
                item.Lines.Length, item.Lines.Sum(line => line.Request.RequestedQty))).ToArray());
    }

    public async Task<OperationGroupResult> GetGroupAsync(
        string submissionGroupId,
        string userId,
        bool manageAll,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        var operations = await ReadOperationsAsync(connection, null, submissionGroupId, cancellationToken);
        if (operations.Count == 0)
            throw new ApiException(StatusCodes.Status404NotFound, "OPERATION_NOT_FOUND", "业务操作不存在。");
        EnsureOperationAccess(operations, userId, manageAll);
        return ToGroupResult(submissionGroupId, operations);
    }

    public async Task<OperationGroupListResult> ListGroupsAsync(
        string userId,
        bool allUsers,
        string? documentType,
        string? status,
        string? operatorId,
        DateOnly? startDate,
        DateOnly? endDate,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var operationType = OperationTypeForDocument(documentType);
        var normalizedStatus = NormalizeStatus(status);
        if (startDate.HasValue && endDate.HasValue && startDate > endDate)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DATE_RANGE_INVALID", "日期范围无效。");

        const string sql = """
            WITH eligible_groups AS
            (
                SELECT op.submission_group_id, MAX(op.updated_at) AS group_updated_at
                FROM dbo.business_operation AS op
                WHERE op.submission_group_id IS NOT NULL
                  AND (@all_users=1 OR op.operator_id=@user_id)
                  AND (@operator_id IS NULL OR op.operator_id=@operator_id)
                  AND (@operation_type IS NULL OR op.operation_type=@operation_type)
                  AND (@start_at IS NULL OR op.created_at>=@start_at)
                  AND (@end_at IS NULL OR op.created_at<@end_at)
                GROUP BY op.submission_group_id
                HAVING @status IS NULL OR SUM(CASE WHEN op.status=@status THEN 1 ELSE 0 END)>0
            ),
            paged_groups AS
            (
                SELECT submission_group_id, group_updated_at, COUNT(1) OVER() AS total_count
                FROM eligible_groups
                ORDER BY group_updated_at DESC, submission_group_id DESC
                OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            )
            SELECT pg.total_count, op.submission_group_id,
                   op.id, op.operation_no, COALESCE(op.source_document_no,N''),
                   COALESCE(MAX(line.from_warehouse_code),MAX(line.to_warehouse_code),N''), op.operator_id,
                   op.operator_name_snapshot, op.operation_type, op.status, op.erp_status,
                   op.approval_status, op.erp_document_no, op.request_payload_hash,
                   op.lock_expires_at, op.error_code, op.error_message,
                   op.created_at, op.updated_at, op.completed_at,
                   COUNT(line.id), COALESCE(SUM(line.requested_qty),0),
                   COALESCE((SELECT MAX(evt.sequence_no) FROM dbo.business_operation_event evt WHERE evt.operation_id=op.id),0)
            FROM paged_groups AS pg
            INNER JOIN dbo.business_operation AS op ON op.submission_group_id=pg.submission_group_id
            LEFT JOIN dbo.business_operation_line AS line ON line.operation_id=op.id
            GROUP BY pg.total_count, pg.group_updated_at, op.submission_group_id,
                     op.id, op.operation_no, op.source_document_no, op.operator_id,
                     op.operator_name_snapshot, op.operation_type, op.status, op.erp_status,
                     op.approval_status, op.erp_document_no, op.request_payload_hash,
                     op.lock_expires_at, op.error_code, op.error_message,
                     op.created_at, op.updated_at, op.completed_at
            ORDER BY pg.group_updated_at DESC, op.submission_group_id DESC, op.id;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@all_users", SqlDbType.Bit).Value = allUsers;
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@operator_id", SqlDbType.NVarChar, 100).Value = DbValue(operatorId);
        command.Parameters.Add("@operation_type", SqlDbType.VarChar, 50).Value = DbValue(operationType);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = DbValue(normalizedStatus);
        command.Parameters.Add("@start_at", SqlDbType.DateTime2).Value = startDate.HasValue
            ? startDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        command.Parameters.Add("@end_at", SqlDbType.DateTime2).Value = endDate.HasValue
            ? endDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        command.Parameters.Add("@skip", SqlDbType.Int).Value = skip;
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var total = 0;
        var rows = new List<OperationRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            total = reader.GetInt32(0);
            rows.Add(ReadOperationRow(reader, 1));
        }
        var items = rows.GroupBy(row => row.SubmissionGroupId, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToGroupResult(group.Key, group.ToArray()))
            .OrderByDescending(group => group.UpdatedAt)
            .ThenByDescending(group => group.SubmissionGroupId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new OperationGroupListResult(skip, take, total, items);
    }

    public async Task<OperationGroupResult> HeartbeatAsync(
        string submissionGroupId,
        int lockMinutes,
        string userId,
        bool manageAll,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE dbo.business_operation
            SET heartbeat_at=DATEADD(HOUR, 8, SYSUTCDATETIME()),
                lock_expires_at=DATEADD(MINUTE, @minutes, DATEADD(HOUR, 8, SYSUTCDATETIME())),
                updated_at=DATEADD(HOUR, 8, SYSUTCDATETIME())
            WHERE submission_group_id=@group_id
              AND (@manage_all=1 OR operator_id=@user_id)
              AND status='PENDING'
              AND lock_expires_at >= DATEADD(HOUR, 8, SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@minutes", SqlDbType.Int).Value = Math.Clamp(lockMinutes, 1, 120);
        command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = submissionGroupId;
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@manage_all", SqlDbType.Bit).Value = manageAll;
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            var operations = await ReadOperationsAsync(connection, null, submissionGroupId, cancellationToken);
            if (operations.Count > 0) EnsureOperationAccess(operations, userId, manageAll);
            throw new ApiException(StatusCodes.Status409Conflict, "OPERATION_NOT_PENDING", "业务操作不能续期。");
        }
        return await GetGroupAsync(submissionGroupId, userId, manageAll, cancellationToken);
    }

    public async Task<OperationGroupResult> CancelAsync(
        string submissionGroupId,
        string requestId,
        string userId,
        bool manageAll,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var operations = await ReadOperationsAsync(connection, transaction, submissionGroupId, cancellationToken, true);
        if (operations.Count == 0)
            throw new ApiException(StatusCodes.Status404NotFound, "OPERATION_NOT_FOUND", "业务操作不存在。");
        EnsureOperationAccess(operations, userId, manageAll);
        if (operations.All(item => item.Status == "CANCELLED"))
        {
            await transaction.CommitAsync(cancellationToken);
            await using var draftTransaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await DocumentDraftLifecycle.SetStatusAsync(
                connection, draftTransaction, submissionGroupId, "CANCELLED",
                userId, requestId, cancellationToken);
            await draftTransaction.CommitAsync(cancellationToken);
            return await GetGroupAsync(submissionGroupId, userId, manageAll, cancellationToken);
        }
        if (operations.Any(item => item.Status != "PENDING" || item.ErpStatus is not ("PENDING" or "NOT_REQUIRED")))
            throw new ApiException(StatusCodes.Status409Conflict, "OPERATION_CANNOT_CANCEL", "当前状态不能取消。");

        var rootOperationId = operations.Min(item => item.Id);
        await using (var releaseLabels = new SqlCommand(
                         "UPDATE dbo.product_label SET locked_operation_id=NULL, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE locked_operation_id=@id;",
                         connection, transaction))
        {
            releaseLabels.Parameters.Add("@id", SqlDbType.BigInt).Value = rootOperationId;
            await releaseLabels.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var releasePallets = new SqlCommand(
                         "UPDATE dbo.pallet SET locked_operation_id=NULL, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE locked_operation_id=@id;",
                         connection, transaction))
        {
            releasePallets.Parameters.Add("@id", SqlDbType.BigInt).Value = rootOperationId;
            await releasePallets.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var cancel = new SqlCommand("""
            UPDATE dbo.business_operation
            SET status='CANCELLED', erp_status='NOT_REQUIRED', lock_acquired_at=NULL,
                heartbeat_at=NULL, lock_expires_at=NULL,
                error_code='USER_CANCELLED', error_message=N'用户取消待处理业务。',
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()), completed_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE submission_group_id=@group_id;
            """, connection, transaction))
        {
            cancel.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = submissionGroupId;
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var operation in operations)
        {
            await InsertEventAsync(connection, transaction, operation.Id, operation.MaxEventSequence + 1,
                "CANCELLED", "SUCCESS", requestId, "已取消待处理业务并释放标签锁。", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await using (var draftTransaction = (SqlTransaction)await connection.BeginTransactionAsync(
                         IsolationLevel.ReadCommitted, cancellationToken))
        {
            await DocumentDraftLifecycle.SetStatusAsync(
                connection, draftTransaction, submissionGroupId, "CANCELLED",
                userId, requestId, cancellationToken);
            await draftTransaction.CommitAsync(cancellationToken);
        }
        return await GetGroupAsync(submissionGroupId, userId, manageAll, cancellationToken);
    }

    private static ResolvedLine[] ResolveLines(
        IReadOnlyList<MaterialOutboundAllocationRequest> lines,
        IReadOnlyDictionary<string, ProductLabelSnapshot> products,
        IReadOnlyDictionary<string, PalletSnapshot> pallets,
        bool allowPalletizedDirectUse)
    {
        var result = new List<ResolvedLine>(lines.Count);
        foreach (var line in lines)
        {
            if (line.ObjectType == "PRODUCT_LABEL")
            {
                var product = products[line.LabelCode.Trim()];
                if (!string.Equals(product.MaterialCode, line.MaterialCode, StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(StatusCodes.Status409Conflict, "LABEL_MATERIAL_MISMATCH", $"标签料号不一致：{line.LabelCode}");
                if (!string.Equals(product.UnitCode, line.UnitCode, StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(StatusCodes.Status409Conflict, "LABEL_UNIT_MISMATCH", $"标签单位不一致：{line.LabelCode}");
                if (string.IsNullOrWhiteSpace(product.WarehouseCode))
                    throw new ApiException(StatusCodes.Status409Conflict, "LABEL_LOCATION_MISSING", $"标签没有当前仓库：{line.LabelCode}");
                if (product.PalletId.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(line.SourcePalletCode) && !allowPalletizedDirectUse)
                        throw new ApiException(StatusCodes.Status409Conflict, "PALLETIZED_LABEL_DIRECT_USE_DISABLED", $"标签已装托：{line.LabelCode}");
                    if (!string.IsNullOrWhiteSpace(line.SourcePalletCode) &&
                        !string.Equals(product.PalletCode, line.SourcePalletCode, StringComparison.OrdinalIgnoreCase))
                        throw new ApiException(StatusCodes.Status409Conflict, "PALLET_RELATION_CHANGED", $"标签托盘关系已变化：{line.LabelCode}");
                }
                result.Add(new ResolvedLine(
                    line, product.Id, null, product.PalletId, product.CurrentQty,
                    product.BatchNo, product.WarehouseCode, product.LocationCode));
            }
            else
            {
                var pallet = pallets[line.LabelCode.Trim()];
                if (!string.Equals(pallet.Line.MaterialCode, line.MaterialCode, StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(StatusCodes.Status409Conflict, "PALLET_MATERIAL_MISMATCH", $"托盘料号不一致：{line.LabelCode}");
                if (!string.Equals(pallet.Line.UnitCode, line.UnitCode, StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(StatusCodes.Status409Conflict, "PALLET_UNIT_MISMATCH", $"托盘单位不一致：{line.LabelCode}");
                if (string.IsNullOrWhiteSpace(pallet.Line.WarehouseCode))
                    throw new ApiException(StatusCodes.Status409Conflict, "PALLET_LOCATION_MISSING", $"托盘没有当前仓库：{line.LabelCode}");
                result.Add(new ResolvedLine(
                    line, null, pallet.Id, null, pallet.Line.AvailableQty,
                    pallet.Line.BatchNo, pallet.Line.WarehouseCode, pallet.Line.LocationCode));
            }
        }
        return result.ToArray();
    }

    private static void ValidateObjectQuantities(IReadOnlyList<ResolvedLine> lines)
    {
        foreach (var group in lines.GroupBy(line => (
                     ObjectType: line.Request.ObjectType,
                     LabelCode: line.Request.LabelCode.ToUpperInvariant())))
        {
            var requested = group.Sum(line => line.Request.RequestedQty);
            var available = group.First().InventoryQty;
            if (requested > available)
                throw new ApiException(StatusCodes.Status409Conflict, "LABEL_QUANTITY_INSUFFICIENT", $"标签数量不足：{group.Key.LabelCode}");
            if (group.Key.ObjectType == "PALLET" && requested != 1)
                throw new ApiException(StatusCodes.Status409Conflict, "PALLET_QUANTITY_INVALID", "托盘数量必须为 1。");
        }
    }

    private static async Task ReserveSourceLinesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MaterialOutboundPrepareRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) op.submission_group_id
            FROM dbo.business_operation_line AS line WITH (UPDLOCK, HOLDLOCK, INDEX(IX_business_operation_line_source_line))
            INNER JOIN dbo.business_operation AS op WITH (UPDLOCK, HOLDLOCK)
                ON op.id = line.operation_id
            WHERE line.source_line_id = @source_line_id
              AND op.source_document_type = 'production-order'
              AND op.status IN ('DRAFT','PENDING','WRITING_ERP','PENDING_APPROVAL','NEEDS_RECONCILIATION')
              AND op.submission_group_id <> @group_id;
            """;
        foreach (var sourceLineId in request.Lines.Select(line => line.SourceLineId).Distinct().Order())
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@source_line_id", SqlDbType.NVarChar, 100).Value = sourceLineId.ToString();
            command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = request.ClientRequestId.Trim();
            if (await command.ExecuteScalarAsync(cancellationToken) is not null)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "SOURCE_LINE_RESERVED", "领料明细已被其他未完成单据占用。");
        }
    }

    private static async Task<long> InsertOperationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string operationNo,
        string submissionGroupId,
        string clientRequestId,
        string sourceDocumentId,
        string sourceDocumentNo,
        MaterialOutboundPrepareRequest request,
        string payloadHash,
        ArchiveResult archive,
        LabelSystemOptions options,
        DateTime now,
        DateTime lockExpiresAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.business_operation
            (
                operation_no, submission_group_id, operation_type, status,
                source_document_type, source_document_id, source_document_no,
                erp_environment, erp_account_id, erp_year, erp_database_name_snapshot,
                erp_document_type, erp_operation_type, erp_status,
                operator_id, operator_name_snapshot, device_id, device_code, device_model,
                client_request_id, request_payload_hash, approval_status, reason,
                lock_acquired_at, heartbeat_at, lock_expires_at,
                archive_relative_path, archive_status, archive_sha256
            )
            OUTPUT inserted.id
            VALUES
            (
                @operation_no, @submission_group_id, 'MATERIAL_OUTBOUND', 'PENDING',
                'production-order', @source_document_id, @source_document_no,
                @erp_environment, @erp_account_id, @erp_year, @erp_database_name,
                'material-outbound', 'ADD', 'PENDING',
                @operator_id, @operator_name, @device_id, @device_code, @device_model,
                @client_request_id, @payload_hash, 'NOT_REQUIRED', @reason,
                @now, @now, @lock_expires_at,
                @archive_path, 'COMPLETE', @archive_sha256
            );
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_no", SqlDbType.NVarChar, 50).Value = operationNo;
        command.Parameters.Add("@submission_group_id", SqlDbType.VarChar, 100).Value = submissionGroupId;
        command.Parameters.Add("@source_document_id", SqlDbType.NVarChar, 100).Value = sourceDocumentId;
        command.Parameters.Add("@source_document_no", SqlDbType.NVarChar, 100).Value = sourceDocumentNo;
        command.Parameters.Add("@erp_environment", SqlDbType.VarChar, 20).Value = options.Environment;
        command.Parameters.Add("@erp_account_id", SqlDbType.NVarChar, 20).Value = options.ErpAccountId;
        command.Parameters.Add("@erp_year", SqlDbType.SmallInt).Value = options.ErpYear;
        command.Parameters.Add("@erp_database_name", SqlDbType.NVarChar, 128).Value = options.ErpDatabaseName;
        command.Parameters.Add("@operator_id", SqlDbType.NVarChar, 100).Value = request.OperatorId.Trim();
        command.Parameters.Add("@operator_name", SqlDbType.NVarChar, 100).Value = request.OperatorName.Trim();
        command.Parameters.Add("@device_id", SqlDbType.NVarChar, 100).Value = DbValue(request.DeviceId);
        command.Parameters.Add("@device_code", SqlDbType.NVarChar, 100).Value = DbValue(request.DeviceCode);
        command.Parameters.Add("@device_model", SqlDbType.NVarChar, 100).Value = DbValue(request.DeviceModel);
        command.Parameters.Add("@client_request_id", SqlDbType.VarChar, 100).Value = clientRequestId;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value = payloadHash;
        command.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = DbValue(request.Reason);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = now;
        command.Parameters.Add("@lock_expires_at", SqlDbType.DateTime2).Value = lockExpiresAt;
        command.Parameters.Add("@archive_path", SqlDbType.NVarChar, 500).Value = archive.RelativePath;
        command.Parameters.Add("@archive_sha256", SqlDbType.Char, 64).Value = archive.Sha256;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertLinesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long operationId,
        IReadOnlyList<ResolvedLine> lines,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.business_operation_line
            (
                operation_id, line_no, line_status,
                label_id, pallet_id, selection_source, source_pallet_id, inventory_qty_snapshot,
                source_line_id, source_line_no, material_code, batch_no, unit_code, requested_qty,
                from_warehouse_code, from_location_code, remark
            )
            VALUES
            (
                @operation_id, @line_no, 'PENDING',
                @label_id, @pallet_id, @selection_source, @source_pallet_id, @inventory_qty,
                @source_line_id, @source_line_no, @material_code, @batch_no, @unit_code, @requested_qty,
                @warehouse_code, @location_code, @remark
            );
            """;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
            command.Parameters.Add("@line_no", SqlDbType.Int).Value = index + 1;
            command.Parameters.Add("@label_id", SqlDbType.BigInt).Value = line.LabelId.HasValue ? line.LabelId.Value : DBNull.Value;
            command.Parameters.Add("@pallet_id", SqlDbType.BigInt).Value = line.PalletId.HasValue ? line.PalletId.Value : DBNull.Value;
            command.Parameters.Add("@selection_source", SqlDbType.VarChar, 20).Value =
                line.SourcePalletId.HasValue || line.PalletId.HasValue ? "PALLET_SCAN" : "LABEL_SCAN";
            command.Parameters.Add("@source_pallet_id", SqlDbType.BigInt).Value =
                line.SourcePalletId.HasValue ? line.SourcePalletId.Value : DBNull.Value;
            command.Parameters.Add("@inventory_qty", SqlDbType.Decimal).Value = line.InventoryQty;
            command.Parameters["@inventory_qty"].Precision = 18;
            command.Parameters["@inventory_qty"].Scale = 6;
            command.Parameters.Add("@source_line_id", SqlDbType.NVarChar, 100).Value = line.Request.SourceLineId.ToString();
            command.Parameters.Add("@source_line_no", SqlDbType.NVarChar, 50).Value =
                line.Request.SourceLineNo.HasValue ? line.Request.SourceLineNo.Value.ToString() : DBNull.Value;
            command.Parameters.Add("@material_code", SqlDbType.NVarChar, 100).Value = line.Request.MaterialCode;
            command.Parameters.Add("@batch_no", SqlDbType.NVarChar, 100).Value = DbValue(line.BatchNo);
            command.Parameters.Add("@unit_code", SqlDbType.NVarChar, 50).Value = line.Request.UnitCode;
            command.Parameters.Add("@requested_qty", SqlDbType.Decimal).Value = line.Request.RequestedQty;
            command.Parameters["@requested_qty"].Precision = 18;
            command.Parameters["@requested_qty"].Scale = 6;
            command.Parameters.Add("@warehouse_code", SqlDbType.NVarChar, 100).Value = line.WarehouseCode;
            command.Parameters.Add("@location_code", SqlDbType.NVarChar, 100).Value = DbValue(line.LocationCode);
            command.Parameters.Add("@remark", SqlDbType.NVarChar, 500).Value = DbValue(line.Request.Remark);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ApplyLocksAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long rootOperationId,
        IEnumerable<long> labelIds,
        IEnumerable<long> palletIds,
        CancellationToken cancellationToken)
    {
        foreach (var id in labelIds.Distinct().Order())
        {
            await using var command = new SqlCommand("""
                UPDATE dbo.product_label
                SET locked_operation_id=@operation_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE id=@id AND locked_operation_id IS NULL;
                """, connection, transaction);
            command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = rootOperationId;
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ApiException(StatusCodes.Status409Conflict, "LABEL_LOCKED", "标签正在处理中。");
        }
        foreach (var id in palletIds.Distinct().Order())
        {
            await using var command = new SqlCommand("""
                UPDATE dbo.pallet
                SET locked_operation_id=@operation_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE id=@id AND locked_operation_id IS NULL;
                """, connection, transaction);
            command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = rootOperationId;
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ApiException(StatusCodes.Status409Conflict, "PALLET_LOCKED", "托盘正在处理中。");
        }
    }

    private static async Task InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long operationId,
        int sequence,
        string eventType,
        string result,
        string requestId,
        string message,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.business_operation_event
                (operation_id, sequence_no, stage, source, event_type, result, request_id, message)
            VALUES
                (@operation_id, @sequence_no, @stage, 'BUSINESS_API', @event_type, @result, @request_id, @message);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
        command.Parameters.Add("@sequence_no", SqlDbType.Int).Value = sequence;
        command.Parameters.Add("@stage", SqlDbType.VarChar, 50).Value = eventType;
        command.Parameters.Add("@event_type", SqlDbType.VarChar, 50).Value = eventType;
        command.Parameters.Add("@result", SqlDbType.VarChar, 20).Value = result;
        command.Parameters.Add("@request_id", SqlDbType.VarChar, 100).Value = requestId;
        command.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = message;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MaterialOutboundPrepareResult?> ReadExistingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string groupId,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var rows = await ReadOperationsAsync(connection, transaction, groupId, cancellationToken, true);
        if (rows.Count == 0) return null;
        if (rows.Any(row => !string.Equals(row.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(StatusCodes.Status409Conflict, "IDEMPOTENCY_CONFLICT", "请求编号已被其他内容使用。");
        var result = ToGroupResult(groupId, rows);
        return new MaterialOutboundPrepareResult(groupId, result.Status, false, result.Operations);
    }

    private sealed record OperationRow(
        string SubmissionGroupId,
        long Id, string OperationNo, string SourceDocumentNo, string WarehouseCode,
        string OperatorId, string OperatorName, string OperationType,
        string Status, string ErpStatus, string ApprovalStatus, string? ErpDocumentNo,
        string? PayloadHash, DateTime? LockExpiresAt, string? ErrorCode, string? ErrorMessage,
        DateTime CreatedAt, DateTime UpdatedAt, DateTime? CompletedAt,
        int LineCount, decimal TotalQty, int MaxEventSequence);

    private static async Task<List<OperationRow>> ReadOperationsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string groupId,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        var hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT op.submission_group_id, op.id, op.operation_no, COALESCE(op.source_document_no,N''),
                   COALESCE(MAX(line.from_warehouse_code),MAX(line.to_warehouse_code),N''), op.operator_id,
                   op.operator_name_snapshot, op.operation_type, op.status, op.erp_status,
                   op.approval_status, op.erp_document_no,
                   op.request_payload_hash, op.lock_expires_at, op.error_code, op.error_message,
                   op.created_at, op.updated_at, op.completed_at,
                   COUNT(line.id), COALESCE(SUM(line.requested_qty),0),
                   COALESCE((SELECT MAX(evt.sequence_no) FROM dbo.business_operation_event evt WHERE evt.operation_id=op.id),0)
            FROM dbo.business_operation AS op{hint}
            LEFT JOIN dbo.business_operation_line AS line ON line.operation_id=op.id
            WHERE op.submission_group_id=@group_id
            GROUP BY op.submission_group_id, op.id, op.operation_no, op.source_document_no,
                     op.operator_id, op.operator_name_snapshot, op.operation_type, op.status, op.erp_status,
                     op.approval_status, op.erp_document_no, op.request_payload_hash, op.lock_expires_at,
                     op.error_code, op.error_message, op.created_at, op.updated_at, op.completed_at
            ORDER BY op.id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OperationRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadOperationRow(reader));
        }
        return rows;
    }

    private static OperationGroupResult ToGroupResult(string groupId, IReadOnlyList<OperationRow> rows)
    {
        var status = AggregateStatus(rows);
        var terminal = rows.All(row => IsTerminalStatus(row.Status));
        var expires = rows.Where(row => row.LockExpiresAt.HasValue)
            .Select(row => row.LockExpiresAt!.Value).DefaultIfEmpty().Min();
        var sourceDocumentNo = string.Join("、", rows.Select(row => row.SourceDocumentNo)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
        var error = rows.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.ErrorCode) ||
                                               !string.IsNullOrWhiteSpace(row.ErrorMessage));
        return new OperationGroupResult(
            groupId,
            DocumentTypeForOperation(rows[0].OperationType),
            status,
            terminal,
            rows[0].OperatorId,
            rows[0].OperatorName,
            sourceDocumentNo,
            rows.Sum(row => row.LineCount),
            rows.Sum(row => row.TotalQty),
            expires == default ? null : new DateTimeOffset(expires, BusinessClock.BeijingOffset),
            ToOffset(rows.Min(row => row.CreatedAt)),
            ToOffset(rows.Max(row => row.UpdatedAt)),
            terminal && rows.All(row => row.CompletedAt.HasValue)
                ? ToOffset(rows.Max(row => row.CompletedAt!.Value)) : null,
            error?.ErrorCode,
            error?.ErrorMessage,
            rows.Select(row => new PreparedOperation(
                row.Id, row.OperationNo, row.SourceDocumentNo, row.WarehouseCode,
                row.LineCount, row.TotalQty, row.Status, row.ErpStatus, row.ApprovalStatus,
                row.ErpDocumentNo, row.ErrorCode, row.ErrorMessage,
                ToOffset(row.CreatedAt), ToOffset(row.UpdatedAt),
                row.CompletedAt.HasValue ? ToOffset(row.CompletedAt.Value) : null)).ToArray());
    }

    private static OperationRow ReadOperationRow(SqlDataReader reader, int offset = 0) => new(
        reader.GetString(offset), reader.GetInt64(offset + 1), reader.GetString(offset + 2),
        reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetString(offset + 5),
        reader.GetString(offset + 6), reader.GetString(offset + 7), reader.GetString(offset + 8),
        reader.GetString(offset + 9), reader.GetString(offset + 10),
        reader.IsDBNull(offset + 11) ? null : reader.GetString(offset + 11),
        reader.IsDBNull(offset + 12) ? null : reader.GetString(offset + 12),
        reader.IsDBNull(offset + 13) ? null : reader.GetDateTime(offset + 13),
        reader.IsDBNull(offset + 14) ? null : reader.GetString(offset + 14),
        reader.IsDBNull(offset + 15) ? null : reader.GetString(offset + 15),
        reader.GetDateTime(offset + 16), reader.GetDateTime(offset + 17),
        reader.IsDBNull(offset + 18) ? null : reader.GetDateTime(offset + 18),
        reader.GetInt32(offset + 19), reader.GetDecimal(offset + 20), reader.GetInt32(offset + 21));

    private static string AggregateStatus(IReadOnlyList<OperationRow> rows)
    {
        var statuses = rows.Select(row => row.Status).Distinct(StringComparer.Ordinal).ToArray();
        if (statuses.Length == 1) return statuses[0];
        if (statuses.Contains("NEEDS_RECONCILIATION", StringComparer.Ordinal)) return "NEEDS_RECONCILIATION";
        if (statuses.Any(status => !IsTerminalStatus(status)))
        {
            if (statuses.Contains("WRITING_ERP", StringComparer.Ordinal)) return "WRITING_ERP";
            if (statuses.Contains("PENDING_APPROVAL", StringComparer.Ordinal)) return "PENDING_APPROVAL";
        }
        return "PARTIAL";
    }

    private static bool IsTerminalStatus(string status) =>
        status is "SUCCESS" or "FAILED" or "CANCELLED" or "REVERSED";

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), BusinessClock.BeijingOffset);

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var normalized = status.Trim().ToUpperInvariant();
        string[] allowed = ["DRAFT", "PENDING", "WRITING_ERP", "PENDING_APPROVAL", "SUCCESS",
            "FAILED", "CANCELLED", "REVERSED", "NEEDS_RECONCILIATION"];
        if (!allowed.Contains(normalized, StringComparer.Ordinal))
            throw new ApiException(StatusCodes.Status400BadRequest, "OPERATION_STATUS_INVALID", "业务状态无效。");
        return normalized;
    }

    private static string? OperationTypeForDocument(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType)) return null;
        return documentType.Trim().ToLowerInvariant() switch
        {
            "purchase-arrival" => "PURCHASE_ARRIVAL",
            "outsourcing-arrival" => "SUBCONTRACT_ARRIVAL",
            "purchase-inbound" => "PURCHASE_RECEIPT",
            "delivery-note" => "SALES_DELIVERY",
            "sales-outbound" => "SALES_OUTBOUND",
            "material-outbound" => "MATERIAL_OUTBOUND",
            "transfer-order" => "TRANSFER",
            "location-adjustment" => "LOCATION_ADJUSTMENT",
            "inventory-count" => "INVENTORY_COUNT",
            _ => throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_TYPE_INVALID", "单据类型无效。")
        };
    }

    private static string DocumentTypeForOperation(string operationType) => operationType switch
    {
        "PURCHASE_ARRIVAL" => "purchase-arrival",
        "SUBCONTRACT_ARRIVAL" => "outsourcing-arrival",
        "PURCHASE_RECEIPT" => "purchase-inbound",
        "SALES_DELIVERY" => "delivery-note",
        "SALES_OUTBOUND" => "sales-outbound",
        "MATERIAL_OUTBOUND" => "material-outbound",
        "TRANSFER" => "transfer-order",
        "LOCATION_ADJUSTMENT" => "location-adjustment",
        "INVENTORY_COUNT" => "inventory-count",
        _ => operationType.ToLowerInvariant()
    };

    private static void EnsureOperationAccess(
        IReadOnlyList<OperationRow> operations,
        string userId,
        bool manageAll)
    {
        if (!manageAll && operations.Any(item =>
                !string.Equals(item.OperatorId, userId, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(StatusCodes.Status403Forbidden,
                "OPERATION_FORBIDDEN", "无权操作该单据。");
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

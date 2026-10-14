using System.Data;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class DocumentPrepareRepository(SqlConnectionFactory connections)
{
    private sealed record ResolvedLine(
        DocumentPrepareLine Request,
        long? LabelId,
        long? PalletId,
        long? SourcePalletId,
        decimal? InventoryQty,
        string BatchNo,
        string WarehouseCode,
        string LocationCode);

    public async Task<MaterialOutboundPrepareResult> PrepareAsync(
        DocumentPrepareRequest request,
        DocumentPrepareDefinition definition,
        bool hasSource,
        string payloadHash,
        ArchiveResult archive,
        LabelSystemOptions options,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var groupId = request.ClientRequestId.Trim();
        var existing = await ReadExistingAsync(
            connection, transaction, groupId, payloadHash, options.U8WritesEnabled, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            await using var draftTransaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await DocumentDraftLifecycle.SetStatusAsync(
                connection, draftTransaction, groupId,
                DocumentDraftLifecycle.FromOperationStatus(existing.Status),
                request.OperatorId.Trim(), requestId, cancellationToken);
            await draftTransaction.CommitAsync(cancellationToken);
            return existing;
        }

        if (hasSource)
            await ReserveSourceLinesAsync(connection, transaction, request, cancellationToken);

        var products = new Dictionary<string, ProductLabelSnapshot>(StringComparer.OrdinalIgnoreCase);
        var allowUsedProductReturn = definition.DocumentType == "material-outbound" &&
                                     request.Header.DocumentDirection == "RED";
        foreach (var code in request.Lines
                     .Where(line => line.ObjectType == "PRODUCT_LABEL")
                     .Select(line => line.LabelCode.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = await LabelSystemRepository.ReadProductLabelAsync(
                connection, transaction, code, cancellationToken, true)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "LABEL_NOT_FOUND", $"未找到标签：{code}");
            EnsureAvailable(
                snapshot.Status, snapshot.LockedOperationId, code, false,
                allowUsedProductReturn);
            products.Add(code, snapshot);
        }

        var palletCodes = request.Lines
            .Where(line => line.ObjectType == "PALLET")
            .Select(line => line.LabelCode.Trim())
            .Concat(products.Values.Where(item => item.PalletCode is not null).Select(item => item.PalletCode!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pallets = new Dictionary<string, PalletSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in palletCodes)
        {
            var snapshot = await LabelSystemRepository.ReadPalletAsync(
                connection, transaction, code, cancellationToken, true)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "PALLET_NOT_FOUND", $"未找到托盘：{code}");
            EnsureAvailable(snapshot.Line.Status, snapshot.LockedOperationId, code, true, false);
            pallets.Add(code, snapshot);
        }

        var resolved = ResolveLines(request, definition, products, pallets);
        if (definition.WholePalletMove)
            await ValidateWholePalletMovesAsync(connection, transaction, request.Lines, products, pallets, cancellationToken);

        var now = BusinessClock.NowDatabase;
        var lockExpiresAt = now.AddMinutes(Math.Clamp(options.LockMinutes, 1, 120));
        var groups = hasSource
            ? resolved.GroupBy(line => new
                {
                    Type = line.Request.SourceType!,
                    Id = line.Request.SourceDocumentId!.Value,
                    No = line.Request.SourceDocumentNo!
                }).OrderBy(group => group.Key.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.Id)
                .Select(group => (Type: group.Key.Type, Id: group.Key.Id.ToString(), No: group.Key.No, Lines: group.ToArray())).ToArray()
            : definition.GroupByWarehouse
                ? resolved.GroupBy(line => line.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => (Type: string.Empty, Id: string.Empty, No: string.Empty, Lines: group.ToArray()))
                    .ToArray()
            : [(Type: request.Header.SourceType ?? string.Empty, Id: string.Empty,
                No: request.Header.SourceDocumentNo ?? string.Empty, Lines: resolved)];
        var created = new List<(long Id, string No, string SourceNo, ResolvedLine[] Lines)>();
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var operationNo = $"{definition.OperationPrefix}-{now:yyMMddHHmmss}-{Guid.NewGuid():N}"[..32];
            var clientRequestId = groups.Length == 1 ? groupId : $"{groupId}:{index + 1}";
            var operationId = await InsertOperationAsync(
                connection, transaction, operationNo, groupId, clientRequestId,
                group.Type, group.Id, group.No, request, definition,
                payloadHash, archive, options, now, lockExpiresAt, cancellationToken);
            await InsertLinesAsync(connection, transaction, operationId, request, definition, group.Lines, cancellationToken);
            await InsertEventAsync(connection, transaction, operationId, 1, "REQUEST_RECEIVED",
                requestId, $"已接收{definition.Title}请求。", cancellationToken);
            await InsertEventAsync(connection, transaction, operationId, 2, "DATABASE_COMMITTED",
                requestId, "已创建待处理业务，U8 尚未写入。", cancellationToken);
            created.Add((operationId, operationNo, group.No, group.Lines));
        }
        var rootOperationId = created.Min(item => item.Id);
        await ApplyLocksAsync(
            connection, transaction, rootOperationId,
            products.Values.Select(item => item.Id), pallets.Values.Select(item => item.Id), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await using (var draftTransaction = (SqlTransaction)await connection.BeginTransactionAsync(
                         IsolationLevel.ReadCommitted, cancellationToken))
        {
            await DocumentDraftLifecycle.SetStatusAsync(
                connection, draftTransaction, groupId, "PREPARED",
                request.OperatorId.Trim(), requestId, cancellationToken);
            await draftTransaction.CommitAsync(cancellationToken);
        }
        return new MaterialOutboundPrepareResult(
            groupId,
            "PENDING",
            options.U8WritesEnabled,
            created.Select(item => new PreparedOperation(
                item.Id, item.No, item.SourceNo,
                request.Header.WarehouseCode?.Trim() ?? resolved.First().WarehouseCode,
                item.Lines.Length, item.Lines.Sum(line => line.Request.RequestedQty))).ToArray());
    }

    private static ResolvedLine[] ResolveLines(
        DocumentPrepareRequest request,
        DocumentPrepareDefinition definition,
        IReadOnlyDictionary<string, ProductLabelSnapshot> products,
        IReadOnlyDictionary<string, PalletSnapshot> pallets)
    {
        var result = new List<ResolvedLine>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            if (line.ObjectType == "PRODUCT_LABEL")
            {
                var product = products[line.LabelCode.Trim()];
                EnsureIdentity(line, product.MaterialCode, product.UnitCode);
                EnsurePosition(request, definition, product.WarehouseCode, product.LocationCode, line.LabelCode);
                if (product.PalletId.HasValue &&
                    !string.Equals(product.PalletCode, line.SourcePalletCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(StatusCodes.Status409Conflict,
                        "PALLET_RELATION_CHANGED", $"标签托盘关系已变化：{line.LabelCode}");
                var isRedMaterialReturn = definition.DocumentType == "material-outbound" &&
                                          request.Header.DocumentDirection == "RED";
                var warehouseCode = isRedMaterialReturn
                    ? line.WarehouseCode?.Trim() ?? string.Empty
                    : product.WarehouseCode;
                var locationCode = isRedMaterialReturn
                    ? line.LocationCode?.Trim() ?? string.Empty
                    : product.LocationCode;
                if (string.IsNullOrWhiteSpace(warehouseCode))
                    throw new ApiException(StatusCodes.Status409Conflict,
                        "LABEL_LOCATION_MISSING", $"标签没有退料仓库：{line.LabelCode}");
                result.Add(new ResolvedLine(
                    line, product.Id, null, product.PalletId, product.CurrentQty,
                    product.BatchNo, warehouseCode, locationCode));
            }
            else if (line.ObjectType == "PALLET")
            {
                var pallet = pallets[line.LabelCode.Trim()];
                EnsureIdentity(line, pallet.Line.MaterialCode, pallet.Line.UnitCode);
                EnsurePosition(request, definition, pallet.Line.WarehouseCode, pallet.Line.LocationCode, line.LabelCode);
                result.Add(new ResolvedLine(
                    line, null, pallet.Id, null, pallet.Line.AvailableQty,
                    pallet.Line.BatchNo, pallet.Line.WarehouseCode, pallet.Line.LocationCode));
            }
            else
            {
                result.Add(new ResolvedLine(
                    line, null, null, null, null,
                    line.BatchNo?.Trim() ?? string.Empty,
                    line.WarehouseCode?.Trim() ?? string.Empty,
                    line.LocationCode?.Trim() ?? string.Empty));
            }
        }
        foreach (var group in result.GroupBy(line => new
                 {
                     line.Request.ObjectType,
                     Code = line.Request.LabelCode.Trim().ToUpperInvariant()
                 }))
        {
            if (group.Key.ObjectType == "UNLABELED_MATERIAL") continue;
            var total = group.Sum(line => line.Request.RequestedQty);
            var available = group.First().InventoryQty ?? 0m;
            var isRedMaterialReturn = definition.DocumentType == "material-outbound" &&
                                      request.Header.DocumentDirection == "RED";
            if (!isRedMaterialReturn && !definition.AllowZeroQuantity && total > available)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "LABEL_QUANTITY_INSUFFICIENT", $"标签数量不足：{group.Key.Code}");
            if (definition.WholePalletMove && total != available)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "WHOLE_OBJECT_QUANTITY_REQUIRED", $"必须整件处理：{group.Key.Code}");
        }
        return result.ToArray();
    }

    private static void EnsureAvailable(
        string status,
        long? lockedOperationId,
        string code,
        bool pallet,
        bool allowUsedProductReturn)
    {
        if (status != "ACTIVE" && !(allowUsedProductReturn && !pallet && status == "USED"))
            throw new ApiException(StatusCodes.Status409Conflict,
                pallet ? "PALLET_NOT_ACTIVE" : "LABEL_NOT_ACTIVE", $"{(pallet ? "托盘" : "标签")}不可用：{code}");
        if (lockedOperationId.HasValue)
            throw new ApiException(StatusCodes.Status409Conflict,
                pallet ? "PALLET_LOCKED" : "LABEL_LOCKED", $"{(pallet ? "托盘" : "标签")}处理中：{code}");
    }

    private static void EnsureIdentity(DocumentPrepareLine line, string materialCode, string unitCode)
    {
        if (!string.Equals(materialCode, line.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status409Conflict, "MATERIAL_MISMATCH", $"料号已变化：{line.LabelCode}");
        if (!string.Equals(unitCode, line.UnitCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status409Conflict, "UNIT_MISMATCH", $"单位已变化：{line.LabelCode}");
    }

    private static void EnsurePosition(
        DocumentPrepareRequest request,
        DocumentPrepareDefinition definition,
        string actualWarehouse,
        string actualLocation,
        string code)
    {
        if (definition.MatchCurrentWarehouse &&
            (string.IsNullOrWhiteSpace(actualWarehouse) ||
             !string.Equals(actualWarehouse, request.Header.WarehouseCode?.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(StatusCodes.Status409Conflict, "WAREHOUSE_CHANGED", $"当前仓库不一致：{code}");
        if (!string.IsNullOrWhiteSpace(request.Header.SourceLocationCode) &&
            !string.Equals(actualLocation, request.Header.SourceLocationCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status409Conflict, "LOCATION_CHANGED", $"当前货位不一致：{code}");
        if (definition.DocumentType == "location-adjustment" &&
            string.Equals(actualLocation, request.Header.TargetLocationCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status409Conflict, "LOCATION_UNCHANGED", $"目标货位未变化：{code}");
    }

    private static async Task ReserveSourceLinesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DocumentPrepareRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) op.submission_group_id
            FROM dbo.business_operation_line AS line WITH (UPDLOCK, HOLDLOCK, INDEX(IX_business_operation_line_source_line))
            INNER JOIN dbo.business_operation AS op WITH (UPDLOCK, HOLDLOCK) ON op.id=line.operation_id
            WHERE line.source_line_id=@source_line_id
              AND op.source_document_type=@source_type
              AND op.status IN ('DRAFT','PENDING','WRITING_ERP','PENDING_APPROVAL','NEEDS_RECONCILIATION')
              AND op.submission_group_id<>@group_id;
            """;
        foreach (var source in request.Lines
                     .Where(line => line.SourceLineId.HasValue && !string.IsNullOrWhiteSpace(line.SourceType))
                     .Select(line => new { Type = line.SourceType!.Trim(), Id = line.SourceLineId!.Value })
                     .Distinct()
                     .OrderBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Id))
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@source_line_id", SqlDbType.NVarChar, 100).Value = source.Id.ToString();
            command.Parameters.Add("@source_type", SqlDbType.VarChar, 50).Value = source.Type;
            command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = request.ClientRequestId.Trim();
            if (await command.ExecuteScalarAsync(cancellationToken) is not null)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "SOURCE_LINE_RESERVED", "源单明细已被其他未完成单据占用。");
        }
    }

    private static async Task ValidateWholePalletMovesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<DocumentPrepareLine> lines,
        IReadOnlyDictionary<string, ProductLabelSnapshot> products,
        IReadOnlyDictionary<string, PalletSnapshot> pallets,
        CancellationToken cancellationToken)
    {
        var requestedLabels = lines
            .Where(line => line.ObjectType == "PRODUCT_LABEL")
            .Select(line => line.LabelCode.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedPallets = lines
            .Where(line => line.ObjectType == "PALLET")
            .Select(line => line.LabelCode.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pallet in pallets.Values.OrderBy(item => item.Line.LabelCode, StringComparer.OrdinalIgnoreCase))
        {
            if (!requestedPallets.Contains(pallet.Line.LabelCode))
                throw new ApiException(StatusCodes.Status409Conflict,
                    "WHOLE_PALLET_REQUIRED", $"请同时处理托盘：{pallet.Line.LabelCode}");

            const string sql = """
                SELECT pl.label_code
                FROM dbo.pallet_label_relation AS rel WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.product_label AS pl WITH (UPDLOCK, HOLDLOCK) ON pl.id=rel.label_id
                WHERE rel.pallet_id=@pallet_id AND rel.unloaded_at IS NULL AND pl.status='ACTIVE'
                ORDER BY pl.label_code;
                """;
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@pallet_id", SqlDbType.BigInt).Value = pallet.Id;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var contents = new List<string>();
            while (await reader.ReadAsync(cancellationToken)) contents.Add(reader.GetString(0));
            foreach (var code in contents)
            {
                if (!requestedLabels.Contains(code) || !products.ContainsKey(code))
                    throw new ApiException(StatusCodes.Status409Conflict,
                        "WHOLE_PALLET_REQUIRED", $"托盘 {pallet.Line.LabelCode} 必须整托处理。");
            }
        }
    }

    private static async Task<long> InsertOperationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string operationNo,
        string groupId,
        string clientRequestId,
        string sourceType,
        string sourceDocumentId,
        string sourceDocumentNo,
        DocumentPrepareRequest request,
        DocumentPrepareDefinition definition,
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
                @operation_no, @group_id, @operation_type, 'PENDING',
                @source_type, @source_id, @source_no,
                @erp_environment, @erp_account_id, @erp_year, @erp_database_name,
                @erp_document_type, 'ADD', 'PENDING',
                @operator_id, @operator_name, @device_id, @device_code, @device_model,
                @client_request_id, @payload_hash, 'NOT_REQUIRED', @reason,
                @now, @now, @lock_expires_at,
                @archive_path, 'COMPLETE', @archive_sha256
            );
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_no", SqlDbType.NVarChar, 50).Value = operationNo;
        command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        command.Parameters.Add("@operation_type", SqlDbType.VarChar, 50).Value = definition.OperationType;
        command.Parameters.Add("@source_type", SqlDbType.VarChar, 50).Value = DbValue(sourceType);
        command.Parameters.Add("@source_id", SqlDbType.NVarChar, 100).Value = DbValue(sourceDocumentId);
        command.Parameters.Add("@source_no", SqlDbType.NVarChar, 100).Value = DbValue(sourceDocumentNo);
        command.Parameters.Add("@erp_environment", SqlDbType.VarChar, 20).Value = options.Environment;
        command.Parameters.Add("@erp_account_id", SqlDbType.NVarChar, 20).Value = options.ErpAccountId;
        command.Parameters.Add("@erp_year", SqlDbType.SmallInt).Value = options.ErpYear;
        command.Parameters.Add("@erp_database_name", SqlDbType.NVarChar, 128).Value = options.ErpDatabaseName;
        command.Parameters.Add("@erp_document_type", SqlDbType.VarChar, 50).Value = definition.ErpDocumentType;
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
        DocumentPrepareRequest request,
        DocumentPrepareDefinition definition,
        IReadOnlyList<ResolvedLine> lines,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.business_operation_line
            (
                operation_id, line_no, line_status,
                label_id, pallet_id, selection_source, source_pallet_id, inventory_qty_snapshot,
                source_line_id, source_line_no, material_code, batch_no, unit_code, requested_qty,
                from_warehouse_code, from_location_code, to_warehouse_code, to_location_code, remark
            )
            VALUES
            (
                @operation_id, @line_no, 'PENDING',
                @label_id, @pallet_id, @selection_source, @source_pallet_id, @inventory_qty,
                @source_line_id, @source_line_no, @material_code, @batch_no, @unit_code, @requested_qty,
                @from_warehouse, @from_location, @to_warehouse, @to_location, @remark
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
                line.LabelId.HasValue
                    ? line.SourcePalletId.HasValue ? "PALLET_SCAN" : "LABEL_SCAN"
                    : line.PalletId.HasValue ? "PALLET_SCAN" : DBNull.Value;
            command.Parameters.Add("@source_pallet_id", SqlDbType.BigInt).Value =
                line.SourcePalletId.HasValue ? line.SourcePalletId.Value : DBNull.Value;
            if (line.InventoryQty.HasValue)
                AddDecimal(command, "@inventory_qty", line.InventoryQty.Value);
            else
                command.Parameters.Add("@inventory_qty", SqlDbType.Decimal).Value = DBNull.Value;
            command.Parameters.Add("@source_line_id", SqlDbType.NVarChar, 100).Value =
                line.Request.SourceLineId.HasValue ? line.Request.SourceLineId.Value.ToString() : DBNull.Value;
            command.Parameters.Add("@source_line_no", SqlDbType.NVarChar, 50).Value =
                line.Request.SourceLineNo.HasValue ? line.Request.SourceLineNo.Value.ToString() : DBNull.Value;
            command.Parameters.Add("@material_code", SqlDbType.NVarChar, 100).Value = line.Request.MaterialCode.Trim();
            command.Parameters.Add("@batch_no", SqlDbType.NVarChar, 100).Value = DbValue(line.BatchNo);
            command.Parameters.Add("@unit_code", SqlDbType.NVarChar, 50).Value = line.Request.UnitCode.Trim();
            AddDecimal(command, "@requested_qty", line.Request.RequestedQty);
            command.Parameters.Add("@from_warehouse", SqlDbType.NVarChar, 100).Value = line.WarehouseCode;
            command.Parameters.Add("@from_location", SqlDbType.NVarChar, 100).Value = DbValue(line.LocationCode);
            var targetWarehouse = request.Header.TargetWarehouseCode;
            if (definition.DocumentType is "purchase-inbound" or "location-adjustment")
                targetWarehouse ??= request.Header.WarehouseCode;
            command.Parameters.Add("@to_warehouse", SqlDbType.NVarChar, 100).Value = DbValue(targetWarehouse);
            command.Parameters.Add("@to_location", SqlDbType.NVarChar, 100).Value = DbValue(request.Header.TargetLocationCode);
            command.Parameters.Add("@remark", SqlDbType.NVarChar, 500).Value = DbValue(line.Request.Remark);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ApplyLocksAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long operationId,
        IEnumerable<long> labelIds,
        IEnumerable<long> palletIds,
        CancellationToken cancellationToken)
    {
        foreach (var id in labelIds.Distinct().Order())
        {
            await using var command = new SqlCommand(
                "UPDATE dbo.product_label SET locked_operation_id=@operation_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id AND locked_operation_id IS NULL;",
                connection, transaction);
            command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ApiException(StatusCodes.Status409Conflict, "LABEL_LOCKED", "标签正在处理中。");
        }
        foreach (var id in palletIds.Distinct().Order())
        {
            await using var command = new SqlCommand(
                "UPDATE dbo.pallet SET locked_operation_id=@operation_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id AND locked_operation_id IS NULL;",
                connection, transaction);
            command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
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
        string requestId,
        string message,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.business_operation_event
                (operation_id, sequence_no, stage, source, event_type, result, request_id, message)
            VALUES
                (@operation_id, @sequence_no, @stage, 'BUSINESS_API', @event_type, 'SUCCESS', @request_id, @message);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@operation_id", SqlDbType.BigInt).Value = operationId;
        command.Parameters.Add("@sequence_no", SqlDbType.Int).Value = sequence;
        command.Parameters.Add("@stage", SqlDbType.VarChar, 50).Value = eventType;
        command.Parameters.Add("@event_type", SqlDbType.VarChar, 50).Value = eventType;
        command.Parameters.Add("@request_id", SqlDbType.VarChar, 100).Value = requestId;
        command.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = message;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<MaterialOutboundPrepareResult?> ReadExistingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string groupId,
        string payloadHash,
        bool u8WriteEnabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT op.id, op.operation_no, COALESCE(op.source_document_no,N''),
                   COALESCE(MAX(line.from_warehouse_code),N''), op.status,
                   op.request_payload_hash, COUNT(line.id), COALESCE(SUM(line.requested_qty),0)
            FROM dbo.business_operation AS op WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN dbo.business_operation_line AS line ON line.operation_id=op.id
            WHERE op.submission_group_id=@group_id
            GROUP BY op.id, op.operation_no, op.source_document_no, op.status, op.request_payload_hash
            ORDER BY op.id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(long Id, string No, string SourceNo, string Warehouse, string Status, string? Hash, int Count, decimal Qty)>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6), reader.GetDecimal(7)));
        if (rows.Count == 0) return null;
        if (rows.Any(row => !string.Equals(row.Hash, payloadHash, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(StatusCodes.Status409Conflict, "IDEMPOTENCY_CONFLICT", "请求编号已被其他内容使用。");
        var status = rows.Select(row => row.Status).Distinct().Count() == 1 ? rows[0].Status : "PARTIAL";
        return new MaterialOutboundPrepareResult(
            groupId, status, u8WriteEnabled,
            rows.Select(row => new PreparedOperation(
                row.Id, row.No, row.SourceNo, row.Warehouse, row.Count, row.Qty)).ToArray());
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 6;
        parameter.Value = value;
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

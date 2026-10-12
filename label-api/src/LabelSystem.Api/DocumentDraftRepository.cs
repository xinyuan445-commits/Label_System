using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class DocumentDraftRepository(SqlConnectionFactory connections)
{
    private const int MaxPayloadCharacters = 8 * 1024 * 1024;
    private static readonly string[] WritableStatuses = ["DRAFT", "PREPARED", "COMPLETED", "CANCELLED"];

    public async Task<DocumentDraftItem> UpsertAsync(
        string draftId,
        SyncDocumentDraftRequest request,
        SessionUser user,
        string requestId,
        CancellationToken cancellationToken)
    {
        draftId = Required(draftId, 100, "DRAFT_ID_INVALID", "草稿编号无效。");
        var deviceId = Required(request.DeviceId, 100, "DEVICE_ID_INVALID", "设备编号无效。");
        var documentType = Required(request.DocumentType, 50, "DOCUMENT_TYPE_INVALID", "单据类型无效。");
        var status = Required(request.BusinessStatus, 20, "DRAFT_STATUS_INVALID", "草稿状态无效。")
            .ToUpperInvariant();
        if (!WritableStatuses.Contains(status, StringComparer.Ordinal))
            throw new ApiException(StatusCodes.Status400BadRequest, "DRAFT_STATUS_INVALID", "草稿状态无效。");
        if (request.SchemaVersion <= 0 || request.ClientRevision <= 0 ||
            request.Summary.LineCount < 0 || request.Summary.TotalQuantity < 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "DRAFT_INVALID", "草稿内容无效。");
        if (request.Payload.ValueKind != JsonValueKind.Object)
            throw new ApiException(StatusCodes.Status400BadRequest, "DRAFT_PAYLOAD_INVALID", "草稿内容无效。");

        var payloadJson = request.Payload.GetRawText();
        if (payloadJson.Length > MaxPayloadCharacters)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge,
                "DRAFT_TOO_LARGE", "草稿内容过大，请拆分后重试。");
        if (request.UpdatedAt < request.CreatedAt || request.UpdatedAt > BusinessClock.Now.AddMinutes(5))
            throw new ApiException(StatusCodes.Status400BadRequest, "DRAFT_TIME_INVALID", "草稿时间无效。");
        var payloadHash = Hashing.Sha256(payloadJson);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var existing = await ReadHeaderForUpdateAsync(connection, transaction, draftId, cancellationToken);
        if (existing is not null && !string.Equals(existing.OwnerUserId, user.Id, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status403Forbidden, "DRAFT_FORBIDDEN", "无权操作这份草稿。");
        var operationGroupId = string.IsNullOrWhiteSpace(request.OperationGroupId)
            ? existing?.OperationGroupId
            : request.OperationGroupId.Trim();
        if (!string.IsNullOrWhiteSpace(existing?.OperationGroupId) &&
            !string.IsNullOrWhiteSpace(request.OperationGroupId) &&
            !string.Equals(existing.OperationGroupId, request.OperationGroupId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status409Conflict,
                "OPERATION_GROUP_CONFLICT", "单据已关联其他业务组。");
        if (!string.IsNullOrWhiteSpace(operationGroupId))
            status = await ReadOperationGroupDraftStatusAsync(
                connection, transaction, operationGroupId, user.Id, cancellationToken);
        else if (status == "PREPARED")
            throw new ApiException(StatusCodes.Status400BadRequest,
                "OPERATION_GROUP_REQUIRED", "业务组编号缺失。");
        if (existing is not null && request.ClientRevision < existing.ClientRevision)
            throw new ApiException(StatusCodes.Status409Conflict, "DRAFT_VERSION_CONFLICT", "草稿已有较新版本。");
        if (existing is not null && request.ClientRevision == existing.ClientRevision &&
            (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal) || existing.Status != status))
            throw new ApiException(StatusCodes.Status409Conflict, "DRAFT_VERSION_CONFLICT", "草稿版本冲突。");
        if (existing is not null && existing.Status is "PREPARED" or "COMPLETED" or "CANCELLED" &&
            status == "DRAFT")
            throw new ApiException(StatusCodes.Status409Conflict, "DRAFT_STATUS_CONFLICT", "已预提交单据不能恢复为草稿。");

        if (existing is null)
        {
            const string insertSql = """
                INSERT INTO dbo.document_draft
                    (draft_id, owner_user_id, owner_display_name, device_id, device_name,
                     document_type, business_status, schema_version, client_revision,
                     source_type_label, source_document_no, document_date, warehouse_code,
                     department_code, line_count, total_quantity, payload_json, payload_hash,
                     operation_group_id, created_at, client_updated_at, last_synced_at, deleted_at)
                VALUES
                    (@draft_id, @owner_user_id, @owner_name, @device_id, @device_name,
                     @document_type, @status, @schema_version, @client_revision,
                     @source_type_label, @source_document_no, @document_date, @warehouse_code,
                     @department_code, @line_count, @total_quantity, @payload_json, @payload_hash,
                     @operation_group_id, @created_at, @updated_at,
                     DATEADD(HOUR, 8, SYSUTCDATETIME()), NULL);
                """;
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            AddDraftParameters(insert, draftId, request, user, status, operationGroupId,
                deviceId, payloadJson, payloadHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await InsertEventAsync(connection, transaction, draftId, "CREATED", null, status,
                user.Id, deviceId, request.ClientRevision, requestId, cancellationToken);
        }
        else if (request.ClientRevision > existing.ClientRevision || existing.Status == "DELETED")
        {
            const string updateSql = """
                UPDATE dbo.document_draft
                SET owner_display_name=@owner_name, device_id=@device_id, device_name=@device_name,
                    document_type=@document_type, business_status=@status,
                    schema_version=@schema_version, client_revision=@client_revision,
                    source_type_label=@source_type_label, source_document_no=@source_document_no,
                    document_date=@document_date, warehouse_code=@warehouse_code,
                    department_code=@department_code, line_count=@line_count,
                    total_quantity=@total_quantity, payload_json=@payload_json,
                    payload_hash=@payload_hash, operation_group_id=@operation_group_id,
                    created_at=@created_at, client_updated_at=@updated_at,
                    last_synced_at=DATEADD(HOUR, 8, SYSUTCDATETIME()), deleted_at=NULL
                WHERE draft_id=@draft_id;
                """;
            await using var update = new SqlCommand(updateSql, connection, transaction);
            AddDraftParameters(update, draftId, request, user, status, operationGroupId,
                deviceId, payloadJson, payloadHash);
            await update.ExecuteNonQueryAsync(cancellationToken);
            if (existing.Status != status)
                await InsertEventAsync(connection, transaction, draftId,
                    existing.Status == "DELETED" ? "RESTORED" : "STATUS_CHANGED",
                    existing.Status, status, user.Id, deviceId, request.ClientRevision,
                    requestId, cancellationToken);
        }
        else
        {
            await using var touch = new SqlCommand("""
                UPDATE dbo.document_draft
                SET last_synced_at=DATEADD(HOUR, 8, SYSUTCDATETIME()),
                    device_id=@device_id, device_name=@device_name
                WHERE draft_id=@draft_id;
                """, connection, transaction);
            touch.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draftId;
            touch.Parameters.Add("@device_id", SqlDbType.NVarChar, 100).Value = deviceId;
            touch.Parameters.Add("@device_name", SqlDbType.NVarChar, 200).Value = DbValue(request.DeviceName, 200);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await ReadOneAsync(connection, transaction, draftId, true, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "DRAFT_READ_FAILED", "读取草稿失败。");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<DocumentDraftItem>> ListAsync(
        string ownerUserId,
        bool allUsers,
        string? documentType,
        string? status,
        int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        const string sql = """
            SELECT TOP (@take)
                   d.draft_id, d.owner_user_id, d.owner_display_name, d.device_id, d.device_name,
                   d.document_type, d.business_status, d.schema_version, d.client_revision,
                   d.document_date, d.source_type_label, d.source_document_no,
                   d.warehouse_code, d.department_code, d.line_count, d.total_quantity,
                   d.operation_group_id, d.created_at, d.client_updated_at, d.last_synced_at,
                   d.deleted_at, d.row_version
            FROM dbo.document_draft AS d
            WHERE (@all_users = 1 OR d.owner_user_id = @owner_user_id)
              AND (@document_type IS NULL OR d.document_type = @document_type)
              AND (@status IS NULL OR d.business_status = @status)
            ORDER BY d.client_updated_at DESC, d.draft_id;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.Parameters.Add("@all_users", SqlDbType.Bit).Value = allUsers;
        command.Parameters.Add("@owner_user_id", SqlDbType.NVarChar, 100).Value = ownerUserId;
        command.Parameters.Add("@document_type", SqlDbType.VarChar, 50).Value = DbValue(documentType, 50);
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = DbValue(normalizedStatus, 20);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DocumentDraftItem>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDraft(reader, null));
        return result;
    }

    public async Task<DocumentDraftItem> GetAsync(
        string draftId,
        string userId,
        bool allowAllUsers,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        var item = await ReadOneAsync(connection, null, draftId, true, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "DRAFT_NOT_FOUND", "草稿不存在。");
        if (!allowAllUsers && !string.Equals(item.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status403Forbidden, "DRAFT_FORBIDDEN", "无权查看这份草稿。");
        return item;
    }

    public async Task<DocumentDraftItem> DeleteAsync(
        string draftId,
        DeleteDocumentDraftRequest request,
        SessionUser user,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (request.ClientRevision <= 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "DRAFT_VERSION_INVALID", "草稿版本无效。");
        var deviceId = Required(request.DeviceId, 100, "DEVICE_ID_INVALID", "设备编号无效。");
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var existing = await ReadHeaderForUpdateAsync(connection, transaction, draftId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "DRAFT_NOT_FOUND", "草稿不存在。");
        if (!string.Equals(existing.OwnerUserId, user.Id, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status403Forbidden, "DRAFT_FORBIDDEN", "无权删除这份草稿。");
        if (request.ClientRevision < existing.ClientRevision)
            throw new ApiException(StatusCodes.Status409Conflict, "DRAFT_VERSION_CONFLICT", "草稿已有较新版本。");
        if (existing.Status != "DELETED")
        {
            await using var update = new SqlCommand("""
                UPDATE dbo.document_draft
                SET business_status='DELETED', client_revision=@client_revision,
                    device_id=@device_id, client_updated_at=DATEADD(HOUR, 8, SYSUTCDATETIME()),
                    last_synced_at=DATEADD(HOUR, 8, SYSUTCDATETIME()),
                    deleted_at=DATEADD(HOUR, 8, SYSUTCDATETIME())
                WHERE draft_id=@draft_id;
                """, connection, transaction);
            update.Parameters.Add("@client_revision", SqlDbType.BigInt).Value = request.ClientRevision;
            update.Parameters.Add("@device_id", SqlDbType.NVarChar, 100).Value = deviceId;
            update.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draftId;
            await update.ExecuteNonQueryAsync(cancellationToken);
            await InsertEventAsync(connection, transaction, draftId, "DELETED", existing.Status,
                "DELETED", user.Id, deviceId, request.ClientRevision, requestId, cancellationToken);
        }
        var result = await ReadOneAsync(connection, transaction, draftId, false, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "DRAFT_READ_FAILED", "读取草稿失败。");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task UpsertDeviceStateAsync(
        DeviceHeartbeatRequest request,
        SessionUser user,
        CancellationToken cancellationToken)
    {
        var deviceId = Required(request.DeviceId, 100, "DEVICE_ID_INVALID", "设备编号无效。");
        if (request.LocalDraftCount < 0 || request.PendingSyncCount < 0 || request.FailedSyncCount < 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "DEVICE_STATE_INVALID", "设备状态无效。");
        const string sql = """
            MERGE dbo.client_device_state WITH (HOLDLOCK) AS target
            USING (SELECT @user_id AS user_id, @device_id AS device_id) AS source
            ON target.user_id=source.user_id AND target.device_id=source.device_id
            WHEN MATCHED THEN UPDATE SET
                device_name=@device_name, device_model=@device_model, app_version=@app_version,
                local_draft_count=@local_count, pending_sync_count=@pending_count,
                failed_sync_count=@failed_count,
                last_seen_at=DATEADD(HOUR, 8, SYSUTCDATETIME()),
                last_successful_sync_at=COALESCE(@last_sync, target.last_successful_sync_at),
                updated_at=DATEADD(HOUR, 8, SYSUTCDATETIME())
            WHEN NOT MATCHED THEN INSERT
                (user_id, device_id, device_name, device_model, app_version,
                 local_draft_count, pending_sync_count, failed_sync_count,
                 last_seen_at, last_successful_sync_at)
            VALUES
                (@user_id, @device_id, @device_name, @device_model, @app_version,
                 @local_count, @pending_count, @failed_count,
                 DATEADD(HOUR, 8, SYSUTCDATETIME()), @last_sync);
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = user.Id;
        command.Parameters.Add("@device_id", SqlDbType.NVarChar, 100).Value = deviceId;
        command.Parameters.Add("@device_name", SqlDbType.NVarChar, 200).Value = DbValue(request.DeviceName, 200);
        command.Parameters.Add("@device_model", SqlDbType.NVarChar, 200).Value = DbValue(request.DeviceModel, 200);
        command.Parameters.Add("@app_version", SqlDbType.NVarChar, 50).Value = DbValue(request.AppVersion, 50);
        command.Parameters.Add("@local_count", SqlDbType.Int).Value = request.LocalDraftCount;
        command.Parameters.Add("@pending_count", SqlDbType.Int).Value = request.PendingSyncCount;
        command.Parameters.Add("@failed_count", SqlDbType.Int).Value = request.FailedSyncCount;
        command.Parameters.Add("@last_sync", SqlDbType.DateTime2).Value = request.LastSuccessfulSyncAt is null
            ? DBNull.Value : ToDatabaseTime(request.LastSuccessfulSyncAt.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClientDeviceStateItem>> ListDevicesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.user_id, u.display_name, d.device_id, d.device_name, d.device_model,
                   d.app_version, d.local_draft_count, d.pending_sync_count,
                   d.failed_sync_count, d.last_seen_at, d.last_successful_sync_at
            FROM dbo.client_device_state AS d
            INNER JOIN dbo.application_user AS u ON u.user_id=d.user_id
            ORDER BY d.last_seen_at DESC, d.user_id, d.device_id;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ClientDeviceStateItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ClientDeviceStateItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), DbString(reader, 3),
                DbString(reader, 4), DbString(reader, 5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetInt32(8), FromDatabaseTime(reader.GetDateTime(9)),
                reader.IsDBNull(10) ? null : FromDatabaseTime(reader.GetDateTime(10))));
        }
        return result;
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        const string settingsSql = """
            SELECT value_json
            FROM dbo.system_setting
            WHERE setting_scope='GLOBAL' AND document_type IS NULL
              AND setting_key='draft_retention_policy' AND is_enabled=1;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var settings = new SqlCommand(settingsSql, connection);
        var json = await settings.ExecuteScalarAsync(cancellationToken) as string;
        var policy = DraftRetentionPolicy.Parse(json);
        if (policy.ActiveDays is null && policy.ClosedDays is null) return 0;

        const string deleteSql = """
            DELETE e
            FROM dbo.document_draft_event AS e
            INNER JOIN dbo.document_draft AS d ON d.draft_id=e.draft_id
            WHERE
                (@active_days IS NOT NULL AND d.business_status IN ('DRAFT', 'PREPARED')
                 AND d.last_synced_at < DATEADD(DAY, -@active_days, DATEADD(HOUR, 8, SYSUTCDATETIME())))
             OR (@closed_days IS NOT NULL AND d.business_status IN ('COMPLETED', 'CANCELLED', 'DELETED')
                 AND d.last_synced_at < DATEADD(DAY, -@closed_days, DATEADD(HOUR, 8, SYSUTCDATETIME())));

            DELETE FROM dbo.document_draft
            WHERE
                (@active_days IS NOT NULL AND business_status IN ('DRAFT', 'PREPARED')
                 AND last_synced_at < DATEADD(DAY, -@active_days, DATEADD(HOUR, 8, SYSUTCDATETIME())))
             OR (@closed_days IS NOT NULL AND business_status IN ('COMPLETED', 'CANCELLED', 'DELETED')
                 AND last_synced_at < DATEADD(DAY, -@closed_days, DATEADD(HOUR, 8, SYSUTCDATETIME())));
            SELECT @@ROWCOUNT;
            """;
        await using var delete = new SqlCommand(deleteSql, connection);
        delete.Parameters.Add("@active_days", SqlDbType.Int).Value = policy.ActiveDays is null
            ? DBNull.Value : policy.ActiveDays.Value;
        delete.Parameters.Add("@closed_days", SqlDbType.Int).Value = policy.ClosedDays is null
            ? DBNull.Value : policy.ClosedDays.Value;
        return Convert.ToInt32(await delete.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string> ReadOperationGroupDraftStatusAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string groupId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT operator_id, status
            FROM dbo.business_operation
            WHERE submission_group_id=@group_id
            ORDER BY id;
            """, connection, transaction);
        command.Parameters.Add("@group_id", SqlDbType.VarChar, 100).Value = groupId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var statuses = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!string.Equals(reader.GetString(0), userId, StringComparison.OrdinalIgnoreCase))
                throw new ApiException(StatusCodes.Status403Forbidden,
                    "OPERATION_GROUP_INVALID", "业务组不存在或不属于当前用户。");
            statuses.Add(reader.GetString(1));
        }
        if (statuses.Count == 0)
            throw new ApiException(StatusCodes.Status409Conflict,
                "OPERATION_GROUP_INVALID", "业务组不存在或不属于当前用户。");
        if (statuses.All(status => status == "CANCELLED")) return "CANCELLED";
        if (statuses.All(status => status is "SUCCESS" or "FAILED" or "CANCELLED" or "REVERSED"))
            return "COMPLETED";
        return "PREPARED";
    }

    private static void AddDraftParameters(
        SqlCommand command,
        string draftId,
        SyncDocumentDraftRequest request,
        SessionUser user,
        string status,
        string? operationGroupId,
        string deviceId,
        string payloadJson,
        string payloadHash)
    {
        command.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draftId;
        command.Parameters.Add("@owner_user_id", SqlDbType.NVarChar, 100).Value = user.Id;
        command.Parameters.Add("@owner_name", SqlDbType.NVarChar, 100).Value = user.Name;
        command.Parameters.Add("@device_id", SqlDbType.NVarChar, 100).Value = deviceId;
        command.Parameters.Add("@device_name", SqlDbType.NVarChar, 200).Value = DbValue(request.DeviceName, 200);
        command.Parameters.Add("@document_type", SqlDbType.VarChar, 50).Value = request.DocumentType.Trim();
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = status;
        command.Parameters.Add("@schema_version", SqlDbType.Int).Value = request.SchemaVersion;
        command.Parameters.Add("@client_revision", SqlDbType.BigInt).Value = request.ClientRevision;
        command.Parameters.Add("@source_type_label", SqlDbType.NVarChar, 100).Value = DbValue(request.Summary.SourceTypeLabel, 100);
        command.Parameters.Add("@source_document_no", SqlDbType.NVarChar, 100).Value = DbValue(request.Summary.SourceDocumentNo, 100);
        command.Parameters.Add("@document_date", SqlDbType.Date).Value = request.Summary.DocumentDate is null
            ? DBNull.Value : request.Summary.DocumentDate.Value.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@warehouse_code", SqlDbType.NVarChar, 100).Value = DbValue(request.Summary.WarehouseCode, 100);
        command.Parameters.Add("@department_code", SqlDbType.NVarChar, 100).Value = DbValue(request.Summary.DepartmentCode, 100);
        command.Parameters.Add("@line_count", SqlDbType.Int).Value = request.Summary.LineCount;
        command.Parameters.Add("@total_quantity", SqlDbType.Decimal).Value = request.Summary.TotalQuantity;
        command.Parameters["@total_quantity"].Precision = 18;
        command.Parameters["@total_quantity"].Scale = 6;
        command.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value = payloadJson;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value = payloadHash;
        command.Parameters.Add("@operation_group_id", SqlDbType.VarChar, 100).Value = DbValue(operationGroupId, 100);
        command.Parameters.Add("@created_at", SqlDbType.DateTime2).Value = ToDatabaseTime(request.CreatedAt);
        command.Parameters.Add("@updated_at", SqlDbType.DateTime2).Value = ToDatabaseTime(request.UpdatedAt);
    }

    private static async Task<DraftHeader?> ReadHeaderForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string draftId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT owner_user_id, business_status, client_revision, payload_hash, operation_group_id
            FROM dbo.document_draft WITH (UPDLOCK, HOLDLOCK)
            WHERE draft_id=@draft_id;
            """, connection, transaction);
        command.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draftId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DraftHeader(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    private static async Task<DocumentDraftItem?> ReadOneAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string draftId,
        bool includePayload,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT d.draft_id, d.owner_user_id, d.owner_display_name, d.device_id, d.device_name,
                   d.document_type, d.business_status, d.schema_version, d.client_revision,
                   d.document_date, d.source_type_label, d.source_document_no,
                   d.warehouse_code, d.department_code, d.line_count, d.total_quantity,
                   d.operation_group_id, d.created_at, d.client_updated_at, d.last_synced_at,
                   d.deleted_at, d.row_version{(includePayload ? ", d.payload_json" : string.Empty)}
            FROM dbo.document_draft AS d
            WHERE d.draft_id=@draft_id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draftId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        JsonElement? payload = null;
        if (includePayload)
        {
            using var document = JsonDocument.Parse(reader.GetString(22));
            payload = document.RootElement.Clone();
        }
        return ReadDraft(reader, payload);
    }

    private static DocumentDraftItem ReadDraft(SqlDataReader reader, JsonElement? payload) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DbString(reader, 4),
        reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt64(8),
        reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
        DbString(reader, 10), DbString(reader, 11), DbString(reader, 12), DbString(reader, 13),
        reader.GetInt32(14), reader.GetDecimal(15), DbString(reader, 16),
        FromDatabaseTime(reader.GetDateTime(17)), FromDatabaseTime(reader.GetDateTime(18)),
        FromDatabaseTime(reader.GetDateTime(19)),
        reader.IsDBNull(20) ? null : FromDatabaseTime(reader.GetDateTime(20)),
        RowVersions.Encode((byte[])reader[21]), payload);

    private static async Task InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string draftId,
        string eventCode,
        string? previousStatus,
        string currentStatus,
        string actorUserId,
        string? deviceId,
        long? clientRevision,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO dbo.document_draft_event
                (draft_id, event_code, previous_status, current_status, actor_user_id,
                 device_id, client_revision, request_id)
            VALUES
                (@draft_id, @event_code, @previous_status, @current_status, @actor_user_id,
                 @device_id, @client_revision, @request_id);
            """, connection, transaction);
        command.Parameters.Add("@draft_id", SqlDbType.NVarChar, 100).Value = draftId;
        command.Parameters.Add("@event_code", SqlDbType.VarChar, 30).Value = eventCode;
        command.Parameters.Add("@previous_status", SqlDbType.VarChar, 20).Value =
            previousStatus is null ? DBNull.Value : previousStatus;
        command.Parameters.Add("@current_status", SqlDbType.VarChar, 20).Value = currentStatus;
        command.Parameters.Add("@actor_user_id", SqlDbType.NVarChar, 100).Value = actorUserId;
        command.Parameters.Add("@device_id", SqlDbType.NVarChar, 100).Value =
            deviceId is null ? DBNull.Value : deviceId;
        command.Parameters.Add("@client_revision", SqlDbType.BigInt).Value =
            clientRevision is null ? DBNull.Value : clientRevision.Value;
        command.Parameters.Add("@request_id", SqlDbType.NVarChar, 100).Value = requestId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Required(string? value, int maxLength, string code, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
            throw new ApiException(StatusCodes.Status400BadRequest, code, message);
        return normalized;
    }

    private static object DbValue(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return DBNull.Value;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? DbString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime ToDatabaseTime(DateTimeOffset value) =>
        value.ToOffset(BusinessClock.BeijingOffset).DateTime;

    private static DateTimeOffset FromDatabaseTime(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), BusinessClock.BeijingOffset);

    private sealed record DraftHeader(
        string OwnerUserId,
        string Status,
        long ClientRevision,
        string PayloadHash,
        string? OperationGroupId);
}

public sealed record DraftRetentionPolicy(int? ActiveDays, int? ClosedDays)
{
    public static DraftRetentionPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, 90);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new(ParseDays(root, "activeMode", "activeDays"),
                ParseDays(root, "closedMode", "closedDays"));
        }
        catch (JsonException)
        {
            return new(null, 90);
        }
    }

    private static int? ParseDays(JsonElement root, string modeName, string daysName)
    {
        if (root.TryGetProperty(modeName, out var mode) &&
            string.Equals(mode.GetString(), "PERMANENT", StringComparison.OrdinalIgnoreCase)) return null;
        if (root.TryGetProperty(daysName, out var days) && days.TryGetInt32(out var value))
            return Math.Clamp(value, 1, 36500);
        return null;
    }
}

public sealed class DocumentDraftCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<DocumentDraftCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var removed = await scope.ServiceProvider
                    .GetRequiredService<DocumentDraftRepository>()
                    .CleanupAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("Removed {Count} expired drafts.", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Draft cleanup failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class UserRepository(SqlConnectionFactory connections)
{
    public async Task<SessionUser?> FindSessionUserByLoginAsync(
        string loginName,
        bool recordLogin,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT user_id, display_name, is_admin
            FROM dbo.application_user
            WHERE login_name=@login_name AND is_enabled=1;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(selectSql, connection);
        command.Parameters.Add("@login_name", SqlDbType.NVarChar, 100).Value = loginName.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var user = ToSessionUser(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2));
        await reader.DisposeAsync();
        if (recordLogin)
        {
            await using var update = new SqlCommand("""
                UPDATE dbo.application_user
                SET last_login_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                    updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE user_id=@user_id;
                """, connection);
            update.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = user.Id;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return user;
    }

    public async Task<SessionUser?> AuthenticateLocalUserAsync(
        string loginName,
        string password,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT user_id, display_name, is_admin,
                   password_hash, password_salt, password_iterations,
                   failed_attempt_count, locked_until
            FROM dbo.application_user
            WHERE login_name=@login_name AND auth_source='LOCAL' AND is_enabled=1;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(selectSql, connection);
        command.Parameters.Add("@login_name", SqlDbType.NVarChar, 100).Value = loginName.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var userId = reader.GetString(0);
        var displayName = reader.GetString(1);
        var isAdmin = reader.GetBoolean(2);
        var expectedHash = (byte[])reader[3];
        var salt = (byte[])reader[4];
        var iterations = reader.GetInt32(5);
        var lockedUntil = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7);
        await reader.DisposeAsync();

        var now = BusinessClock.Now.DateTime;
        if (lockedUntil.HasValue && lockedUntil.Value > now) return null;
        var verified = PasswordCredentials.Verify(password, expectedHash, salt, iterations);
        if (!verified)
        {
            await using var failed = new SqlCommand("""
                UPDATE dbo.application_user
                SET failed_attempt_count=CASE WHEN failed_attempt_count>=4 THEN 0 ELSE failed_attempt_count+1 END,
                    locked_until=CASE WHEN failed_attempt_count>=4
                        THEN DATEADD(MINUTE,15,DATEADD(HOUR,8,SYSUTCDATETIME())) ELSE NULL END,
                    updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE user_id=@user_id;
                """, connection);
            failed.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            await failed.ExecuteNonQueryAsync(cancellationToken);
            return null;
        }

        await using (var login = new SqlCommand("""
            UPDATE dbo.application_user
            SET last_login_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                failed_attempt_count=0, locked_until=NULL,
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE user_id=@user_id;
            """, connection))
        {
            login.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            await login.ExecuteNonQueryAsync(cancellationToken);
        }
        return ToSessionUser(userId, displayName, isAdmin);
    }

    public async Task<SessionUser?> FindSessionUserByIdAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT user_id, display_name, is_admin
            FROM dbo.application_user
            WHERE user_id=@user_id AND is_enabled=1;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ToSessionUser(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))
            : null;
    }

    public async Task<IReadOnlyList<UserItem>> ListUsersAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT user_id, login_name, display_name, auth_source,
                   erp_operator_code, erp_operator_name, is_admin, is_enabled,
                   last_login_at, failed_attempt_count, locked_until,
                   password_changed_at, row_version
            FROM dbo.application_user
            ORDER BY is_enabled DESC, display_name, user_id;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<UserItem>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadUser(reader));
        return result;
    }

    public async Task<UserItem> CreateUserAsync(
        CreateUserRequest request,
        UserAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        var values = ValidateUser(request.Id, request.LoginName, request.Name, request.AuthSource,
            request.ErpOperatorCode, request.ErpOperatorName);
        PasswordCredentialValue? credential = null;
        if (values.Source == "LOCAL")
            credential = PasswordCredentials.Create(request.Password ?? string.Empty);
        else if (!string.IsNullOrEmpty(request.Password))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PASSWORD_NOT_ALLOWED", "当前身份来源不能设置本地密码。");
        const string sql = """
            INSERT INTO dbo.application_user
                (user_id, login_name, display_name, auth_source,
                 erp_operator_code, erp_operator_name, is_admin, is_enabled,
                 password_hash, password_salt, password_iterations, password_changed_at,
                 created_by, updated_by)
            OUTPUT inserted.user_id, inserted.login_name, inserted.display_name,
                   inserted.auth_source, inserted.erp_operator_code, inserted.erp_operator_name,
                   inserted.is_admin, inserted.is_enabled, inserted.last_login_at,
                   inserted.failed_attempt_count, inserted.locked_until,
                   inserted.password_changed_at, inserted.row_version
            VALUES
                (@user_id, @login_name, @display_name, @auth_source,
                 @erp_code, @erp_name, @is_admin, @is_enabled,
                 @hash, @salt, @iterations,
                 CASE WHEN @hash IS NULL THEN NULL ELSE DATEADD(HOUR,8,SYSUTCDATETIME()) END,
                 @updated_by, @updated_by);
            """;
        try
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection, transaction);
            AddUserParameters(command, values, request.IsAdmin, request.IsEnabled, audit.ActorUserId);
            AddCredentialParameters(command, credential);
            UserItem result;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                result = ReadUser(reader);
            }
            await InsertAuditAsync(connection, transaction, "USER_CREATED", audit, result.Id,
                null, UserSnapshot(result), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "USER_EXISTS", "用户已存在。");
        }
    }

    public async Task SetPasswordAsync(
        string userId,
        string password,
        UserAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        var credential = PasswordCredentials.Create(password);
        const string sql = """
            UPDATE dbo.application_user
            SET password_hash=@hash, password_salt=@salt, password_iterations=@iterations,
                failed_attempt_count=0, locked_until=NULL,
                password_changed_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE user_id=@user_id AND auth_source='LOCAL';
            IF @@ROWCOUNT=0 THROW 50010, N'USER_NOT_LOCAL', 1;
            """;
        try
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            AddCredentialParameters(command, credential);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await InsertAuditAsync(connection, transaction, "PASSWORD_RESET", audit, userId,
                null, new { passwordReset = true }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 50010)
        {
            throw new ApiException(StatusCodes.Status400BadRequest,
                "USER_NOT_LOCAL", "该用户不是本地密码用户。");
        }
    }

    public async Task<UserItem> UpdateUserAsync(
        string userId,
        UpdateUserRequest request,
        UserAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        var values = ValidateUser(userId, userId, request.Name, "LOCAL",
            request.ErpOperatorCode, request.ErpOperatorName);
        const string sql = """
            UPDATE dbo.application_user
            SET display_name=@display_name, erp_operator_code=@erp_code,
                erp_operator_name=@erp_name, is_admin=@is_admin, is_enabled=@is_enabled,
                updated_by=@updated_by, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            OUTPUT inserted.user_id, inserted.login_name, inserted.display_name,
                   inserted.auth_source, inserted.erp_operator_code, inserted.erp_operator_name,
                   inserted.is_admin, inserted.is_enabled, inserted.last_login_at,
                   inserted.failed_attempt_count, inserted.locked_until,
                   inserted.password_changed_at, inserted.row_version
            WHERE user_id=@user_id AND row_version=@row_version;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var before = await GetUserForUpdateAsync(connection, transaction, userId, cancellationToken);
        if (before is null)
            throw new ApiException(StatusCodes.Status404NotFound, "USER_NOT_FOUND", "用户不存在。");
        if (before.IsAdmin && before.IsEnabled && (!request.IsAdmin || !request.IsEnabled))
        {
            await using var countCommand = new SqlCommand(
                "SELECT COUNT_BIG(1) FROM dbo.application_user WITH (UPDLOCK,HOLDLOCK) WHERE is_admin=1 AND is_enabled=1;",
                connection, transaction);
            var enabledAdminCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));
            if (enabledAdminCount <= 1)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "LAST_ADMIN_PROTECTED", "至少保留一个启用的管理员。");
        }
        await using var command = new SqlCommand(sql, connection, transaction);
        AddUserParameters(command, values, request.IsAdmin, request.IsEnabled, audit.ActorUserId);
        command.Parameters.Add("@row_version", SqlDbType.Timestamp).Value =
            RowVersions.Decode(request.ExpectedRowVersion);
        UserItem? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) result = ReadUser(reader);
        if (result is null)
            throw new ApiException(StatusCodes.Status409Conflict, "DATA_CHANGED", "数据已变化，请刷新后重试。");
        await InsertAuditAsync(connection, transaction, "USER_UPDATED", audit, userId,
            UserSnapshot(before), UserSnapshot(result), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<FunctionItem>> ListFunctionsForUserAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT f.function_code, f.function_name, f.function_kind, f.parent_function_code,
                   f.group_code, f.group_name, f.group_sort, f.function_sort, f.icon_code,
                   f.is_enabled, f.row_version
            FROM dbo.system_function f
            JOIN dbo.application_user u ON u.user_id=@user_id AND u.is_enabled=1
            LEFT JOIN dbo.user_function_permission p
              ON p.user_id=u.user_id AND p.function_code=f.function_code
            WHERE f.is_enabled=1
              AND ((u.is_admin=1 AND ISNULL(p.permission_state, 'INHERIT') <> 'DENY')
                   OR (u.is_admin=0 AND p.permission_state='ALLOW'))
            ORDER BY f.group_sort, f.function_sort, f.function_code;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FunctionItem>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadFunction(reader));
        return result;
    }

    public async Task<IReadOnlyList<UserFunctionPermissionItem>> ListPermissionsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT f.function_code, f.function_name, f.function_kind,
                   f.group_code, f.group_name, f.group_sort, f.function_sort,
                   ISNULL(p.permission_state, 'INHERIT'),
                   CONVERT(bit, CASE
                       WHEN f.is_enabled=0 THEN 0
                       WHEN u.is_admin=1 AND ISNULL(p.permission_state, 'INHERIT') <> 'DENY' THEN 1
                       WHEN u.is_admin=0 AND p.permission_state='ALLOW' THEN 1
                       ELSE 0 END),
                   p.data_scope_json, p.row_version
            FROM dbo.system_function f
            JOIN dbo.application_user u ON u.user_id=@user_id
            LEFT JOIN dbo.user_function_permission p
              ON p.function_code=f.function_code AND p.user_id=@user_id
            ORDER BY f.group_sort, f.function_sort, f.function_code;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<UserFunctionPermissionItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            JsonElement? scope = null;
            if (!reader.IsDBNull(9))
            {
                using var document = JsonDocument.Parse(reader.GetString(9));
                scope = document.RootElement.Clone();
            }
            result.Add(new UserFunctionPermissionItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetString(7), reader.GetBoolean(8), scope,
                reader.IsDBNull(10) ? null : RowVersions.Encode((byte[])reader[10])));
        }
        return result;
    }

    public async Task UpdatePermissionAsync(
        string userId,
        string functionCode,
        UpdateUserFunctionPermissionRequest request,
        UserAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        var state = request.State.Trim().ToUpperInvariant();
        if (state is not ("INHERIT" or "ALLOW" or "DENY"))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PERMISSION_STATE_INVALID", "权限状态无效。");
        var scopeJson = request.DataScope.HasValue ? request.DataScope.Value.GetRawText() : null;
        if (state == "INHERIT" && scopeJson is not null) state = "ALLOW";
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var before = await GetPermissionSnapshotAsync(connection, transaction, userId, functionCode, cancellationToken);
        if (state == "INHERIT" && scopeJson is null)
        {
            await using var delete = new SqlCommand("""
                DELETE FROM dbo.user_function_permission
                WHERE user_id=@user_id AND function_code=@function_code
                  AND (@row_version IS NULL OR row_version=@row_version);
                """, connection, transaction);
            delete.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            delete.Parameters.Add("@function_code", SqlDbType.VarChar, 100).Value = functionCode;
            delete.Parameters.Add("@row_version", SqlDbType.Timestamp).Value =
                string.IsNullOrWhiteSpace(request.ExpectedRowVersion)
                    ? DBNull.Value : RowVersions.Decode(request.ExpectedRowVersion);
            var affected = await delete.ExecuteNonQueryAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.ExpectedRowVersion) && affected == 0)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "DATA_CHANGED", "权限数据已变化，请刷新后重试。");
            await InsertAuditAsync(connection, transaction, "PERMISSION_UPDATED", audit, userId,
                new { functionCode, state = before.State, dataScope = before.DataScopeJson },
                new { functionCode, state, dataScope = scopeJson }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        const string sql = """
            UPDATE dbo.user_function_permission
            SET permission_state=@state, data_scope_json=@scope,
                updated_by=@updated_by, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE user_id=@user_id AND function_code=@function_code
              AND (@row_version IS NULL OR row_version=@row_version);
            IF @@ROWCOUNT=0 AND @row_version IS NULL
                INSERT INTO dbo.user_function_permission
                    (user_id, function_code, permission_state, data_scope_json, updated_by)
                VALUES (@user_id, @function_code, @state, @scope, @updated_by);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@function_code", SqlDbType.VarChar, 100).Value = functionCode;
        command.Parameters.Add("@state", SqlDbType.VarChar, 10).Value = state;
        command.Parameters.Add("@scope", SqlDbType.NVarChar, -1).Value =
            scopeJson is null ? DBNull.Value : scopeJson;
        command.Parameters.Add("@updated_by", SqlDbType.NVarChar, 100).Value = audit.ActorUserId;
        command.Parameters.Add("@row_version", SqlDbType.Timestamp).Value =
            string.IsNullOrWhiteSpace(request.ExpectedRowVersion)
                ? DBNull.Value : RowVersions.Decode(request.ExpectedRowVersion);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.ExpectedRowVersion) && updated == 0)
            throw new ApiException(StatusCodes.Status409Conflict,
                "DATA_CHANGED", "权限数据已变化，请刷新后重试。");
        await InsertAuditAsync(connection, transaction, "PERMISSION_UPDATED", audit, userId,
            new { functionCode, state = before.State, dataScope = before.DataScopeJson },
            new { functionCode, state, dataScope = scopeJson }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<JsonElement?> GetDataScopeAsync(
        string userId,
        string functionCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.data_scope_json
            FROM dbo.application_user u
            LEFT JOIN dbo.user_function_permission p
              ON p.user_id=u.user_id AND p.function_code=@function_code
            WHERE u.user_id=@user_id AND u.is_enabled=1;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@function_code", SqlDbType.VarChar, 100).Value = functionCode;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull) return null;
        using var document = JsonDocument.Parse(Convert.ToString(value)!);
        return document.RootElement.Clone();
    }

    public async Task UnlockUserAsync(
        string userId,
        UserAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE dbo.application_user
            SET failed_attempt_count=0, locked_until=NULL,
                updated_by=@actor, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE user_id=@user_id;
            """, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 100).Value = audit.ActorUserId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new ApiException(StatusCodes.Status404NotFound, "USER_NOT_FOUND", "用户不存在。");
        await InsertAuditAsync(connection, transaction, "ACCOUNT_UNLOCKED", audit, userId,
            null, new { unlocked = true }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> ResetPreferencesAsync(
        string userId,
        UserAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureUserExistsAsync(connection, transaction, userId, cancellationToken);
        await using var command = new SqlCommand(
            "DELETE FROM dbo.user_preference WHERE user_id=@user_id;", connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await InsertAuditAsync(connection, transaction, "PREFERENCES_RESET", audit, userId,
            null, new { deletedCount = count }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task RecordSessionRevocationAsync(
        string userId,
        int revokedCount,
        UserAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureUserExistsAsync(connection, transaction, userId, cancellationToken);
        await InsertAuditAsync(connection, transaction, "SESSIONS_REVOKED", audit, userId,
            null, new { revokedCount }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserAdminAuditItem>> ListAuditAsync(
        string? userId,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 200);
        const string sql = """
            SELECT TOP (@limit) a.audit_id, a.action_code, a.actor_user_id,
                   actor.display_name, a.target_user_id, target.display_name,
                   a.request_id, a.created_at
            FROM dbo.user_admin_audit a
            JOIN dbo.application_user actor ON actor.user_id=a.actor_user_id
            JOIN dbo.application_user target ON target.user_id=a.target_user_id
            WHERE @user_id IS NULL OR a.target_user_id=@user_id
            ORDER BY a.created_at DESC, a.audit_id DESC;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(userId) ? DBNull.Value : userId.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<UserAdminAuditItem>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new UserAdminAuditItem(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                ToBeijingOffset(reader.GetDateTime(7))));
        return result;
    }

    public async Task<UserPreferenceItem?> GetPreferenceAsync(
        string userId,
        string key,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT preference_key, value_json, preference_version,
                   updated_by_device, updated_at, row_version
            FROM dbo.user_preference
            WHERE user_id=@user_id AND preference_key=@key;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@key", SqlDbType.VarChar, 100).Value = ValidatePreferenceKey(key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPreference(reader) : null;
    }

    public async Task<UserPreferenceItem> UpsertPreferenceAsync(
        string userId,
        string key,
        UpdateUserPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        key = ValidatePreferenceKey(key);
        if (request.Value.ValueKind == JsonValueKind.Undefined)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PREFERENCE_VALUE_REQUIRED", "漫游数据不能为空。");
        var valueJson = request.Value.GetRawText();
        if (valueJson.Length > 100000)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PREFERENCE_TOO_LARGE", "漫游数据过大。");
        const string sql = """
            UPDATE dbo.user_preference
            SET value_json=@value_json, preference_version=preference_version+1,
                updated_by_device=@device_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            OUTPUT inserted.preference_key, inserted.value_json, inserted.preference_version,
                   inserted.updated_by_device, inserted.updated_at, inserted.row_version
            WHERE user_id=@user_id AND preference_key=@key
              AND (@row_version IS NULL OR row_version=@row_version);
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using (var command = new SqlCommand(sql, connection))
        {
            AddPreferenceParameters(command, userId, key, valueJson, request);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) return ReadPreference(reader);
        }
        if (!string.IsNullOrWhiteSpace(request.ExpectedRowVersion))
            throw new ApiException(StatusCodes.Status409Conflict,
                "DATA_CHANGED", "漫游数据已变化，请刷新后重试。");

        const string insertSql = """
            INSERT INTO dbo.user_preference
                (user_id, preference_key, value_json, updated_by_device)
            OUTPUT inserted.preference_key, inserted.value_json, inserted.preference_version,
                   inserted.updated_by_device, inserted.updated_at, inserted.row_version
            VALUES (@user_id, @key, @value_json, @device_id);
            """;
        try
        {
            await using var insert = new SqlCommand(insertSql, connection);
            AddPreferenceParameters(insert, userId, key, valueJson, request);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return ReadPreference(reader);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // Another request may have created the same preference after our initial UPDATE.
            // Without an expected row version, preference writes use last-write-wins semantics.
            await using var retry = new SqlCommand(sql, connection);
            AddPreferenceParameters(retry, userId, key, valueJson, request);
            await using var reader = await retry.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) return ReadPreference(reader);

            throw new ApiException(StatusCodes.Status409Conflict,
                "DATA_CHANGED", "漫游数据已变化，请刷新后重试。");
        }
    }

    public async Task<ErpOperator> GetErpOperatorAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT erp_operator_code, erp_operator_name
            FROM dbo.application_user
            WHERE user_id=@user_id AND is_enabled=1;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1))
            throw new ApiException(StatusCodes.Status409Conflict,
                "ERP_OPERATOR_NOT_CONFIGURED", "当前用户未配置 ERP 制单人。");
        return new ErpOperator(reader.GetString(0), reader.GetString(1));
    }

    public async Task<bool> HasFunctionAccessAsync(
        string userId,
        string functionCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1
                FROM dbo.application_user u
                JOIN dbo.system_function f ON f.function_code=@function_code AND f.is_enabled=1
                LEFT JOIN dbo.user_function_permission p
                  ON p.user_id=u.user_id AND p.function_code=f.function_code
                WHERE u.user_id=@user_id AND u.is_enabled=1
                  AND ((u.is_admin=1 AND ISNULL(p.permission_state, 'INHERIT') <> 'DENY')
                       OR (u.is_admin=0 AND p.permission_state='ALLOW'))
            ) THEN 1 ELSE 0 END);
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@function_code", SqlDbType.VarChar, 100).Value = functionCode;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<UserItem?> GetUserForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT user_id, login_name, display_name, auth_source,
                   erp_operator_code, erp_operator_name, is_admin, is_enabled,
                   last_login_at, failed_attempt_count, locked_until,
                   password_changed_at, row_version
            FROM dbo.application_user WITH (UPDLOCK,HOLDLOCK)
            WHERE user_id=@user_id;
            """, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    private static async Task<PermissionSnapshot> GetPermissionSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string userId,
        string functionCode,
        CancellationToken cancellationToken)
    {
        await EnsureUserExistsAsync(connection, transaction, userId, cancellationToken);
        await using var command = new SqlCommand("""
            SELECT permission_state, data_scope_json
            FROM dbo.user_function_permission WITH (UPDLOCK,HOLDLOCK)
            WHERE user_id=@user_id AND function_code=@function_code;
            """, connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@function_code", SqlDbType.VarChar, 100).Value = functionCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PermissionSnapshot(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : new PermissionSnapshot("INHERIT", null);
    }

    private static async Task EnsureUserExistsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT_BIG(1) FROM dbo.application_user WHERE user_id=@user_id;", connection, transaction);
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
            throw new ApiException(StatusCodes.Status404NotFound, "USER_NOT_FOUND", "用户不存在。");
    }

    private static async Task InsertAuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string action,
        UserAdminAuditContext audit,
        string targetUserId,
        object? before,
        object? after,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO dbo.user_admin_audit
                (action_code, actor_user_id, target_user_id, before_json, after_json,
                 request_id, client_ip, user_agent)
            VALUES
                (@action, @actor, @target, @before, @after, @request_id, @client_ip, @user_agent);
            """, connection, transaction);
        command.Parameters.Add("@action", SqlDbType.VarChar, 40).Value = action;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 100).Value = audit.ActorUserId;
        command.Parameters.Add("@target", SqlDbType.NVarChar, 100).Value = targetUserId;
        command.Parameters.Add("@before", SqlDbType.NVarChar, -1).Value =
            before is null ? DBNull.Value : JsonSerializer.Serialize(before);
        command.Parameters.Add("@after", SqlDbType.NVarChar, -1).Value =
            after is null ? DBNull.Value : JsonSerializer.Serialize(after);
        command.Parameters.Add("@request_id", SqlDbType.NVarChar, 100).Value = audit.RequestId;
        command.Parameters.Add("@client_ip", SqlDbType.NVarChar, 64).Value =
            string.IsNullOrWhiteSpace(audit.ClientIp) ? DBNull.Value : audit.ClientIp;
        command.Parameters.Add("@user_agent", SqlDbType.NVarChar, 500).Value =
            string.IsNullOrWhiteSpace(audit.UserAgent) ? DBNull.Value : audit.UserAgent[..Math.Min(500, audit.UserAgent.Length)];
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object UserSnapshot(UserItem user) => new
    {
        user.Id,
        user.LoginName,
        user.Name,
        user.AuthSource,
        user.ErpOperatorCode,
        user.ErpOperatorName,
        user.IsAdmin,
        user.IsEnabled,
        user.FailedAttemptCount,
        user.LockedUntil
    };

    private static SessionUser ToSessionUser(string id, string name, bool isAdmin) =>
        new(id, name, isAdmin ? ["admin"] : ["user"]);

    private sealed record PermissionSnapshot(string State, string? DataScopeJson);

    private static FunctionItem ReadFunction(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9),
        RowVersions.Encode((byte[])reader[10]));

    private static UserItem ReadUser(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetBoolean(6), reader.GetBoolean(7),
        reader.IsDBNull(8) ? null : ToBeijingOffset(reader.GetDateTime(8)),
        reader.GetInt32(9),
        reader.IsDBNull(10) ? null : ToBeijingOffset(reader.GetDateTime(10)),
        reader.IsDBNull(11) ? null : ToBeijingOffset(reader.GetDateTime(11)),
        RowVersions.Encode((byte[])reader[12]));

    private static UserPreferenceItem ReadPreference(SqlDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(1));
        return new UserPreferenceItem(
            reader.GetString(0), document.RootElement.Clone(), reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ToBeijingOffset(reader.GetDateTime(4)), RowVersions.Encode((byte[])reader[5]));
    }

    private static (string Id, string Login, string Name, string Source, string? ErpCode, string? ErpName)
        ValidateUser(string id, string login, string name, string source, string? erpCode, string? erpName)
    {
        id = id.Trim(); login = login.Trim(); name = name.Trim(); source = source.Trim().ToUpperInvariant();
        erpCode = NullIfWhiteSpace(erpCode); erpName = NullIfWhiteSpace(erpName);
        if (id.Length is < 1 or > 100 || login.Length is < 1 or > 100 || name.Length is < 1 or > 100 ||
            source is not ("DEVELOPMENT" or "LOCAL" or "EXTERNAL") ||
            erpCode?.Length > 100 || erpName?.Length > 100 || (erpCode is null) != (erpName is null))
            throw new ApiException(StatusCodes.Status400BadRequest, "USER_INVALID", "用户信息无效。");
        return (id, login, name, source, erpCode, erpName);
    }

    private static void AddUserParameters(
        SqlCommand command,
        (string Id, string Login, string Name, string Source, string? ErpCode, string? ErpName) values,
        bool isAdmin,
        bool isEnabled,
        string updatedBy)
    {
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = values.Id;
        command.Parameters.Add("@login_name", SqlDbType.NVarChar, 100).Value = values.Login;
        command.Parameters.Add("@display_name", SqlDbType.NVarChar, 100).Value = values.Name;
        command.Parameters.Add("@auth_source", SqlDbType.VarChar, 20).Value = values.Source;
        command.Parameters.Add("@erp_code", SqlDbType.NVarChar, 100).Value = values.ErpCode is null ? DBNull.Value : values.ErpCode;
        command.Parameters.Add("@erp_name", SqlDbType.NVarChar, 100).Value = values.ErpName is null ? DBNull.Value : values.ErpName;
        command.Parameters.Add("@is_admin", SqlDbType.Bit).Value = isAdmin;
        command.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = isEnabled;
        command.Parameters.Add("@updated_by", SqlDbType.NVarChar, 100).Value = updatedBy;
    }

    private static void AddCredentialParameters(
        SqlCommand command,
        PasswordCredentialValue? credential)
    {
        command.Parameters.Add("@hash", SqlDbType.VarBinary, 64).Value =
            credential is null ? DBNull.Value : credential.Hash;
        command.Parameters.Add("@salt", SqlDbType.VarBinary, 32).Value =
            credential is null ? DBNull.Value : credential.Salt;
        command.Parameters.Add("@iterations", SqlDbType.Int).Value =
            credential is null ? DBNull.Value : credential.Iterations;
    }

    private static void AddPreferenceParameters(
        SqlCommand command,
        string userId,
        string key,
        string valueJson,
        UpdateUserPreferenceRequest request)
    {
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@key", SqlDbType.VarChar, 100).Value = key;
        command.Parameters.Add("@value_json", SqlDbType.NVarChar, -1).Value = valueJson;
        command.Parameters.Add("@device_id", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(request.DeviceId) ? DBNull.Value : request.DeviceId.Trim();
        if (command.CommandText.Contains("@row_version", StringComparison.Ordinal))
            command.Parameters.Add("@row_version", SqlDbType.Timestamp).Value =
                string.IsNullOrWhiteSpace(request.ExpectedRowVersion)
                    ? DBNull.Value : RowVersions.Decode(request.ExpectedRowVersion);
    }

    private static string ValidatePreferenceKey(string key)
    {
        key = key.Trim();
        if (key.Length is < 1 or > 100 || key.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PREFERENCE_KEY_INVALID", "漫游数据标识无效。");
        return key;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset ToBeijingOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), BusinessClock.BeijingOffset);
}

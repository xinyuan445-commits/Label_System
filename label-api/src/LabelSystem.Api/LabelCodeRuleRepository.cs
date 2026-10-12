using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public sealed class LabelCodeRuleRepository(
    SqlConnectionFactory connections,
    LabelCodeRuleEngine engine)
{
    public async Task<IReadOnlyList<LabelCodeRuleItem>> ListAsync(
        bool catalogOnly,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.id, r.rule_code, r.rule_name, r.object_type, r.priority,
                   r.is_enabled, r.updated_by, r.updated_at, r.row_version,
                   v.id, v.version_no, v.version_status, v.spec_json, v.spec_sha256,
                   v.created_by, v.created_at, v.updated_by, v.updated_at,
                   v.activated_by, v.activated_at, v.retired_by, v.retired_at, v.row_version
            FROM dbo.label_code_rule AS r
            INNER JOIN dbo.label_code_rule_version AS v ON v.rule_id = r.id
            WHERE @catalog_only = 0
               OR (r.is_enabled = 1 AND v.version_status IN ('ACTIVE', 'RETIRED'))
            ORDER BY r.priority, r.id, v.version_no DESC;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@catalog_only", SqlDbType.Bit).Value = catalogOnly;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rules = new List<RuleBuilder>();
        var byId = new Dictionary<long, RuleBuilder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var ruleId = reader.GetInt64(0);
            if (!byId.TryGetValue(ruleId, out var rule))
            {
                rule = new RuleBuilder(
                    ruleId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetBoolean(5), reader.GetString(6),
                    ToBeijingOffset(reader.GetDateTime(7)), RowVersions.Encode((byte[])reader[8]));
                byId.Add(ruleId, rule);
                rules.Add(rule);
            }
            using var document = JsonDocument.Parse(reader.GetString(12));
            rule.Versions.Add(new LabelCodeRuleVersionItem(
                reader.GetInt64(9), reader.GetInt32(10), reader.GetString(11),
                document.RootElement.Clone(), reader.GetString(13), reader.GetString(14),
                ToBeijingOffset(reader.GetDateTime(15)), reader.GetString(16),
                ToBeijingOffset(reader.GetDateTime(17)),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : ToBeijingOffset(reader.GetDateTime(19)),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : ToBeijingOffset(reader.GetDateTime(21)),
                RowVersions.Encode((byte[])reader[22])));
        }
        return rules.Select(rule => rule.Build()).ToArray();
    }

    public async Task<LabelCodeRuleItem> CreateAsync(
        CreateLabelCodeRuleRequest request,
        string userId,
        CancellationToken cancellationToken)
    {
        var values = ValidateRule(request.Code, request.Name, request.ObjectType, request.Priority);
        var spec = engine.Validate(request.Spec);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        long ruleId;
        try
        {
            const string insertRuleSql = """
                INSERT INTO dbo.label_code_rule
                    (rule_code, rule_name, object_type, priority, is_enabled, created_by, updated_by)
                OUTPUT inserted.id
                VALUES (@code, @name, @object_type, @priority, 1, @user_id, @user_id);
                """;
            await using (var command = new SqlCommand(insertRuleSql, connection, transaction))
            {
                AddRuleParameters(command, values, userId);
                ruleId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            const string insertVersionSql = """
                INSERT INTO dbo.label_code_rule_version
                    (rule_id, version_no, version_status, spec_json, spec_sha256,
                     created_by, updated_by)
                VALUES (@rule_id, 1, 'DRAFT', @spec_json, @spec_sha256,
                        @user_id, @user_id);
                """;
            await using (var command = new SqlCommand(insertVersionSql, connection, transaction))
            {
                command.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
                command.Parameters.Add("@spec_json", SqlDbType.NVarChar, -1).Value = spec.Json;
                command.Parameters.Add("@spec_sha256", SqlDbType.Char, 64).Value = spec.Sha256;
                command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ApiException(StatusCodes.Status409Conflict,
                "LABEL_CODE_RULE_EXISTS", "规则编号已经存在。");
        }
        return await GetAsync(ruleId, cancellationToken);
    }

    public async Task<LabelCodeRuleItem> SaveDraftAsync(
        long ruleId,
        SaveLabelCodeRuleDraftRequest request,
        string userId,
        CancellationToken cancellationToken)
    {
        var spec = engine.Validate(request.Spec);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        const string readRuleSql = """
            SELECT rule_code, object_type, row_version
            FROM dbo.label_code_rule WITH (UPDLOCK, HOLDLOCK)
            WHERE id=@rule_id;
            """;
        string code;
        string objectType;
        byte[] ruleVersion;
        await using (var command = new SqlCommand(readRuleSql, connection, transaction))
        {
            command.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new ApiException(StatusCodes.Status404NotFound,
                    "LABEL_CODE_RULE_NOT_FOUND", "规则不存在。");
            code = reader.GetString(0);
            objectType = reader.GetString(1);
            ruleVersion = (byte[])reader[2];
        }
        if (!ruleVersion.SequenceEqual(RowVersions.Decode(request.ExpectedRuleRowVersion)))
            throw Changed();
        var values = ValidateRule(code, request.Name, objectType, request.Priority);

        const string findDraftSql = """
            SELECT id, row_version
            FROM dbo.label_code_rule_version WITH (UPDLOCK, HOLDLOCK)
            WHERE rule_id=@rule_id AND version_status='DRAFT';
            """;
        long? draftId = null;
        byte[]? draftVersion = null;
        await using (var command = new SqlCommand(findDraftSql, connection, transaction))
        {
            command.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                draftId = reader.GetInt64(0);
                draftVersion = (byte[])reader[1];
            }
        }

        if (draftId.HasValue)
        {
            if (string.IsNullOrWhiteSpace(request.ExpectedVersionRowVersion) ||
                !draftVersion!.SequenceEqual(RowVersions.Decode(request.ExpectedVersionRowVersion)))
                throw Changed();
            const string updateVersionSql = """
                UPDATE dbo.label_code_rule_version
                SET spec_json=@spec_json, spec_sha256=@spec_sha256,
                    updated_by=@user_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE id=@version_id;
                """;
            await using var updateVersion = new SqlCommand(updateVersionSql, connection, transaction);
            updateVersion.Parameters.Add("@spec_json", SqlDbType.NVarChar, -1).Value = spec.Json;
            updateVersion.Parameters.Add("@spec_sha256", SqlDbType.Char, 64).Value = spec.Sha256;
            updateVersion.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            updateVersion.Parameters.Add("@version_id", SqlDbType.BigInt).Value = draftId.Value;
            await updateVersion.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.ExpectedVersionRowVersion))
                throw Changed();
            const string insertDraftSql = """
                INSERT INTO dbo.label_code_rule_version
                    (rule_id, version_no, version_status, spec_json, spec_sha256,
                     created_by, updated_by)
                SELECT @rule_id, ISNULL(MAX(version_no),0)+1, 'DRAFT',
                       @spec_json, @spec_sha256, @user_id, @user_id
                FROM dbo.label_code_rule_version WITH (UPDLOCK, HOLDLOCK)
                WHERE rule_id=@rule_id;
                """;
            await using var insertDraft = new SqlCommand(insertDraftSql, connection, transaction);
            insertDraft.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
            insertDraft.Parameters.Add("@spec_json", SqlDbType.NVarChar, -1).Value = spec.Json;
            insertDraft.Parameters.Add("@spec_sha256", SqlDbType.Char, 64).Value = spec.Sha256;
            insertDraft.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            await insertDraft.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateRuleSql = """
            UPDATE dbo.label_code_rule
            SET rule_name=@name, priority=@priority, is_enabled=@is_enabled,
                updated_by=@user_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@rule_id;
            """;
        await using (var updateRule = new SqlCommand(updateRuleSql, connection, transaction))
        {
            updateRule.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = values.Name;
            updateRule.Parameters.Add("@priority", SqlDbType.Int).Value = values.Priority;
            updateRule.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = request.IsEnabled;
            updateRule.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            updateRule.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
            await updateRule.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(ruleId, cancellationToken);
    }

    public async Task<LabelCodeRuleItem> ActivateAsync(
        long ruleId,
        long versionId,
        ChangeLabelCodeRuleVersionRequest request,
        string userId,
        CancellationToken cancellationToken)
    {
        await ChangeVersionAsync(ruleId, versionId, request, userId, true, cancellationToken);
        return await GetAsync(ruleId, cancellationToken);
    }

    public async Task<LabelCodeRuleItem> RetireAsync(
        long ruleId,
        long versionId,
        ChangeLabelCodeRuleVersionRequest request,
        string userId,
        CancellationToken cancellationToken)
    {
        await ChangeVersionAsync(ruleId, versionId, request, userId, false, cancellationToken);
        return await GetAsync(ruleId, cancellationToken);
    }

    public async Task<LabelCodeRuleItem> GetAsync(long ruleId, CancellationToken cancellationToken)
    {
        var rules = await ListAsync(false, cancellationToken);
        return rules.FirstOrDefault(rule => rule.Id == ruleId) ??
               throw new ApiException(StatusCodes.Status404NotFound,
                   "LABEL_CODE_RULE_NOT_FOUND", "规则不存在。");
    }

    private async Task ChangeVersionAsync(
        long ruleId,
        long versionId,
        ChangeLabelCodeRuleVersionRequest request,
        string userId,
        bool activate,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        const string ruleSql = """
            SELECT row_version FROM dbo.label_code_rule WITH (UPDLOCK, HOLDLOCK) WHERE id=@rule_id;
            """;
        byte[] ruleVersion;
        await using (var command = new SqlCommand(ruleSql, connection, transaction))
        {
            command.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not byte[] bytes)
                throw new ApiException(StatusCodes.Status404NotFound,
                    "LABEL_CODE_RULE_NOT_FOUND", "规则不存在。");
            ruleVersion = bytes;
        }
        if (!ruleVersion.SequenceEqual(RowVersions.Decode(request.ExpectedRuleRowVersion)))
            throw Changed();

        const string versionSql = """
            SELECT version_status, row_version, spec_json
            FROM dbo.label_code_rule_version WITH (UPDLOCK, HOLDLOCK)
            WHERE id=@version_id AND rule_id=@rule_id;
            """;
        string status;
        byte[] version;
        string specJson;
        await using (var command = new SqlCommand(versionSql, connection, transaction))
        {
            command.Parameters.Add("@version_id", SqlDbType.BigInt).Value = versionId;
            command.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new ApiException(StatusCodes.Status404NotFound,
                    "LABEL_CODE_RULE_VERSION_NOT_FOUND", "规则版本不存在。");
            status = reader.GetString(0);
            version = (byte[])reader[1];
            specJson = reader.GetString(2);
        }
        if (!version.SequenceEqual(RowVersions.Decode(request.ExpectedVersionRowVersion)))
            throw Changed();

        if (activate)
        {
            if (status != "DRAFT")
                throw new ApiException(StatusCodes.Status409Conflict,
                    "LABEL_CODE_RULE_VERSION_STATE_INVALID", "只有草稿版本可以启用。");
            using var specDocument = JsonDocument.Parse(specJson);
            engine.Validate(specDocument.RootElement);
            const string retireSql = """
                UPDATE dbo.label_code_rule_version
                SET version_status='RETIRED', retired_by=@user_id,
                    retired_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                    updated_by=@user_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE rule_id=@rule_id AND version_status='ACTIVE';
                """;
            await using (var retire = new SqlCommand(retireSql, connection, transaction))
            {
                retire.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
                retire.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
                await retire.ExecuteNonQueryAsync(cancellationToken);
            }
            const string activateSql = """
                UPDATE dbo.label_code_rule_version
                SET version_status='ACTIVE', activated_by=@user_id,
                    activated_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                    updated_by=@user_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE id=@version_id;
                """;
            await using var activateCommand = new SqlCommand(activateSql, connection, transaction);
            activateCommand.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            activateCommand.Parameters.Add("@version_id", SqlDbType.BigInt).Value = versionId;
            await activateCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            if (status != "ACTIVE")
                throw new ApiException(StatusCodes.Status409Conflict,
                    "LABEL_CODE_RULE_VERSION_STATE_INVALID", "只有生效版本可以停止生成。");
            const string retireSql = """
                UPDATE dbo.label_code_rule_version
                SET version_status='RETIRED', retired_by=@user_id,
                    retired_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                    updated_by=@user_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                WHERE id=@version_id;
                """;
            await using var retireCommand = new SqlCommand(retireSql, connection, transaction);
            retireCommand.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            retireCommand.Parameters.Add("@version_id", SqlDbType.BigInt).Value = versionId;
            await retireCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string touchSql = """
            UPDATE dbo.label_code_rule
            SET updated_by=@user_id, updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@rule_id;
            """;
        await using (var touch = new SqlCommand(touchSql, connection, transaction))
        {
            touch.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
            touch.Parameters.Add("@rule_id", SqlDbType.BigInt).Value = ruleId;
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static (string Code, string Name, string ObjectType, int Priority) ValidateRule(
        string code,
        string name,
        string objectType,
        int priority)
    {
        code = code.Trim().ToUpperInvariant();
        name = name.Trim();
        objectType = objectType.Trim().ToUpperInvariant();
        if (code.Length is < 1 or > 50 ||
            code.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "LABEL_CODE_RULE_CODE_INVALID", "规则编号无效。");
        if (name.Length is < 1 or > 100 ||
            objectType is not ("PRODUCT_LABEL" or "PALLET" or "INVENTORY_OBJECT" or "EXTERNAL") ||
            priority is < 0 or > 10000)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "LABEL_CODE_RULE_INVALID", "规则内容无效。");
        return (code, name, objectType, priority);
    }

    private static void AddRuleParameters(
        SqlCommand command,
        (string Code, string Name, string ObjectType, int Priority) values,
        string userId)
    {
        command.Parameters.Add("@code", SqlDbType.VarChar, 50).Value = values.Code;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = values.Name;
        command.Parameters.Add("@object_type", SqlDbType.VarChar, 20).Value = values.ObjectType;
        command.Parameters.Add("@priority", SqlDbType.Int).Value = values.Priority;
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
    }

    private static ApiException Changed() =>
        new(StatusCodes.Status409Conflict, "DATA_CHANGED", "规则已变化，请刷新后重试。");

    private static DateTimeOffset ToBeijingOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), BusinessClock.BeijingOffset);

    private sealed class RuleBuilder(
        long id,
        string code,
        string name,
        string objectType,
        int priority,
        bool isEnabled,
        string updatedBy,
        DateTimeOffset updatedAt,
        string rowVersion)
    {
        public List<LabelCodeRuleVersionItem> Versions { get; } = [];

        public LabelCodeRuleItem Build() => new(
            id, code, name, objectType, priority, isEnabled,
            updatedBy, updatedAt, rowVersion, Versions);
    }
}

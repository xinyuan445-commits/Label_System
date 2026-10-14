using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabelSystem.Api;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var externalConfigPath = Environment.GetEnvironmentVariable("LABELSYSTEM_CONFIG_PATH");
if (!string.IsNullOrWhiteSpace(externalConfigPath))
    builder.Configuration.AddJsonFile(externalConfigPath, optional: false, reloadOnChange: true);

builder.Services.Configure<LabelSystemOptions>(builder.Configuration.GetSection("LabelSystem"));
builder.Services.Configure<U8GatewayOptions>(builder.Configuration.GetSection("U8Gateway"));
builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection("Archive"));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection("Cors"));
builder.Services.Configure<ManagementAccessOptions>(builder.Configuration.GetSection("ManagementAccess"));
builder.Services.AddSingleton<DevelopmentSessionStore>();
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddScoped<LabelSystemRepository>();
builder.Services.AddScoped<MaterialOutboundRepository>();
builder.Services.AddScoped<MaterialOutboundService>();
builder.Services.AddScoped<MaterialOutboundExecutionRepository>();
builder.Services.AddScoped<MaterialOutboundExecutionService>();
builder.Services.AddScoped<MaterialOutboundSubmissionService>();
builder.Services.AddScoped<DocumentPrepareRepository>();
builder.Services.AddScoped<DocumentPrepareService>();
builder.Services.AddScoped<ReferenceDataRepository>();
builder.Services.AddScoped<ReferenceDataSyncService>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<UserDataScopeService>();
builder.Services.AddScoped<DocumentDraftRepository>();
builder.Services.AddSingleton<LabelCodeRuleEngine>();
builder.Services.AddScoped<LabelCodeRuleRepository>();
builder.Services.AddScoped<ManualReturnValidationService>();
builder.Services.AddSingleton<OperationArchiveService>();
builder.Services.AddHostedService<DocumentDraftCleanupService>();
builder.Services.AddHttpClient(nameof(U8GatewayClient), client => client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddScoped<U8GatewayClient>();

var cors = builder.Configuration.GetSection("Cors").Get<CorsOptions>() ?? new CorsOptions();
builder.Services.AddCors(config => config.AddPolicy("H5", policy =>
{
    if (cors.AllowedOrigins.Length > 0)
        policy.WithOrigins(cors.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors("H5");
app.UseMiddleware<SessionAuthenticationMiddleware>();

app.MapGet("/api/v1/health", async (
    HttpContext context,
    LabelSystemRepository repository,
    IOptionsMonitor<LabelSystemOptions> options,
    CancellationToken cancellationToken) =>
{
    var databaseReady = false;
    try { databaseReady = await repository.CanConnectAsync(cancellationToken); }
    catch { /* 健康接口不暴露数据库异常细节。 */ }
    var current = options.CurrentValue;
    var data = new
    {
        status = databaseReady ? "ready" : "degraded",
        environment = current.Environment,
        version = current.Version,
        databaseReady,
        u8WritesEnabled = current.U8WritesEnabled
    };
    return databaseReady
        ? ApiResults.Ok(context, data)
        : ApiResults.Error(context, StatusCodes.Status503ServiceUnavailable,
            "DATABASE_UNAVAILABLE", "数据库连接失败。");
});

app.MapPost("/api/v1/auth/login", async (
    HttpContext context,
    LoginRequest request,
    DevelopmentSessionStore sessions,
    UserRepository users,
    CancellationToken cancellationToken,
    IOptionsMonitor<LabelSystemOptions> options) =>
{
    var current = options.CurrentValue;
    var developmentAdmin = current.EnableDevelopmentLogin &&
        string.Equals(current.Environment, "development", StringComparison.OrdinalIgnoreCase) &&
        FixedTimeEquals(request.Username, "admin") && FixedTimeEquals(request.Password, "admin");
    var user = developmentAdmin
        ? await users.FindSessionUserByLoginAsync(request.Username, true, cancellationToken)
        : await users.AuthenticateLocalUserAsync(request.Username, request.Password, cancellationToken);
    if (user is null)
        return ApiResults.Error(context, StatusCodes.Status401Unauthorized,
            "LOGIN_FAILED", "账号或密码错误。");
    var result = sessions.Create(user);
    context.Response.Cookies.Append("LabelSystem.Session", result.Token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Expires = result.ExpiresAt,
        Path = "/"
    });
    return ApiResults.Ok(context, result);
});

app.MapPost("/api/v1/auth/logout", (HttpContext context, DevelopmentSessionStore sessions) =>
{
    sessions.Remove(SessionAuthenticationMiddleware.ReadToken(context));
    context.Response.Cookies.Delete("LabelSystem.Session", new CookieOptions { Path = "/" });
    return ApiResults.Ok(context, new { loggedOut = true });
});

app.MapGet("/api/v1/auth/me", (HttpContext context) =>
    ApiResults.Ok(context, RequestContext.User(context)));

app.MapGet("/api/v1/auth/management-access", (
    HttpContext context,
    DevelopmentSessionStore sessions) =>
    ApiResults.Ok(context, new
    {
        allowed = RequestContext.IsAdmin(context) ||
                  sessions.HasManagementAccess(SessionAuthenticationMiddleware.ReadToken(context))
    }));

app.MapPost("/api/v1/auth/management-access", (
    HttpContext context,
    ManagementAccessRequest request,
    DevelopmentSessionStore sessions,
    IOptionsMonitor<ManagementAccessOptions> options) =>
{
    var configured = options.CurrentValue.PasswordHash?.Trim();
    if (string.IsNullOrWhiteSpace(configured))
        throw new ApiException(StatusCodes.Status503ServiceUnavailable,
            "MANAGEMENT_ACCESS_NOT_CONFIGURED", "管理密码尚未配置。");
    if (string.IsNullOrEmpty(request.Password) ||
        !FixedTimeEquals(Hashing.Sha256(request.Password), configured.ToUpperInvariant()))
        throw new ApiException(StatusCodes.Status403Forbidden,
            "MANAGEMENT_PASSWORD_INVALID", "管理密码错误。");
    return ApiResults.Ok(context, sessions.ElevateManagementAccess(
        SessionAuthenticationMiddleware.ReadToken(context), options.CurrentValue.ElevationMinutes));
});

app.MapGet("/api/v1/functions", async (
    HttpContext context,
    UserRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.ListFunctionsForUserAsync(
        RequestContext.User(context).Id, cancellationToken)));

app.MapGet("/api/v1/admin/users", async (
    HttpContext context,
    UserRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.ListUsersAsync(cancellationToken));
});

app.MapPost("/api/v1/admin/users", async (
    HttpContext context,
    CreateUserRequest request,
    UserRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.CreateUserAsync(
        request, CreateUserAuditContext(context), cancellationToken));
});

app.MapPut("/api/v1/admin/users/{userId}", async (
    HttpContext context,
    string userId,
    UpdateUserRequest request,
    UserRepository repository,
    DevelopmentSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    var current = RequestContext.User(context);
    if (string.Equals(current.Id, userId, StringComparison.OrdinalIgnoreCase) &&
        (!request.IsEnabled || !request.IsAdmin))
        throw new ApiException(StatusCodes.Status409Conflict,
            "CURRENT_ADMIN_PROTECTED", "不能停用自己或取消自己的管理员权限。");
    var result = await repository.UpdateUserAsync(
        userId, request, CreateUserAuditContext(context), cancellationToken);
    if (!result.IsEnabled) sessions.RemoveUser(userId);
    return ApiResults.Ok(context, result);
});

app.MapPut("/api/v1/admin/users/{userId}/password", async (
    HttpContext context,
    string userId,
    ResetUserPasswordRequest request,
    UserRepository repository,
    DevelopmentSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    await repository.SetPasswordAsync(
        userId, request.Password, CreateUserAuditContext(context), cancellationToken);
    var revoked = sessions.RemoveUser(userId);
    return ApiResults.Ok(context, new { updated = true, revoked });
});

app.MapGet("/api/v1/admin/users/{userId}/functions", async (
    HttpContext context,
    string userId,
    UserRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.ListPermissionsAsync(userId, cancellationToken));
});

app.MapPut("/api/v1/admin/users/{userId}/functions/{functionCode}", async (
    HttpContext context,
    string userId,
    string functionCode,
    UpdateUserFunctionPermissionRequest request,
    UserRepository repository,
    UserDataScopeService dataScopes,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    await dataScopes.ValidateForUpdateAsync(functionCode, request.DataScope, cancellationToken);
    await repository.UpdatePermissionAsync(userId, functionCode, request,
        CreateUserAuditContext(context), cancellationToken);
    return ApiResults.Ok(context, new { updated = true });
});

app.MapPost("/api/v1/admin/users/{userId}/unlock", async (
    HttpContext context,
    string userId,
    UserRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    await repository.UnlockUserAsync(userId, CreateUserAuditContext(context), cancellationToken);
    return ApiResults.Ok(context, new { unlocked = true });
});

app.MapPost("/api/v1/admin/users/{userId}/sessions/revoke", async (
    HttpContext context,
    string userId,
    UserRepository repository,
    DevelopmentSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    var revoked = sessions.RemoveUser(userId);
    await repository.RecordSessionRevocationAsync(
        userId, revoked, CreateUserAuditContext(context), cancellationToken);
    return ApiResults.Ok(context, new { revoked });
});

app.MapPost("/api/v1/admin/users/{userId}/preferences/reset", async (
    HttpContext context,
    string userId,
    UserRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    var deleted = await repository.ResetPreferencesAsync(
        userId, CreateUserAuditContext(context), cancellationToken);
    return ApiResults.Ok(context, new { deleted });
});

app.MapGet("/api/v1/admin/users/audit", async (
    HttpContext context,
    string? userId,
    int? limit,
    UserRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.ListAuditAsync(
        userId, limit ?? 100, cancellationToken));
});

app.MapGet("/api/v1/users/me/preferences/{key}", async (
    HttpContext context,
    string key,
    UserRepository repository,
    CancellationToken cancellationToken) =>
{
    var item = await repository.GetPreferenceAsync(
        RequestContext.User(context).Id, key, cancellationToken);
    return item is null
        ? ApiResults.Error(context, StatusCodes.Status404NotFound,
            "PREFERENCE_NOT_FOUND", "漫游数据不存在。")
        : ApiResults.Ok(context, item);
});

app.MapPut("/api/v1/users/me/preferences/{key}", async (
    HttpContext context,
    string key,
    UpdateUserPreferenceRequest request,
    UserRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.UpsertPreferenceAsync(
        RequestContext.User(context).Id, key, request, cancellationToken)));

app.MapGet("/api/v1/admin/functions", async (
    HttpContext context,
    LabelSystemRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.ListFunctionsAsync(false, cancellationToken));
});

app.MapPut("/api/v1/admin/functions/{code}", async (
    HttpContext context,
    string code,
    UpdateFunctionRequest request,
    LabelSystemRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    var user = RequestContext.User(context);
    return ApiResults.Ok(context,
        await repository.UpdateFunctionAsync(code, request, user.Id, cancellationToken));
});

app.MapGet("/api/v1/settings", async (
    HttpContext context,
    string? documentType,
    LabelSystemRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.ListSettingsAsync(documentType, cancellationToken)));

app.MapPut("/api/v1/admin/settings/{scope}/{key}", async (
    HttpContext context,
    string scope,
    string key,
    UpdateSettingRequest request,
    LabelSystemRepository repository,
    CancellationToken cancellationToken) =>
{
    if (string.Equals(scope, "document", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(key, "field_input_rules", StringComparison.OrdinalIgnoreCase))
        RequireRealAdmin(context);
    else
        RequireAdmin(context);
    ValidateSetting(scope, key, request);
    var user = RequestContext.User(context);
    return ApiResults.Ok(context,
        await repository.UpsertSettingAsync(scope, key, request, user.Id, cancellationToken));
});

app.MapGet("/api/v1/label-code-rules/capabilities", (
    HttpContext context,
    LabelCodeRuleEngine engine) =>
    ApiResults.Ok(context, new
    {
        schemaVersion = 1,
        symbologies = engine.GetSupportedSymbologies(),
        segmentKinds = new[] { "LITERAL", "SEPARATOR", "FIELD", "DATE", "SEQUENCE", "CHECKSUM" },
        fields = new[]
        {
            "ERP_ACCOUNT_ID", "LABEL_TYPE", "MATERIAL_CODE", "BATCH_NO",
            "ISSUED_QUANTITY", "UNIT_CODE", "MANUFACTURE_DATE", "SOURCE_DOCUMENT_NO",
            "LABEL_UNIQUE_VALUE"
        },
        encodings = new[] { "NONE", "PERCENT" },
        checksumAlgorithms = new[] { "SHA256_8", "EAN_MOD10" }
    }));

app.MapGet("/api/v1/label-code-rules/catalog", async (
    HttpContext context,
    LabelCodeRuleRepository repository,
    CancellationToken cancellationToken) =>
{
    var rules = await repository.ListAsync(true, cancellationToken);
    var fingerprint = string.Join('|', rules.SelectMany(rule => rule.Versions.Select(version =>
        $"{rule.Id}:{rule.Code}:{rule.ObjectType}:{rule.Priority}:{rule.IsEnabled}:{rule.RowVersion}:" +
        $"{version.Id}:{version.Version}:{version.Status}:{version.SpecSha256}:{version.RowVersion}")));
    var catalogVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
    return ApiResults.Ok(context,
        new LabelCodeRuleCatalog(catalogVersion, BusinessClock.Now, rules));
});

app.MapPost("/api/v1/label-codes/parse", async (
    HttpContext context,
    ParseLabelCodeRequest request,
    LabelCodeRuleRepository repository,
    LabelCodeRuleEngine engine,
    IOptionsMonitor<LabelSystemOptions> options,
    CancellationToken cancellationToken) =>
{
    var rules = await repository.ListAsync(true, cancellationToken);
    var matches = new List<(int Priority, ParsedLabelCodeResult Result)>();
    LabelCodeParseAttempt? accountMismatch = null;
    LabelCodeParseAttempt? matchedFailure = null;
    foreach (var rule in rules)
    {
        foreach (var version in rule.Versions
                     .OrderBy(item => item.Status == "ACTIVE" ? 0 : 1)
                     .ThenByDescending(item => item.Version))
        {
            var validated = engine.Validate(version.Spec);
            var attempt = engine.ParseCode(
                validated.Spec, request.Code, request.Symbology,
                options.CurrentValue.ErpAccountId);
            if (attempt.Success && attempt.Fields is not null)
            {
                matches.Add((rule.Priority, new ParsedLabelCodeResult(
                    rule.Id, rule.Code, rule.Name, rule.ObjectType,
                    version.Id, version.Version, version.Status,
                    attempt.Fields, attempt.NormalizedCode,
                    string.IsNullOrWhiteSpace(request.Symbology)
                        ? null : request.Symbology.Trim().ToUpperInvariant())));
                break;
            }
            else if (attempt.Matched && attempt.ErrorCode == "ERP_ACCOUNT_MISMATCH")
            {
                accountMismatch ??= attempt;
            }
            else if (attempt.Matched)
            {
                matchedFailure ??= attempt;
            }
        }
    }
    if (accountMismatch is not null)
        throw new ApiException(StatusCodes.Status422UnprocessableEntity,
            accountMismatch.ErrorCode!, accountMismatch.ErrorMessage!);
    if (matches.Count > 0)
    {
        var bestPriority = matches.Min(item => item.Priority);
        var preferred = matches.Where(item => item.Priority == bestPriority).ToArray();
        if (preferred.Length == 1) return ApiResults.Ok(context, preferred[0].Result);
        throw new ApiException(StatusCodes.Status409Conflict,
            "LABEL_CODE_RULE_AMBIGUOUS", "标签编码同时匹配多条规则。");
    }
    if (matchedFailure is not null)
        throw new ApiException(StatusCodes.Status422UnprocessableEntity,
            matchedFailure.ErrorCode!, matchedFailure.ErrorMessage!);
    throw new ApiException(StatusCodes.Status400BadRequest,
        "LABEL_CODE_RULE_NOT_MATCHED", "标签编码不符合已启用规则。");
});

app.MapGet("/api/v1/admin/label-code-rules", async (
    HttpContext context,
    LabelCodeRuleRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.ListAsync(false, cancellationToken));
});

app.MapPost("/api/v1/admin/label-code-rules/preview", (
    HttpContext context,
    LabelCodePreviewRequest request,
    LabelCodeRuleEngine engine,
    IOptionsMonitor<LabelSystemOptions> options) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, engine.Preview(
        request.Spec, request.Values, options.CurrentValue.ErpAccountId, request.Symbology));
});

app.MapPost("/api/v1/admin/label-code-rules", async (
    HttpContext context,
    CreateLabelCodeRuleRequest request,
    LabelCodeRuleRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.CreateAsync(
        request, RequestContext.User(context).Id, cancellationToken));
});

app.MapPut("/api/v1/admin/label-code-rules/{ruleId:long}/draft", async (
    HttpContext context,
    long ruleId,
    SaveLabelCodeRuleDraftRequest request,
    LabelCodeRuleRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.SaveDraftAsync(
        ruleId, request, RequestContext.User(context).Id, cancellationToken));
});

app.MapPost("/api/v1/admin/label-code-rules/{ruleId:long}/versions/{versionId:long}/activate", async (
    HttpContext context,
    long ruleId,
    long versionId,
    ChangeLabelCodeRuleVersionRequest request,
    LabelCodeRuleRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.ActivateAsync(
        ruleId, versionId, request, RequestContext.User(context).Id, cancellationToken));
});

app.MapPost("/api/v1/admin/label-code-rules/{ruleId:long}/versions/{versionId:long}/retire", async (
    HttpContext context,
    long ruleId,
    long versionId,
    ChangeLabelCodeRuleVersionRequest request,
    LabelCodeRuleRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.RetireAsync(
        ruleId, versionId, request, RequestContext.User(context).Id, cancellationToken));
});

app.MapPut("/api/v1/drafts/{draftId}", async (
    HttpContext context,
    string draftId,
    SyncDocumentDraftRequest request,
    DocumentDraftRepository repository,
    CancellationToken cancellationToken) =>
{
    var user = RequestContext.User(context);
    return ApiResults.Ok(context, await repository.UpsertAsync(
        draftId, request, user, RequestContext.Id(context), cancellationToken));
});

app.MapGet("/api/v1/drafts", async (
    HttpContext context,
    bool? allUsers,
    string? documentType,
    string? status,
    int? take,
    DocumentDraftRepository repository,
    CancellationToken cancellationToken) =>
{
    if (allUsers == true && !RequestContext.IsAdmin(context))
        throw new ApiException(StatusCodes.Status403Forbidden,
            "ADMIN_REQUIRED", "只有管理员可以查看其他用户的草稿。");
    return ApiResults.Ok(context, await repository.ListAsync(
        RequestContext.User(context).Id, allUsers == true, documentType, status,
        take ?? 100, cancellationToken));
});

app.MapGet("/api/v1/drafts/{draftId}", async (
    HttpContext context,
    string draftId,
    DocumentDraftRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.GetAsync(
        draftId, RequestContext.User(context).Id, RequestContext.IsAdmin(context), cancellationToken)));

app.MapDelete("/api/v1/drafts/{draftId}", async (
    HttpContext context,
    string draftId,
    [Microsoft.AspNetCore.Mvc.FromBody] DeleteDocumentDraftRequest request,
    DocumentDraftRepository repository,
    CancellationToken cancellationToken) =>
{
    var user = RequestContext.User(context);
    return ApiResults.Ok(context, await repository.DeleteAsync(
        draftId, request, user, RequestContext.Id(context), cancellationToken));
});

app.MapPost("/api/v1/drafts/device-heartbeat", async (
    HttpContext context,
    DeviceHeartbeatRequest request,
    DocumentDraftRepository repository,
    CancellationToken cancellationToken) =>
{
    await repository.UpsertDeviceStateAsync(
        request, RequestContext.User(context), cancellationToken);
    return ApiResults.Ok(context, new { updated = true });
});

app.MapGet("/api/v1/admin/draft-devices", async (
    HttpContext context,
    DocumentDraftRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireRealAdmin(context);
    return ApiResults.Ok(context, await repository.ListDevicesAsync(cancellationToken));
});

app.MapGet("/api/v1/inventory-objects/lookup", async (
    HttpContext context,
    string code,
    LabelSystemRepository repository,
    UserRepository users,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users,
        DocumentOrQueryFunctionCode(context.Request.Headers["X-Function-Code"]), cancellationToken);
    return ApiResults.Ok(context, await repository.LookupInventoryObjectAsync(code, cancellationToken));
});

app.MapGet("/api/v1/reference-data/rd-styles", async (
    HttpContext context,
    string? direction,
    bool terminalOnly,
    ReferenceDataRepository repository,
    UserRepository users,
    UserDataScopeService dataScopes,
    CancellationToken cancellationToken) =>
{
    var items = await repository.ListRdStylesAsync(direction, terminalOnly, cancellationToken);
    var header = context.Request.Headers["X-Function-Code"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(header)) return ApiResults.Ok(context, items);
    var functionCode = DocumentFunctionCode(header);
    await RequireFunctionAsync(context, users, functionCode, cancellationToken);
    return ApiResults.Ok(context, await dataScopes.FilterRdStylesAsync(
        RequestContext.User(context).Id, functionCode, items, cancellationToken));
});

app.MapGet("/api/v1/reference-data/warehouse-positions", async (
    HttpContext context,
    ReferenceDataRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.ListWarehousePositionsAsync(cancellationToken)));

app.MapGet("/api/v1/reference-data/departments", async (
    HttpContext context,
    ReferenceDataRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.ListDepartmentsAsync(cancellationToken)));

app.MapGet("/api/v1/reference-data/materials", async (
    HttpContext context,
    string? keyword,
    int? limit,
    U8GatewayClient gateway,
    UserRepository users,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users, "material-outbound", cancellationToken);
    var normalized = keyword?.Trim() ?? string.Empty;
    if (normalized.Length > 100)
        throw new ApiException(StatusCodes.Status400BadRequest,
            "MATERIAL_QUERY_INVALID", "物料查询条件无效。");
    return ApiResults.Ok(context,
        await gateway.SearchMaterialsAsync(normalized, Math.Clamp(limit ?? 50, 1, 100), cancellationToken));
});

app.MapGet("/api/v1/reference-data/purchase-types", async (
    HttpContext context,
    ReferenceDataRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.ListPurchaseTypesAsync(cancellationToken)));

app.MapGet("/api/v1/reference-data/sales-types", async (
    HttpContext context,
    ReferenceDataRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.ListSalesTypesAsync(cancellationToken)));

app.MapGet("/api/v1/reference-data/currencies", async (
    HttpContext context,
    ReferenceDataRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.ListCurrenciesAsync(cancellationToken)));

app.MapGet("/api/v1/admin/data-sync/status", async (
    HttpContext context,
    ReferenceDataRepository repository,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context, await repository.ListSyncStatusAsync(cancellationToken));
});

app.MapPost("/api/v1/admin/data-sync/rd-styles", async (
    HttpContext context,
    ReferenceDataSyncService service,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context,
        await service.SyncRdStylesAsync(RequestContext.User(context).Id, cancellationToken));
});

app.MapPost("/api/v1/admin/data-sync/warehouse-positions", async (
    HttpContext context,
    ReferenceDataSyncService service,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context,
        await service.SyncWarehousePositionsAsync(RequestContext.User(context).Id, cancellationToken));
});

app.MapPost("/api/v1/admin/data-sync/departments", async (
    HttpContext context,
    ReferenceDataSyncService service,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context,
        await service.SyncDepartmentsAsync(RequestContext.User(context).Id, cancellationToken));
});

app.MapPost("/api/v1/admin/data-sync/document-master-data", async (
    HttpContext context,
    ReferenceDataSyncService service,
    CancellationToken cancellationToken) =>
{
    RequireAdmin(context);
    return ApiResults.Ok(context,
        await service.SyncDocumentMasterDataAsync(RequestContext.User(context).Id, cancellationToken));
});

app.MapGet("/api/v1/source-documents", async (
    HttpContext context,
    UserRepository users,
    U8GatewayClient gateway,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users,
        DocumentFunctionCode(context.Request.Headers["X-Function-Code"]), cancellationToken);
    var proxy = await gateway.ForwardGetAsync(
        "/api/v1/source-documents" + context.Request.QueryString, cancellationToken);
    return Results.Content(proxy.Content, proxy.ContentType, Encoding.UTF8, proxy.StatusCode);
});

app.MapPost("/api/v1/source-documents/lines", async (
    HttpContext context,
    UserRepository users,
    U8GatewayClient gateway,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users,
        DocumentFunctionCode(context.Request.Headers["X-Function-Code"]), cancellationToken);
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    var proxy = await gateway.ForwardPostAsync(
        "/api/v1/source-documents/lines", await reader.ReadToEndAsync(cancellationToken), cancellationToken);
    return Results.Content(proxy.Content, proxy.ContentType, Encoding.UTF8, proxy.StatusCode);
});

app.MapPost("/api/v1/source-documents/material-requirements", async (
    HttpContext context,
    UserRepository users,
    U8GatewayClient gateway,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users,
        DocumentFunctionCode(context.Request.Headers["X-Function-Code"]), cancellationToken);
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    var proxy = await gateway.ForwardPostAsync(
        "/api/v1/source-documents/material-requirements", await reader.ReadToEndAsync(cancellationToken), cancellationToken);
    return Results.Content(proxy.Content, proxy.ContentType, Encoding.UTF8, proxy.StatusCode);
});

app.MapGet("/api/v1/approval-documents", async (
    HttpContext context,
    UserRepository users,
    U8GatewayClient gateway,
    CancellationToken cancellationToken) =>
{
    var functionCode = DocumentFunctionCode(context.Request.Query["documentType"]);
    await RequireFunctionAsync(context, users, $"approval-{functionCode}", cancellationToken);
    var documentType = ApprovalDocumentType(functionCode);
    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(context.Request.QueryString.Value ?? "");
    var values = query.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    values["documentType"] = documentType;
    var target = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
        "/api/v1/approval-documents", values!);
    var proxy = await gateway.ForwardGetAsync(target, cancellationToken);
    return Results.Content(proxy.Content, proxy.ContentType, Encoding.UTF8, proxy.StatusCode);
});

app.MapGet("/api/v1/approval-documents/{documentType}/{id:long}", async (
    HttpContext context,
    string documentType,
    long id,
    UserRepository users,
    U8GatewayClient gateway,
    CancellationToken cancellationToken) =>
{
    var functionCode = DocumentFunctionCode(documentType);
    await RequireFunctionAsync(context, users, $"approval-{functionCode}", cancellationToken);
    var proxy = await gateway.ForwardGetAsync(
        $"/api/v1/approval-documents/{ApprovalDocumentType(functionCode)}/{id}", cancellationToken);
    return Results.Content(proxy.Content, proxy.ContentType, Encoding.UTF8, proxy.StatusCode);
});

app.MapPost("/api/v1/approval-documents/{documentType}/{voucherCode}/{action}", async (
    HttpContext context,
    string documentType,
    string voucherCode,
    string action,
    ApprovalActionRequest request,
    UserRepository users,
    U8GatewayClient gateway,
    IOptionsMonitor<LabelSystemOptions> options,
    CancellationToken cancellationToken) =>
{
    var functionCode = DocumentFunctionCode(documentType);
    await RequireFunctionAsync(context, users, $"approval-{functionCode}", cancellationToken);
    var operation = action.Trim().ToLowerInvariant();
    if (operation is not ("audit" or "unaudit"))
        throw new ApiException(StatusCodes.Status400BadRequest,
            "APPROVAL_ACTION_INVALID", "审核操作无效。");
    if (!options.CurrentValue.U8WritesEnabled)
        throw new ApiException(StatusCodes.Status409Conflict,
            "U8_WRITE_DISABLED", "U8 写入未开放。");
    if (string.IsNullOrWhiteSpace(request.ClientRequestId) ||
        string.IsNullOrWhiteSpace(request.ExpectedVersion))
        throw new ApiException(StatusCodes.Status400BadRequest,
            "APPROVAL_REQUEST_INVALID", "请求参数不完整。");
    var current = RequestContext.User(context);
    var erp = await users.GetErpOperatorAsync(current.Id, cancellationToken);
    var payload = JsonSerializer.Serialize(new
    {
        request.ClientRequestId,
        OperatorId = current.Id,
        OperatorName = erp.Name,
        DeviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? "h5" : request.DeviceId,
        request.DeviceModel,
        request.ApkVersion,
        request.H5Version,
        request.BridgeVersion,
        request.Reason,
        request.ExpectedVersion
    });
    var gatewayAction = operation == "audit" ? "verify" : "unverify";
    var proxy = await gateway.ForwardPostAsync(
        $"/api/v1/documents/{ApprovalDocumentType(functionCode)}/{Uri.EscapeDataString(voucherCode)}/{gatewayAction}",
        payload, cancellationToken);
    return Results.Content(proxy.Content, proxy.ContentType, Encoding.UTF8, proxy.StatusCode);
});

app.MapPost("/api/v1/operations/material-outbound/submit", async (
    HttpContext context,
    MaterialOutboundPrepareRequest request,
    [Microsoft.AspNetCore.Mvc.FromQuery] bool autoApprove,
    MaterialOutboundSubmissionService service,
    UserRepository users,
    UserDataScopeService dataScopes,
    LabelSystemRepository settings,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users, "material-outbound", cancellationToken);
    var session = RequestContext.User(context);
    if (!string.Equals(session.Id, request.OperatorId, StringComparison.OrdinalIgnoreCase) &&
        !RequestContext.IsAdmin(context))
        throw new ApiException(StatusCodes.Status403Forbidden, "OPERATOR_MISMATCH", "操作人不匹配。");
    request = request with { OperatorId = session.Id, OperatorName = session.Name };
    await users.GetErpOperatorAsync(session.Id, cancellationToken);
    var materialRequiredFields = await settings.GetRequiredDocumentFieldsAsync(
        "material-outbound", cancellationToken);
    await dataScopes.ValidateRdStyleAsync(
        session.Id, "material-outbound", request.CategoryCode,
        !materialRequiredFields.TryGetValue("header.categoryCode", out var materialCategoryRequired) ||
        materialCategoryRequired,
        cancellationToken);
    var result = await service.SubmitAsync(
        request, session, RequestContext.Id(context), autoApprove, cancellationToken);
    return ApiResults.Ok(context, result);
});

app.MapPost("/api/v1/operations/documents/{documentType}/submit", async (
    HttpContext context,
    string documentType,
    DocumentPrepareRequest request,
    [Microsoft.AspNetCore.Mvc.FromQuery] bool autoApprove,
    MaterialOutboundSubmissionService service,
    UserRepository users,
    UserDataScopeService dataScopes,
    ReferenceDataRepository referenceData,
    LabelSystemRepository settings,
    CancellationToken cancellationToken) =>
{
    var functionCode = DocumentFunctionCode(documentType);
    await RequireFunctionAsync(context, users, functionCode, cancellationToken);
    var session = RequestContext.User(context);
    if (!string.Equals(session.Id, request.OperatorId, StringComparison.OrdinalIgnoreCase) &&
        !RequestContext.IsAdmin(context))
        throw new ApiException(StatusCodes.Status403Forbidden, "OPERATOR_MISMATCH", "操作人不匹配。");
    request = request with { OperatorId = session.Id, OperatorName = session.Name };
    await users.GetErpOperatorAsync(session.Id, cancellationToken);
    var requiredFields = await settings.GetRequiredDocumentFieldsAsync(documentType, cancellationToken);
    await ValidateDocumentReferenceDataAsync(
        documentType, request, session.Id, functionCode, requiredFields,
        dataScopes, referenceData, cancellationToken);
    var result = await service.SubmitAsync(
        documentType, request, session, RequestContext.Id(context), autoApprove, cancellationToken);
    return ApiResults.Ok(context, result);
});

app.MapPost("/api/v1/operations/material-outbound/prepare", (HttpContext _) =>
    RejectPrepareOnly());

app.MapPost("/api/v1/operations/documents/{documentType}/prepare", (HttpContext _) =>
    RejectPrepareOnly());

app.MapGet("/api/v1/operations/groups", async (
    HttpContext context,
    MaterialOutboundRepository repository,
    string? documentType,
    string? status,
    string? operatorId,
    DateOnly? startDate,
    DateOnly? endDate,
    int skip,
    int take,
    bool? allUsers,
    CancellationToken cancellationToken) =>
{
    var user = RequestContext.User(context);
    if (allUsers == true && !RequestContext.IsAdmin(context))
        throw new ApiException(StatusCodes.Status403Forbidden, "ADMIN_REQUIRED", "无权查看其他用户单据。");
    if (!RequestContext.IsAdmin(context) && !string.IsNullOrWhiteSpace(operatorId) &&
        !string.Equals(operatorId, user.Id, StringComparison.OrdinalIgnoreCase))
        throw new ApiException(StatusCodes.Status403Forbidden, "OPERATION_FORBIDDEN", "无权查看其他用户单据。");
    return ApiResults.Ok(context, await repository.ListGroupsAsync(
        user.Id, allUsers == true && RequestContext.IsAdmin(context), documentType, status,
        RequestContext.IsAdmin(context) ? operatorId : user.Id,
        startDate, endDate, skip, take == 0 ? 100 : take, cancellationToken));
});

app.MapGet("/api/v1/operations/groups/{groupId}", async (
    HttpContext context,
    string groupId,
    MaterialOutboundRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.GetGroupAsync(
        groupId, RequestContext.User(context).Id,
        RequestContext.IsAdmin(context), cancellationToken)));

app.MapPost("/api/v1/operations/groups/{groupId}/heartbeat", async (
    HttpContext context,
    string groupId,
    MaterialOutboundRepository repository,
    IOptionsMonitor<LabelSystemOptions> options,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.HeartbeatAsync(
        groupId, options.CurrentValue.LockMinutes, RequestContext.User(context).Id,
        false, cancellationToken)));

app.MapPost("/api/v1/operations/groups/{groupId}/validate", async (
    HttpContext context,
    string groupId,
    MaterialOutboundExecutionService service,
    UserRepository users,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users, "material-outbound", cancellationToken);
    return ApiResults.Ok(context, await service.ValidateAsync(
        groupId, RequestContext.User(context), cancellationToken));
});

app.MapPost("/api/v1/operations/groups/{groupId}/execute", async (
    HttpContext context,
    string groupId,
    ExecuteOperationGroupRequest request,
    MaterialOutboundExecutionService service,
    UserRepository users,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users, "material-outbound", cancellationToken);
    return ApiResults.Ok(context, await service.ExecuteAsync(
        groupId, request, RequestContext.User(context),
        RequestContext.Id(context), cancellationToken));
});

app.MapPost("/api/v1/operations/groups/{groupId}/cancel", async (
    HttpContext context,
    string groupId,
    MaterialOutboundRepository repository,
    CancellationToken cancellationToken) =>
    ApiResults.Ok(context, await repository.CancelAsync(
        groupId, RequestContext.Id(context), RequestContext.User(context).Id,
        false, cancellationToken)));

app.MapPost("/api/v1/operations/groups/{groupId}/issued-labels/{labelCode}/confirm", async (
    HttpContext context,
    string groupId,
    string labelCode,
    MaterialOutboundRepository repository,
    UserRepository users,
    CancellationToken cancellationToken) =>
{
    await RequireFunctionAsync(context, users, "material-outbound", cancellationToken);
    return ApiResults.Ok(context, await repository.ConfirmIssuedLabelAsync(
        groupId, labelCode, RequestContext.Id(context), RequestContext.User(context).Id,
        RequestContext.IsAdmin(context), cancellationToken));
});

app.Run();

static IResult RejectPrepareOnly() => throw new ApiException(
    StatusCodes.Status409Conflict, "DIRECT_SUBMIT_REQUIRED", "请刷新后直接上传 ERP。");

static async Task ValidateDocumentReferenceDataAsync(
    string documentType,
    DocumentPrepareRequest request,
    string userId,
    string functionCode,
    IReadOnlyDictionary<string, bool> requiredFields,
    UserDataScopeService dataScopes,
    ReferenceDataRepository referenceData,
    CancellationToken cancellationToken)
{
    var header = request.Header ?? throw new ApiException(
        StatusCodes.Status400BadRequest, "HEADER_REQUIRED", "单据表头不能为空。");
    var purchaseType = AdditionalText(header.AdditionalFields, "purchaseTypeCode");
    var salesType = AdditionalText(header.AdditionalFields, "salesTypeCode");
    if (documentType is "purchase-arrival" or "outsourcing-arrival" or "purchase-inbound")
    {
        purchaseType ??= documentType is "purchase-arrival" or "outsourcing-arrival"
            ? header.CategoryCode : null;
        if (string.IsNullOrWhiteSpace(purchaseType) ||
            !await referenceData.DocumentTypeExistsAsync("PURCHASE", purchaseType, cancellationToken))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PURCHASE_TYPE_INVALID", "请选择有效采购类型。");
    }
    if (documentType is "delivery-note" or "sales-outbound")
    {
        salesType ??= documentType == "delivery-note" ? header.CategoryCode : null;
        if (string.IsNullOrWhiteSpace(salesType) ||
            !await referenceData.DocumentTypeExistsAsync("SALES", salesType, cancellationToken))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "SALES_TYPE_INVALID", "请选择有效销售类型。");
    }

    if (documentType is "purchase-arrival" or "outsourcing-arrival" or "purchase-inbound" or "delivery-note")
    {
        var currency = AdditionalText(header.AdditionalFields, "currencyCode");
        if (string.IsNullOrWhiteSpace(currency) ||
            await referenceData.FindCurrencyAsync(currency, cancellationToken) is null)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "CURRENCY_INVALID", "请选择有效币种。");
    }

    if (documentType == "location-adjustment" ||
        documentType is "purchase-arrival" or "outsourcing-arrival" or "delivery-note") return;

    if (documentType is "transfer-order" or "inventory-count")
    {
        await dataScopes.ValidateRdStyleDirectionAsync(
            userId, functionCode, AdditionalText(header.AdditionalFields, "outCategoryCode"),
            "OUT", Required(requiredFields, "header.outCategoryCode"), cancellationToken);
        await dataScopes.ValidateRdStyleDirectionAsync(
            userId, functionCode, AdditionalText(header.AdditionalFields, "inCategoryCode"),
            "IN", Required(requiredFields, "header.inCategoryCode"), cancellationToken);
        return;
    }

    await dataScopes.ValidateRdStyleAsync(
        userId, functionCode, header.CategoryCode,
        Required(requiredFields, "header.categoryCode"), cancellationToken);

    static bool Required(IReadOnlyDictionary<string, bool> fields, string key) =>
        !fields.TryGetValue(key, out var required) || required;
}

static string? AdditionalText(JsonElement? fields, string name)
{
    if (fields is not { ValueKind: JsonValueKind.Object } value) return null;
    foreach (var property in value.EnumerateObject())
    {
        var logical = property.Name.StartsWith("header.", StringComparison.OrdinalIgnoreCase)
            ? property.Name[7..] : property.Name;
        if (!logical.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            property.Value.ValueKind == JsonValueKind.Null) continue;
        var text = property.Value.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
    return null;
}

static void RequireAdmin(HttpContext context)
{
    if (!RequestContext.HasManagementAccess(context))
        throw new ApiException(StatusCodes.Status403Forbidden, "ADMIN_REQUIRED", "无权操作。");
}

static void RequireRealAdmin(HttpContext context)
{
    if (!RequestContext.IsAdmin(context))
        throw new ApiException(StatusCodes.Status403Forbidden, "ADMIN_REQUIRED", "只有管理员可以操作。");
}

static void ValidateSetting(string scope, string key, UpdateSettingRequest request)
{
    if (!string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(key, "draft_retention_policy", StringComparison.OrdinalIgnoreCase)) return;
    if (request.Value.ValueKind != JsonValueKind.Object)
        throw new ApiException(StatusCodes.Status400BadRequest,
            "DRAFT_RETENTION_INVALID", "草稿保留设置无效。");
    foreach (var prefix in new[] { "active", "closed" })
    {
        if (!request.Value.TryGetProperty($"{prefix}Mode", out var modeElement) ||
            modeElement.ValueKind != JsonValueKind.String)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DRAFT_RETENTION_INVALID", "草稿保留设置无效。");
        var mode = modeElement.GetString()?.ToUpperInvariant();
        if (mode is not ("DAYS" or "PERMANENT"))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DRAFT_RETENTION_INVALID", "草稿保留设置无效。");
        if (mode == "DAYS" &&
            (!request.Value.TryGetProperty($"{prefix}Days", out var daysElement) ||
             !daysElement.TryGetInt32(out var days) || days is < 1 or > 36500))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DRAFT_RETENTION_INVALID", "保留天数应为 1 至 36500。");
    }
}

static UserAdminAuditContext CreateUserAuditContext(HttpContext context) => new(
    RequestContext.User(context).Id,
    RequestContext.Id(context),
    context.Connection.RemoteIpAddress?.ToString(),
    context.Request.Headers.UserAgent.FirstOrDefault());

static async Task RequireFunctionAsync(
    HttpContext context,
    UserRepository users,
    string functionCode,
    CancellationToken cancellationToken)
{
    if (!await users.HasFunctionAccessAsync(
            RequestContext.User(context).Id, functionCode, cancellationToken))
        throw new ApiException(StatusCodes.Status403Forbidden, "FUNCTION_FORBIDDEN", "无权操作。");
}

static bool FixedTimeEquals(string? left, string right)
{
    if (left is null) return false;
    var a = Encoding.UTF8.GetBytes(left);
    var b = Encoding.UTF8.GetBytes(right);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

static string ApprovalDocumentType(string? documentType) => documentType?.Trim().ToLowerInvariant() switch
{
    "purchase-arrival" => "purchase-arrival",
    "outsourcing-arrival" => "subcontract-arrival",
    "purchase-inbound" => "purchase-receipt",
    "delivery-note" => "sales-delivery",
    "sales-outbound" => "sales-outbound",
    "material-outbound" => "material-outbound",
    "transfer-order" => "transfer",
    "location-adjustment" => "location-adjustment",
    "inventory-count" => "inventory-count",
    _ => throw new ApiException(StatusCodes.Status400BadRequest,
        "DOCUMENT_TYPE_INVALID", "单据类型无效。")
};

static string DocumentFunctionCode(string? documentType) => documentType?.Trim().ToLowerInvariant() switch
{
    "purchase-arrival" => "purchase-arrival",
    "outsourcing-arrival" => "outsourcing-arrival",
    "purchase-inbound" => "purchase-inbound",
    "delivery-note" => "delivery-note",
    "sales-outbound" => "sales-outbound",
    "material-outbound" => "material-outbound",
    "transfer-order" => "transfer-order",
    "location-adjustment" => "location-adjustment",
    "inventory-count" => "inventory-count",
    _ => throw new ApiException(StatusCodes.Status400BadRequest,
        "DOCUMENT_TYPE_INVALID", "单据类型无效。")
};

static string DocumentOrQueryFunctionCode(string? functionCode) =>
    string.Equals(functionCode?.Trim(), "label-query", StringComparison.OrdinalIgnoreCase)
        ? "label-query"
        : DocumentFunctionCode(functionCode);

public partial class Program;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace LabelSystem.Api;

public static class BusinessClock
{
    public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
    public static DateTime NowDatabase => Now.DateTime;
}

public static class RequestContext
{
    private const string RequestIdKey = "LabelSystem.RequestId";
    private const string UserKey = "LabelSystem.User";
    private const string ManagementAccessKey = "LabelSystem.ManagementAccess";

    public static string Id(HttpContext context) =>
        context.Items.TryGetValue(RequestIdKey, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    public static SessionUser User(HttpContext context) =>
        context.Items.TryGetValue(UserKey, out var value) && value is SessionUser user
            ? user
            : throw new ApiException(StatusCodes.Status401Unauthorized, "AUTH_REQUIRED", "请先登录。");

    public static bool IsAdmin(HttpContext context) =>
        User(context).Roles.Contains("admin", StringComparer.OrdinalIgnoreCase);

    public static bool HasManagementAccess(HttpContext context) =>
        IsAdmin(context) ||
        context.Items.TryGetValue(ManagementAccessKey, out var value) && value is true;

    public static void SetId(HttpContext context, string requestId) => context.Items[RequestIdKey] = requestId;
    public static void SetUser(HttpContext context, SessionUser user) => context.Items[UserKey] = user;
    public static void SetManagementAccess(HttpContext context, bool allowed) =>
        context.Items[ManagementAccessKey] = allowed;
}

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException exception)
        {
            if (!context.Response.HasStarted)
            {
                await ApiResults.Error(context, exception.StatusCode, exception.Code, exception.Message)
                    .ExecuteAsync(context);
            }
        }
        catch (SqlException exception)
        {
            logger.LogError(exception, "Database request failed. RequestId={RequestId}", RequestContext.Id(context));
            if (!context.Response.HasStarted)
            {
                var constraintFailure = exception.Number is 547 or 2601 or 2627;
                await ApiResults.Error(
                    context,
                    constraintFailure ? StatusCodes.Status409Conflict : StatusCodes.Status503ServiceUnavailable,
                    constraintFailure ? "DATABASE_CONFLICT" : "DATABASE_UNAVAILABLE",
                    constraintFailure ? "数据校验失败。" : "数据库连接失败。"
                ).ExecuteAsync(context);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request failure. RequestId={RequestId}", RequestContext.Id(context));
            if (!context.Response.HasStarted)
            {
                await ApiResults.Error(context, StatusCodes.Status500InternalServerError,
                    "INTERNAL_ERROR", "处理失败。请稍后重试。").ExecuteAsync(context);
            }
        }
    }
}

public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Request-ID"].FirstOrDefault();
        var requestId = !string.IsNullOrWhiteSpace(supplied) && supplied.Length <= 100
            ? supplied.Trim()
            : Guid.NewGuid().ToString("N");
        RequestContext.SetId(context, requestId);
        context.Response.Headers["X-Request-ID"] = requestId;
        await next(context);
    }
}

public sealed class DevelopmentSessionStore
{
    private const int SessionHours = 12;
    private sealed record StoredSession(
        SessionUser User,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ManagementAccessExpiresAt);
    private readonly ConcurrentDictionary<string, StoredSession> _sessions = new(StringComparer.Ordinal);

    public LoginResult Create(SessionUser user)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = BusinessClock.Now.AddHours(SessionHours);
        _sessions[token] = new StoredSession(user, expiresAt, null);
        return new LoginResult(token, expiresAt, user, SessionHours * 60 * 60);
    }

    public SessionUser? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresAt > BusinessClock.Now) return session.User;
        _sessions.TryRemove(token, out _);
        return null;
    }

    public void Remove(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _sessions.TryRemove(token, out _);
    }

    public int RemoveUser(string userId)
    {
        var removed = 0;
        foreach (var session in _sessions.Where(item =>
                     string.Equals(item.Value.User.Id, userId, StringComparison.OrdinalIgnoreCase)))
            if (_sessions.TryRemove(session.Key, out _)) removed++;
        return removed;
    }

    public ManagementAccessResult ElevateManagementAccess(string? token, int minutes)
    {
        if (string.IsNullOrWhiteSpace(token) || !_sessions.TryGetValue(token, out var session) ||
            session.ExpiresAt <= BusinessClock.Now)
            throw new ApiException(StatusCodes.Status401Unauthorized, "AUTH_REQUIRED", "请先登录。");
        var seconds = Math.Clamp(minutes, 1, 60) * 60;
        var expiresAt = BusinessClock.Now.AddSeconds(seconds);
        _sessions[token] = session with { ManagementAccessExpiresAt = expiresAt };
        return new ManagementAccessResult(expiresAt, seconds);
    }

    public bool HasManagementAccess(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        _sessions.TryGetValue(token, out var session) &&
        session.ExpiresAt > BusinessClock.Now &&
        session.ManagementAccessExpiresAt > BusinessClock.Now;
}

public sealed class SessionAuthenticationMiddleware(RequestDelegate next)
{
    private static readonly string[] PublicPaths = ["/api/v1/health", "/api/v1/auth/login"];

    public async Task InvokeAsync(
        HttpContext context,
        DevelopmentSessionStore sessions,
        UserRepository users)
    {
        if (HttpMethods.IsOptions(context.Request.Method) ||
            PublicPaths.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var token = ReadToken(context);
        var storedUser = sessions.Resolve(token);
        var user = storedUser is null
            ? null
            : await users.FindSessionUserByIdAsync(storedUser.Id, context.RequestAborted);
        if (user is null)
        {
            sessions.Remove(token);
            await ApiResults.Error(context, StatusCodes.Status401Unauthorized,
                "AUTH_REQUIRED", "请先登录。").ExecuteAsync(context);
            return;
        }

        RequestContext.SetUser(context, user);
        RequestContext.SetManagementAccess(context, sessions.HasManagementAccess(token));
        await next(context);
    }

    public static string? ReadToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authorization) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization[7..].Trim();
        return context.Request.Cookies["LabelSystem.Session"];
    }
}

public sealed class SqlConnectionFactory(IConfiguration configuration)
{
    public SqlConnection Create()
    {
        var value = configuration.GetConnectionString("LabelSystem");
        if (string.IsNullOrWhiteSpace(value))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "DATABASE_NOT_CONFIGURED", "数据库尚未配置。");
        return new SqlConnection(value);
    }
}

public static class RowVersions
{
    public static string Encode(byte[] value) => Convert.ToHexString(value);

    public static byte[] Decode(string value)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length != 8) throw new FormatException();
            return bytes;
        }
        catch (FormatException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest,
                "ROW_VERSION_INVALID", "数据版本无效。");
        }
    }
}

public static class Hashing
{
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

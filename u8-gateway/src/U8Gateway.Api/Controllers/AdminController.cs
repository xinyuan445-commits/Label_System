using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Api.Models;
using U8Gateway.Contracts;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("admin")]
    public sealed class AdminController : ApiController
    {
        [HttpGet]
        [Route("config")]
        public HttpResponseMessage Page()
        {
            EnsureLocal();
            string path = HttpContext.Current.Server.MapPath("~/Admin/index.html");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    File.ReadAllText(path, Encoding.UTF8),
                    Encoding.UTF8,
                    "text/html")
            };
        }

        [HttpGet]
        [Route("api/config")]
        public IHttpActionResult GetConfig()
        {
            EnsureAuthorized();
            return Ok(ApiResponse<GatewaySettingsView>.Ok(
                AppRuntime.Settings.GetView(),
                GatewayRequestContext.Id));
        }

        [HttpGet]
        [Route("api/overview")]
        public IHttpActionResult GetOverview()
        {
            EnsureAuthorized();
            return Ok(ApiResponse<object>.Ok(
                DeploymentAdminService.Overview(), GatewayRequestContext.Id));
        }

        [HttpGet]
        [Route("api/business-config")]
        public IHttpActionResult GetBusinessConfig()
        {
            EnsureAuthorized();
            try
            {
                return Ok(ApiResponse<object>.Ok(
                    DeploymentAdminService.ReadBusinessView(), GatewayRequestContext.Id));
            }
            catch (Exception exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.ServiceUnavailable, exception.Message));
            }
        }

        [HttpPost]
        [Route("api/test-business-sql")]
        public IHttpActionResult TestBusinessSql(BusinessSettingsInput input)
        {
            EnsureAuthorized();
            try
            {
                return Ok(ApiResponse<object>.Ok(
                    DeploymentAdminService.TestBusinessSql(input),
                    GatewayRequestContext.Id,
                    "标签数据库连接成功"));
            }
            catch (Exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.BadRequest, "标签数据库连接失败。"));
            }
        }

        [HttpPost]
        [Route("api/save-business")]
        public IHttpActionResult SaveBusiness(BusinessSettingsInput input)
        {
            EnsureAuthorized(requireExistingAdmin: true);
            try
            {
                object result = DeploymentAdminService.SaveBusiness(input);
                return Ok(ApiResponse<object>.Ok(
                    result, GatewayRequestContext.Id, "Business API 配置已保存"));
            }
            catch (InvalidOperationException exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.BadRequest, exception.Message));
            }
        }

        [HttpGet]
        [Route("api/templates")]
        public IHttpActionResult GetTemplates()
        {
            EnsureAuthorized(requireExistingAdmin: true);
            try
            {
                return Ok(ApiResponse<object>.Ok(
                    DeploymentAdminService.ReadTemplates(), GatewayRequestContext.Id));
            }
            catch (Exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.ServiceUnavailable, "配置模板读取失败。"));
            }
        }

        [HttpPost]
        [Route("api/templates/save")]
        public IHttpActionResult SaveTemplate(DeploymentTemplateInput input)
        {
            EnsureAuthorized(requireExistingAdmin: true);
            try
            {
                return Ok(ApiResponse<object>.Ok(
                    DeploymentAdminService.SaveTemplate(input),
                    GatewayRequestContext.Id, "配置模板已保存"));
            }
            catch (InvalidOperationException exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.BadRequest, exception.Message));
            }
        }

        [HttpPost]
        [Route("api/templates/apply")]
        public IHttpActionResult ApplyTemplate(ApplyDeploymentTemplateInput input)
        {
            EnsureAuthorized(requireExistingAdmin: true);
            try
            {
                return Ok(ApiResponse<object>.Ok(
                    DeploymentAdminService.ApplyTemplate(input),
                    GatewayRequestContext.Id, "配置模板已应用"));
            }
            catch (InvalidOperationException exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.BadRequest, exception.Message));
            }
            catch (SqlException)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.BadRequest, "模板数据库连接测试失败。"));
            }
            catch (Exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.ServiceUnavailable, "配置模板应用失败。"));
            }
        }

        [HttpPost]
        [Route("api/traceback-check")]
        public IHttpActionResult TraceBackCheck(TraceBackCheckInput input)
        {
            EnsureAuthorized(requireExistingAdmin: true);
            try
            {
                return Ok(ApiResponse<object>.Ok(
                    DeploymentAdminService.TraceBackCheck(input?.Code),
                    GatewayRequestContext.Id,
                    "TraceBack 检查完成"));
            }
            catch (Exception)
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.ServiceUnavailable, "TraceBack 检查失败。"));
            }
        }

        [HttpPost]
        [Route("api/test-sql")]
        public IHttpActionResult TestSql(AdminSettingsInput input)
        {
            EnsureAuthorized();
            EnsureInput(input);
            GatewaySettings current = AppRuntime.Settings.Current;
            string password = string.IsNullOrWhiteSpace(input.SqlPassword)
                ? current.SqlPassword
                : input.SqlPassword;
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = input.SqlServer,
                InitialCatalog = input.SqlDatabase,
                UserID = input.SqlUser,
                Password = password,
                ConnectTimeout = 10,
                Encrypt = false,
                TrustServerCertificate = true
            };
            using (var connection = new SqlConnection(builder.ConnectionString))
            using (var command = new SqlCommand("select 1", connection))
            {
                connection.Open();
                command.ExecuteScalar();
            }
            return Ok(ApiResponse<object>.Ok(
                new { connected = true },
                GatewayRequestContext.Id,
                "SQL 连接成功"));
        }

        [HttpPost]
        [Route("api/test-u8")]
        public async Task<IHttpActionResult> TestU8(AdminSettingsInput input)
        {
            EnsureAuthorized();
            EnsureInput(input);
            GatewaySettings current = AppRuntime.Settings.Current;
            string password = string.IsNullOrWhiteSpace(input.U8Password)
                ? current.U8Password
                : input.U8Password;
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new HttpResponseException(Request.CreateErrorResponse(
                    HttpStatusCode.BadRequest,
                    "请填写 U8 密码。"));
            }

            U8ConnectionTestResult result = await AppRuntime.U8Queue.ExecuteAsync(
                () => U8ConnectionTester.Test(new U8ConnectionOptions
                {
                    Server = input.U8Server,
                    AccountId = input.U8AccountId,
                    Year = input.U8Year,
                    Subsystem = input.U8Subsystem,
                    User = input.U8User,
                    Password = password,
                    LoginDate = BusinessClock.Today
                }));
            return Ok(ApiResponse<U8ConnectionTestResult>.Ok(
                result,
                GatewayRequestContext.Id,
                "U8 连接成功"));
        }

        [HttpPost]
        [Route("api/save")]
        public IHttpActionResult Save(AdminSettingsInput input)
        {
            EnsureAuthorized();
            EnsureInput(input);
            var settings = new GatewaySettings
            {
                EnvironmentName = input.EnvironmentName?.Trim(),
                BusinessTimeZone = "China Standard Time",
                SqlServer = input.SqlServer?.Trim(),
                SqlDatabase = input.SqlDatabase?.Trim(),
                SqlUser = input.SqlUser?.Trim(),
                SqlPassword = input.SqlPassword,
                U8Server = input.U8Server?.Trim(),
                U8AccountId = input.U8AccountId?.Trim(),
                U8Year = input.U8Year?.Trim(),
                U8Subsystem = input.U8Subsystem?.Trim(),
                U8User = input.U8User?.Trim(),
                U8Password = input.U8Password,
                DefaultMaker = input.DefaultMaker?.Trim(),
                OperationArchiveRoot = input.OperationArchiveRoot?.Trim(),
                EnableTestWrites = input.EnableTestWrites,
                WriteAllowedDatabase = input.WriteAllowedDatabase?.Trim(),
                WriteAllowedAccountId = input.WriteAllowedAccountId?.Trim()
            };
            EnsureWriteSafety(settings, input.WriteConfirmation);
            string backup = DeploymentAdminService.BackupGatewaySettings();
            string generatedKey = AppRuntime.Settings.Save(
                settings,
                input.NewAdminPassword);
            DeploymentAdminService.Audit("GATEWAY_CONFIG_SAVED", new Newtonsoft.Json.Linq.JObject
            {
                ["environment"] = settings.EnvironmentName,
                ["database"] = settings.SqlDatabase,
                ["accountId"] = AccountCode(settings.U8AccountId),
                ["writesEnabled"] = settings.EnableTestWrites,
                ["backup"] = backup
            });
            return Ok(ApiResponse<object>.Ok(new
            {
                config = AppRuntime.Settings.GetView(),
                internalApiKey = generatedKey,
                backup
            }, GatewayRequestContext.Id, "配置已保存"));
        }

        [HttpPost]
        [Route("api/rotate-key")]
        public IHttpActionResult RotateKey()
        {
            EnsureAuthorized(requireExistingAdmin: true);
            string key = AppRuntime.Settings.RotateInternalApiKey();
            return Ok(ApiResponse<object>.Ok(
                new { internalApiKey = key },
                GatewayRequestContext.Id,
                "密钥已轮换"));
        }

        private void EnsureAuthorized(bool requireExistingAdmin = false)
        {
            EnsureLocal();
            if (!AppRuntime.Settings.IsConfigured && !requireExistingAdmin)
            {
                return;
            }
            string password = Request.Headers.TryGetValues(
                    "X-Admin-Password",
                    out var values)
                ? values.FirstOrDefault()
                : null;
            if (!AppRuntime.Settings.VerifyAdminPassword(password))
            {
                throw new HttpResponseException(Request.CreateErrorResponse(
                    HttpStatusCode.Unauthorized,
                    "管理密码无效。"));
            }
        }

        private static void EnsureLocal()
        {
            if (HttpContext.Current?.Request?.IsLocal != true)
            {
                throw new HttpResponseException(HttpStatusCode.Forbidden);
            }
        }

        private static void EnsureInput(AdminSettingsInput input)
        {
            if (input == null ||
                string.IsNullOrWhiteSpace(input.EnvironmentName) ||
                string.IsNullOrWhiteSpace(input.SqlServer) ||
                string.IsNullOrWhiteSpace(input.SqlDatabase) ||
                string.IsNullOrWhiteSpace(input.SqlUser) ||
                string.IsNullOrWhiteSpace(input.U8Server) ||
                string.IsNullOrWhiteSpace(input.U8AccountId) ||
                string.IsNullOrWhiteSpace(input.U8Year) ||
                string.IsNullOrWhiteSpace(input.U8Subsystem) ||
                string.IsNullOrWhiteSpace(input.U8User) ||
                string.IsNullOrWhiteSpace(input.OperationArchiveRoot))
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }
        }

        private static void EnsureWriteSafety(GatewaySettings settings, string confirmation)
        {
            if (!settings.EnableTestWrites)
            {
                return;
            }
            if (!string.Equals(settings.EnvironmentName, "test", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(settings.WriteAllowedDatabase) ||
                string.IsNullOrWhiteSpace(settings.WriteAllowedAccountId) ||
                !string.Equals(settings.SqlDatabase, settings.WriteAllowedDatabase, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    AccountCode(settings.U8AccountId),
                    AccountCode(settings.WriteAllowedAccountId),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.BadRequest,
                    "写入仅允许绑定测试环境、指定测试数据库和测试账套。"));
            }
            string expected = "ENABLE " + AccountCode(settings.WriteAllowedAccountId) + " " +
                settings.WriteAllowedDatabase;
            if (!string.Equals(confirmation?.Trim(), expected, StringComparison.Ordinal))
            {
                throw new HttpResponseException(RequestError(
                    HttpStatusCode.BadRequest, "写入确认文字不匹配。"));
            }
        }

        private static HttpResponseMessage RequestError(HttpStatusCode status, string message)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(message, Encoding.UTF8, "text/plain")
            };
        }

        private static string AccountCode(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            int separator = trimmed.LastIndexOf('@');
            return separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
        }
    }
}

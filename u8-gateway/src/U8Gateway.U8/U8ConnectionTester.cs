using System;
using System.Runtime.InteropServices;
using U8Gateway.Contracts;

namespace U8Gateway.U8
{
    public sealed class U8ConnectionOptions
    {
        public string Server { get; set; }
        public string AccountId { get; set; }
        public string Year { get; set; }
        public string Subsystem { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string Serial { get; set; }
        public DateTime LoginDate { get; set; }
    }

    public sealed class U8ConnectionTestResult
    {
        public bool Connected { get; set; }
        public string AccountId { get; set; }
        public string Year { get; set; }
        public string Subsystem { get; set; }
        public string LoginDate { get; set; }
    }

    public sealed class U8LoginException : InvalidOperationException
    {
        public U8LoginException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }

        public static U8LoginException FromMessage(string message)
        {
            string value = string.IsNullOrWhiteSpace(message)
                ? "U8 登录失败。"
                : message.Trim();
            string code = value.IndexOf("许可", StringComparison.OrdinalIgnoreCase) >= 0 &&
                          value.IndexOf("饱和", StringComparison.OrdinalIgnoreCase) >= 0
                ? "U8_LICENSE_UNAVAILABLE"
                : "U8_LOGIN_FAILED";
            return new U8LoginException(code, value);
        }
    }

    public static class U8ConnectionTester
    {
        public static U8ConnectionTestResult Test(U8ConnectionOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            string accountId = NormalizeAccountId(options.AccountId);
            var loginDate = options.LoginDate == default(DateTime)
                ? BusinessClock.Today
                : options.LoginDate.Date;
            U8Login.clsLogin login = null;
            try
            {
                login = new U8Login.clsLogin();
                bool connected = login.Login(
                    Required(options.Subsystem, "U8 subsystem"),
                    accountId,
                    Required(options.Year, "U8 year"),
                    Required(options.User, "U8 user"),
                    options.Password ?? string.Empty,
                    loginDate.ToString("yyyy-MM-dd"),
                    Required(options.Server, "U8 server"),
                    options.Serial ?? string.Empty);
                if (!connected)
                {
                    throw U8LoginException.FromMessage(
                        Convert.ToString(login.ShareString));
                }

                return new U8ConnectionTestResult
                {
                    Connected = true,
                    AccountId = accountId,
                    Year = options.Year.Trim(),
                    Subsystem = options.Subsystem.Trim(),
                    LoginDate = loginDate.ToString("yyyy-MM-dd")
                };
            }
            finally
            {
                if (login != null)
                {
                    try
                    {
                        login.ShutDown();
                    }
                    catch
                    {
                    }
                    if (Marshal.IsComObject(login))
                    {
                        Marshal.FinalReleaseComObject(login);
                    }
                }
            }
        }

        public static string NormalizeAccountId(string accountId)
        {
            string value = Required(accountId, "U8 account");
            if (value.IndexOf('@') >= 0)
            {
                return value;
            }
            if (value.Length == 3 &&
                char.IsDigit(value[0]) &&
                char.IsDigit(value[1]) &&
                char.IsDigit(value[2]))
            {
                return "(default)@" + value;
            }
            throw new ArgumentException(
                "U8 account must be a three-digit code or a complete login account id.",
                nameof(accountId));
        }

        private static string Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + " is required.");
            }
            return value.Trim();
        }
    }
}

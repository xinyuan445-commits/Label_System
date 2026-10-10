using System;
using U8Gateway.U8;
using U8Gateway.U8.Documents;

namespace U8Gateway.WriteSafetySmokeTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                MustBlock(Base());

                U8GatewayRuntimeOptions production = Base();
                production.EnableWrites = true;
                production.EnvironmentName = "production";
                MustBlock(production);

                U8GatewayRuntimeOptions wrongDatabase = Allowed();
                wrongDatabase.SqlDatabase = "UFDATA_008_2021";
                MustBlock(wrongDatabase);

                U8GatewayRuntimeOptions wrongAccount = Allowed();
                wrongAccount.U8.AccountId = "008";
                MustBlock(wrongAccount);

                LateBoundU8DocumentRuntime.ValidateWriteOptions(Allowed());
                U8GatewayRuntimeOptions completeAccount = Allowed();
                completeAccount.U8.AccountId = "(default)@888";
                LateBoundU8DocumentRuntime.ValidateWriteOptions(completeAccount);

                Console.WriteLine("write safety gate smoke passed");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static U8GatewayRuntimeOptions Allowed()
        {
            U8GatewayRuntimeOptions options = Base();
            options.EnableWrites = true;
            options.EnvironmentName = "test";
            return options;
        }

        private static U8GatewayRuntimeOptions Base()
        {
            return new U8GatewayRuntimeOptions
            {
                EnvironmentName = "test",
                EnableWrites = false,
                WriteAllowedDatabase = "UFDATA_888_2021",
                WriteAllowedAccountId = "888",
                SqlServer = "server",
                SqlDatabase = "UFDATA_888_2021",
                SqlUser = "user",
                SqlPassword = "password",
                U8 = new U8ConnectionOptions
                {
                    Server = "server",
                    AccountId = "888",
                    Year = "2026",
                    Subsystem = "ST",
                    User = "user",
                    Password = "password"
                }
            };
        }

        private static void MustBlock(U8GatewayRuntimeOptions options)
        {
            try
            {
                LateBoundU8DocumentRuntime.ValidateWriteOptions(options);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            throw new InvalidOperationException("不安全的写入配置未被拦截。");
        }
    }
}

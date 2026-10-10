using System.Threading;

namespace U8Gateway.Api.Infrastructure
{
    internal static class GatewayRequestContext
    {
        private static readonly AsyncLocal<string> CurrentValue =
            new AsyncLocal<string>();

        public static string Id
        {
            get => CurrentValue.Value;
            set => CurrentValue.Value = value;
        }
    }
}

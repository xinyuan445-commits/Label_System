using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace U8Gateway.Persistence.Archive
{
    internal static class SensitiveDataRedactor
    {
        private static readonly HashSet<string> SensitiveNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "password",
                "sqlPassword",
                "u8Password",
                "apiKey",
                "authorization",
                "accessToken",
                "refreshToken",
                "token",
                "cookie",
                "setCookie",
                "credential",
                "connectionString"
            };

        private static readonly Regex EmbeddedSecret = new Regex(
            @"(?i)\b(password|pwd|token|authorization|api[_-]?key|connectionstring)\b\s*[:=]\s*([^;\s,]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex BearerToken = new Regex(
            @"(?i)\bBearer\s+[A-Za-z0-9._~+\-/]+=*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static JToken Redact(JToken token)
        {
            if (token == null)
            {
                return JValue.CreateNull();
            }

            JToken copy = token.DeepClone();
            RedactInPlace(copy);
            return copy;
        }

        private static void RedactInPlace(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                foreach (JProperty property in value.Properties())
                {
                    if (SensitiveNames.Contains(property.Name) ||
                        property.Name.EndsWith("Password", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.EndsWith("Secret", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.EndsWith("Token", StringComparison.OrdinalIgnoreCase))
                    {
                        property.Value = "***REDACTED***";
                    }
                    else
                    {
                        RedactInPlace(property.Value);
                    }
                }
                return;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (JToken item in array)
                {
                    RedactInPlace(item);
                }
                return;
            }

            var scalar = token as JValue;
            if (scalar?.Type == JTokenType.String)
            {
                string text = (string)scalar.Value;
                text = EmbeddedSecret.Replace(text, "$1=***REDACTED***");
                scalar.Value = BearerToken.Replace(text, "Bearer ***REDACTED***");
            }
        }
    }
}

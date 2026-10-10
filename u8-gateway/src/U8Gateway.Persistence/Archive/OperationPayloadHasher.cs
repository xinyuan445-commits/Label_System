using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace U8Gateway.Persistence.Archive
{
    public static class OperationPayloadHasher
    {
        public static string Compute(object value)
        {
            JToken token = value == null
                ? JValue.CreateNull()
                : JToken.FromObject(value);
            string json = Sort(token).ToString(Formatting.None);
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(json));
                return string.Concat(bytes.Select(x => x.ToString("x2")));
            }
        }

        private static JToken Sort(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                var sorted = new JObject();
                foreach (JProperty property in value.Properties()
                    .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    sorted.Add(property.Name, Sort(property.Value));
                }
                return sorted;
            }

            var array = token as JArray;
            if (array != null)
            {
                return new JArray(array.Select(Sort));
            }

            return token.DeepClone();
        }
    }
}

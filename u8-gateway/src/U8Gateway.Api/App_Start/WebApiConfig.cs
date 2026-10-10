using System.Net.Http.Formatting;
using System.Web.Http;
using Newtonsoft.Json.Serialization;
using U8Gateway.Api.Infrastructure;

namespace U8Gateway.Api
{
    internal static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            config.MapHttpAttributeRoutes();
            config.MessageHandlers.Add(new RequestIdHandler());
            config.MessageHandlers.Add(new InternalApiKeyHandler());

            config.Formatters.Remove(config.Formatters.XmlFormatter);
            JsonMediaTypeFormatter json = config.Formatters.JsonFormatter;
            json.SerializerSettings.ContractResolver =
                new CamelCasePropertyNamesContractResolver();
            json.SerializerSettings.NullValueHandling =
                Newtonsoft.Json.NullValueHandling.Ignore;
        }
    }
}

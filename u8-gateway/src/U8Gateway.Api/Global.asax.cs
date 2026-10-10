using System.Web;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;

namespace U8Gateway.Api
{
    public class WebApiApplication : HttpApplication
    {
        protected void Application_Start()
        {
            AppRuntime.Initialize(Server.MapPath("~/App_Data"));
            GlobalConfiguration.Configure(WebApiConfig.Register);
        }

        protected void Application_End()
        {
            AppRuntime.Dispose();
        }
    }
}

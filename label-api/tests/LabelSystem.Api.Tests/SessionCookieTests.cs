using LabelSystem.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LabelSystem.Api.Tests;
public sealed class SessionCookieTests
{
    [Theory]
    [InlineData(null,"LabelSystem.Session","production")]
    [InlineData("LabelSystem.DevPrint.Session","LabelSystem.DevPrint.Session","development")]
    public void DedicatedDevelopmentCookieCannotConsumeProductionSession(string? configured,string expected,string token)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Session:CookieName",configured}}).Build();
        using var services=new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider();
        var context=new DefaultHttpContext{RequestServices=services};
        context.Request.Headers.Cookie="LabelSystem.Session=production; LabelSystem.DevPrint.Session=development";
        Assert.Equal(expected,SessionAuthenticationMiddleware.CookieName(context));
        Assert.Equal(token,SessionAuthenticationMiddleware.ReadToken(context));
    }
}

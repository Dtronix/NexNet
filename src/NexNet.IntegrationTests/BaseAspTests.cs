using System.Security.Claims;

namespace NexNet.IntegrationTests;

internal class BaseAspTests : BaseTests
{
    /// <summary>
    /// Setup services for simple authentication.
    /// </summary>
    protected static void AspCreateAuthServices(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication().AddBearerToken("BearerToken", o =>
        {
            o.Events.OnMessageReceived = context =>
            {
                if (context.Request.Headers.Authorization.FirstOrDefault() == "Bearer Token123")
                {
                    context.Principal = new ClaimsPrincipal();
                    context.Success();
                }
                else
                    context.Fail("Failed to authenticate token");
                    
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization();
    }
        
    /// <summary>
    /// Apply authentication to ASP application,
    /// </summary>
    protected static void AspAppAuthConfigure(WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }
}

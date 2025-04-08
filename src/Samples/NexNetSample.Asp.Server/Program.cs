using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.HttpOverrides;
using NexNet.Asp;
using NexNet.Logging;

namespace NexNetSample.Asp.Server;
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel((context, serverOptions) =>
        {
            serverOptions.Listen(IPAddress.Any, 9001);
        });
        builder.Services.AddTransient<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.All);
        builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();
        
        // Sample dummy authentication.
        builder.Services.AddAuthentication().AddBearerToken("BearerToken", options =>
        {
            options.Events = new BearerTokenEvents()
            {
                OnMessageReceived = context =>
                {
                    if (context.Request.Headers.Authorization.First() == "Bearer SecretTokenValue")
                    {
                        context.Principal = new ClaimsPrincipal();
                        context.Success();
                    }
                    else
                    {
                        context.Fail("Failed Connection");
                    }

                    return Task.CompletedTask;
                }
            };
        });
        
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();

        var app = builder.Build();
        
        app.UseForwardedHeaders();
        
        app.UseAuthentication();
        app.UseAuthorization();

        await app.UseHttpSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(c =>
        {
            c.NexusConfig.Path = "/nexus";
            c.NexusConfig.Logger!.Behaviors = NexusLogBehaviors.LocalInvocationsLogAsInfo;
            c.NexusConfig.AspEnableAuthentication = true;
            c.NexusConfig.AspAuthenticationScheme = "BearerToken";
        }).StartAsync(app.Lifetime.ApplicationStopped);


        await app.RunAsync();
    }
}

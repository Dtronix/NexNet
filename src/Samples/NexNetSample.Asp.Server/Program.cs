using System.Net;
using System.Security.Claims;
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
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        });
        
        builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();
        
        // Sample dummy authentication.
        builder.Services.AddAuthentication().AddBearerToken("BearerToken", options =>
        {
            options.Events = new BearerTokenEvents()
            {
                OnMessageReceived = context =>
                {
                    context.Principal = new ClaimsPrincipal();
                    context.Success();
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
            c.Path = "/nexus";
            c.Logger.Behaviors = NexusLogBehaviors.LocalInvocationsLogAsInfo;
            c.AspEnableAuthentication = true;
            c.AspAuthenticationScheme = "BearerToken";
        }).StartAsync(app.Lifetime.ApplicationStopped);


        await app.RunAsync();
    }
}

using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using NexNet;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Invocation;
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
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });
        
        builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();
        
        var app = builder.Build();

        //app.UseHttpsRedirection();
        app.UseForwardedHeaders();

        //await app.UseWebSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(c =>
        //{
        //    c.Path = "/websocket";
        //}).StartAsync(app.Lifetime.ApplicationStopped);

        await app.UseHttpSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(c =>
        {
            c.Path = "/httpsocket";
            c.Logger.Behaviors = NexusLogBehaviors.LocalInvocationsLogAsInfo;
        }).StartAsync(app.Lifetime.ApplicationStopped);


        await app.RunAsync();
    }
}

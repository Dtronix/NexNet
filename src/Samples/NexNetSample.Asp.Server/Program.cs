using System.Net;
using System.Security.Claims;
using MemoryPack;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.HttpOverrides;
using NexNet;
using NexNet.Asp;
using NexNet.Logging;

namespace NexNetSample.Asp.Server;
public class Program
{
    internal partial class Message2 {
        [MemoryPackOrder(0)] public int VersionDiff { get; set; }
        [MemoryPackOrder(1)] public int TotalValuesDiff { get; set; }
    }
    public static async Task Main(string[] args)
    {
        var s = MemoryPackSerializer.Serialize(new Message2());
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel((context, serverOptions) =>
        {
            serverOptions.Listen(IPAddress.Any, 9001);
        });
        builder.Services.AddTransient<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.All);
        
        // Sample dummy authentication.
        builder.Services.AddAuthentication().AddBearerToken("BearerToken", options =>
        {
            options.Events = new BearerTokenEvents()
            {
                OnMessageReceived = context =>
                {
                    if (context.Request.Headers.Authorization.First() == "Bearer SecretTokenValue")
                    {
                        // Dummy data
                        context.Principal = new ClaimsPrincipal([
                            new ClaimsIdentity([
                                new Claim(ClaimTypes.Name, "Connected User Name"),
                            ])
                        ]);
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
        
        builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();
        
        //builder.Services.AddHostedService<SimpleService>();

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

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class Message {
    [MemoryPackOrder(0)] public VersionMessage[] Messages { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class VersionMessage {
    [MemoryPackOrder(0)] public int Version { get; set; }
    [MemoryPackOrder(1)] public int TotalValues { get; set; }
    [MemoryPackOrder(2)] public ValuesMessage Values { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValuesMessage {
    [MemoryPackOrder(0)] public byte[] Values { get; set; }
    [MemoryPackOrder(1)] public ValueObjects ValueObjects { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValueObjects {
    [MemoryPackOrder(0)] public string[] Values { get; set; }
}

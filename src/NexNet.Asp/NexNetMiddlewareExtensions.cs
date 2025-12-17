using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Transports;

namespace NexNet.Asp;

/// <summary>
/// Extension methods to simplify adding NexNet servers to ASP.
/// </summary>
public static partial class NexNetMiddlewareExtensions
{
    /// <summary>
    /// Adds a Nexus Server and ClientProxy to the Service collection
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection AddNexusServer<TServerNexus, TClientProxy>(this IServiceCollection services)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        // Add the nexus to the services as a transient service to allow for instancing on each connection
        services.AddTransient<TServerNexus>();
        
        // Adds the server as a singleton to the service collection
        services.AddSingleton<NexusServer<TServerNexus, TClientProxy>>();
        
        // Adds the context provider
        services.AddSingleton<ServerNexusContextProvider<TServerNexus, TClientProxy>>(sp =>
            sp.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>().ContextProvider);

        return services;
    }
    
    /// <summary>
    /// Applies the current configured authentication scheme.
    /// </summary>
    /// <param name="context">Http context for the current connection.</param>
    /// <param name="config">Configuration containing the authentication scheme.</param>
    /// <returns>
    /// Returns true if the connection should continue based upon the configurations.
    /// The client may be authenticated or not if configured to disable authentication.
    /// </returns>
    public static async Task<bool> ApplyAuthentication(HttpContext context, AspServerConfig config)
    {
        if (!config.AspEnableAuthentication)
            return true;
        
        var authScheme = config.AspAuthenticationScheme?.Trim();
        AuthenticateResult authResult;
        if (string.IsNullOrWhiteSpace(authScheme))
        {
            authResult = await context.AuthenticateAsync().ConfigureAwait(false);
        }
        else
        {
            authResult = await context.AuthenticateAsync(authScheme).ConfigureAwait(false);
        }

        if (authResult.Succeeded)
            return true;

        // Log the failure.
        config.Logger?.LogInfo(authResult.Failure, "Authentication failed");
                    
        // Notify the connecting client with Unauthorized.
        context.Response.StatusCode = 401;
        return false;
    }

}

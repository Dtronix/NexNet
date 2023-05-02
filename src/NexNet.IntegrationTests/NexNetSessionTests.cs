using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Cache;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class NexNetSessionTests : BaseTests
{

    public async Task ConnectsToServer(Type type)
    {

        var (serverPipe, clientPipe) = Nerdbank.Streams.FullDuplexStream.CreatePipePair(PipeOptions.Default);

        var session = new NexNetSession<ClientHub, ServerHubProxyImpl>(
            new NexNetSessionConfigurations<ClientHub, ServerHubProxyImpl>()
            {
                Id = 0,
                Cache = new SessionCacheManager<ServerHubProxyImpl>(),
                Configs = CreateClientConfig(type, false),
                Hub = new ClientHub(),
                IsServer = false,
                SessionManager = new SessionManager(),
                Transport = clientPipe
            });

        _ = Task.Run(session.StartReadAsync);

        await session.SendHeader(MessageType.Ping);

        var result = await serverPipe.Input.ReadAsync();

    }


}

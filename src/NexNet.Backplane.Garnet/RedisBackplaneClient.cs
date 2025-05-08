using Garnet;
using Garnet.client;
using Garnet.server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace NexNet.Backplane.Garnet;

public class RedisBackplaneClient : IBackplaneClient
{

    public RedisBackplaneClient()
    {

    }
    public ValueTask ConnectAsync()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisconnectAsync()
    {
        throw new NotImplementedException();
    }
}

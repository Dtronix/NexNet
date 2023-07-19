using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNetDemo.Samples;

public class SampleBase
{
    protected UdsServerConfig ServerConfig { get; }
    protected UdsClientConfig ClientConfig { get; }

    public SampleBase(bool log)
    {
        var path = "test.sock";
        if (File.Exists(path))
            File.Delete(path);

        ServerConfig = new UdsServerConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            Logger = log ? new Logger("Server") : null
        };
        ClientConfig = new UdsClientConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            Logger = log ? new Logger("Client") : null
        };
    }
}

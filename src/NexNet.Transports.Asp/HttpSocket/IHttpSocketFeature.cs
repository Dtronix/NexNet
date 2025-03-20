using System.Threading.Tasks;
using NexNet.Transports.HttpSocket;

namespace NexNet.Transports.Asp.HttpSocket;

public interface IHttpSocketFeature
{
    bool IsHttpSocketRequest { get; }
    Task<HttpSocketDuplexPipe> AcceptAsync();
}

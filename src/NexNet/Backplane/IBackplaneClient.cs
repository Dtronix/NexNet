using System.Threading.Tasks;

namespace NexNet.Backplane;

public interface IBackplaneClient
{
    ValueTask ConnectAsync();
    ValueTask DisconnectAsync();
    
    
}

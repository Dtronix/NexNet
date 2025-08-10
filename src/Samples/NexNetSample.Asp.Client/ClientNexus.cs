using NexNet;
using NexNetSample.Asp.Shared;

namespace NexNetSample.Asp.Client;

[Nexus<IClientNexus, IServerNexusV2>(NexusType = NexusType.Client)]
public partial class ClientNexus
{
    
}

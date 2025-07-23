using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes;

namespace NexNetSample.Asp.Shared;

[NexusVersion(Version = "v3")]
public partial interface IServerNexusV3 : IServerNexusV2
{

}

[NexusVersion(Version = "v2")]
public partial interface IServerNexusV2 : IServerNexus
{
    [NexusCollection(NexusCollectionMode.BiDrirectional)]
    INexusList<int> IntegerList { get; }
}

[NexusVersion(Version = "v1")]
public interface IServerNexus
{
    ValueTask CalculateNumber(INexusDuplexPipe pipe);
}


public partial interface IClientNexus
{
}

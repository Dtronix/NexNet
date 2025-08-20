using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;

namespace NexNetDemo.Samples.Collections;

interface ICollectionSampleClientNexus
{
    
}

interface ICollectionSampleServerNexus
{
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<int> MainList { get; }
    
}


[Nexus<ICollectionSampleClientNexus, ICollectionSampleServerNexus>(NexusType = NexusType.Client)]
partial class CollectionSampleClientNexus
{

}

[Nexus<ICollectionSampleServerNexus, ICollectionSampleClientNexus>(NexusType = NexusType.Server)]
partial class CollectionSampleServerNexus
{

}

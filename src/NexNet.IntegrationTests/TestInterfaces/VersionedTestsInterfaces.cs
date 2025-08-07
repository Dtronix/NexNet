using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Messages;
using NexNet.Pipes;

// ReSharper disable InconsistentNaming
#pragma warning disable CS8618
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.TestInterfaces;

public partial interface ISimpleClientNexus
{
    ValueTask Run();
}


[NexusVersion(Version = "v1.0", HashLock = -808086739)]
public partial interface IVersionedServerNexus_V1
{
    ValueTask<bool> VerifyVersion(string version);
}

[NexusVersion(Version = "v1.1", HashLock = -358538641)]
public partial interface IVersionedServerNexus_V1_1 : IVersionedServerNexus_V1
{
    void RunTask();
    
    ValueTask<ReturnState> RunTaskWithResult();
    
    public enum ReturnState : ushort
    {
        Unset,
        Success,
        Failure,
    }

}

[Nexus<ISimpleClientNexus, IVersionedServerNexus_V1_1>(NexusType = NexusType.Client)]
public partial class VersionedClientNexus
{
    public ValueTask Run()
    {
        return default;
    }
}

[Nexus<IVersionedServerNexus_V1_1, ISimpleClientNexus>(NexusType = NexusType.Server)]
public partial class VersionedServerNexus
{
    public ValueTask<bool> VerifyVersion(string version)
    {
        return new ValueTask<bool>(false);
    }

    public void RunTask()
    {

    }

    public ValueTask<IVersionedServerNexus_V1_1.ReturnState> RunTaskWithResult()
    {
        return new ValueTask<IVersionedServerNexus_V1_1.ReturnState>(IVersionedServerNexus_V1_1.ReturnState.Success);
    }
}

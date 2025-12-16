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


[NexusVersion(Version = "v1.0", HashLock = 49342072)]
public partial interface IVersionedServerNexusV1
{
    [NexusMethod(1)]
    ValueTask<bool> VerifyVersionV1(string version);
}

[NexusVersion(Version = "v1.1", HashLock = 311996033)]
public partial interface IVersionedServerNexusV1_1 : IVersionedServerNexusV1
{
    [NexusMethod(2)]
    void RunTaskV1_1();
    
    [NexusMethod(3)]
    ValueTask<ReturnState> RunTaskWithResultV1_1();
    
    public enum ReturnState : ushort
    {
        Unset = 0,
        Success = 1,
        Failure = 2,
    }
}

[NexusVersion(Version = "v1.2", HashLock = -1007258537)]
public partial interface IVersionedServerNexusV2 : IVersionedServerNexusV1_1
{
    [NexusMethod(4)]
    ValueTask RunTaskNewV2();
}

public partial interface INonVersionedServerNexus
{
    ValueTask RunTaskNew();
    void RunTask();
    ValueTask<ReturnState> RunTaskWithResult();
    ValueTask<bool> VerifyVersion(string version);
    
    public enum ReturnState : ushort
    {
        Unset = 0,
        Success = 1,
        Failure = 2,
    }
}


[Nexus<ISimpleClientNexus, IVersionedServerNexusV1>(NexusType = NexusType.Client)]
public partial class VersionedClientNexusV1
{
    public ValueTask Run()
    {
        return default;
    }
}


[Nexus<ISimpleClientNexus, IVersionedServerNexusV1_1>(NexusType = NexusType.Client)]
public partial class VersionedClientNexusV1_1
{
    public ValueTask Run()
    {
        return default;
    }
}

[Nexus<ISimpleClientNexus, IVersionedServerNexusV2>(NexusType = NexusType.Client)]
public partial class VersionedClientNexusV2
{
    public ValueTask Run()
    {
        return default;
    }
}

[Nexus<ISimpleClientNexus, INonVersionedServerNexus>(NexusType = NexusType.Client)]
public partial class NonVersionedClientNexus
{
    public ValueTask Run()
    {
        return default;
    }
}




[Nexus<IVersionedServerNexusV2, ISimpleClientNexus>(NexusType = NexusType.Server)]
public partial class VersionedServerNexusV2
{
    public Func<string, ValueTask<bool>> VerifyVersionV1Action { get; set; }
    public Action RunTaskV1_1Action { get; set; }
    public Func<ValueTask<IVersionedServerNexusV1_1.ReturnState>> RunTaskWithResultV1_1Action { get; set; }
    public Func<ValueTask> RunTaskNewV2Action { get; set; }

    public ValueTask<bool> VerifyVersionV1(string version)
    {
        return VerifyVersionV1Action.Invoke(version);
    }

    public void RunTaskV1_1()
    {
        RunTaskV1_1Action.Invoke();
    }

    public ValueTask<IVersionedServerNexusV1_1.ReturnState> RunTaskWithResultV1_1()
    {
        return RunTaskWithResultV1_1Action.Invoke();
    }

    public ValueTask RunTaskNewV2()
    {
        return RunTaskNewV2Action.Invoke();
    }
}

[Nexus<IVersionedServerNexusV1_1, ISimpleClientNexus>(NexusType = NexusType.Server)]
public partial class VersionedServerNexusV1_1
{
    public Func<string, ValueTask<bool>> VerifyVersionV1Action { get; set; }
    public Action RunTaskV1_1Action { get; set; }
    public Func<ValueTask<IVersionedServerNexusV1_1.ReturnState>> RunTaskWithResultV1_1Action { get; set; }

    public ValueTask<bool> VerifyVersionV1(string version)
    {
        return VerifyVersionV1Action.Invoke(version);
    }

    public void RunTaskV1_1()
    {
        RunTaskV1_1Action.Invoke();
    }

    public ValueTask<IVersionedServerNexusV1_1.ReturnState> RunTaskWithResultV1_1()
    {
        return RunTaskWithResultV1_1Action.Invoke();
    }
}

[Nexus<IVersionedServerNexusV1, ISimpleClientNexus>(NexusType = NexusType.Server)]
public partial class VersionedServerNexusV1
{
    public Func<string, ValueTask<bool>> VerifyVersionV1Action { get; set; }

    public ValueTask<bool> VerifyVersionV1(string version)
    {
        return VerifyVersionV1Action.Invoke(version);
    }
}

[Nexus<INonVersionedServerNexus, ISimpleClientNexus>(NexusType = NexusType.Server)]
public partial class NonVersionedServerNexus
{
    public Func<string, ValueTask<bool>> VerifyVersionAction { get; set; }
    public Action RunTaskAction { get; set; }
    public Func<ValueTask<INonVersionedServerNexus.ReturnState>> RunTaskWithResultAction { get; set; }
    public Func<ValueTask> RunTaskNewAction { get; set; }

    public ValueTask<bool> VerifyVersion(string version)
    {
        return VerifyVersionAction.Invoke(version);
    }

    public void RunTask()
    {
        RunTaskAction.Invoke();
    }

    public ValueTask<INonVersionedServerNexus.ReturnState> RunTaskWithResult()
    {
        return RunTaskWithResultAction.Invoke();
    }

    public ValueTask RunTaskNew()
    {
        return RunTaskNewAction.Invoke();
    }
}

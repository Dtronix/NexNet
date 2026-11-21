using NexNet;

namespace NexNet.Generator.IntegrationSample;

public interface ISampleClientNexus
{
    ValueTask Notify(string message);
}

public interface ISampleServerNexus
{
    ValueTask<int> Add(int a, int b);

    ValueTask<SampleItem> Echo(SampleItem item);
}

[NexNet.Serialization.NexusObject]
public partial class SampleItem
{
    [NexNet.Serialization.NexusKey(0)]
    public string Name { get; set; } = "";

    [NexNet.Serialization.NexusKey(1)]
    public long Count { get; set; }
}

[Nexus<ISampleServerNexus, ISampleClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.Java | NexusTargets.JavaScript)]
public partial class SampleServerNexus
{
    public ValueTask<int> Add(int a, int b) => ValueTask.FromResult(a + b);

    public ValueTask<SampleItem> Echo(SampleItem item) => ValueTask.FromResult(item);
}

[Nexus<ISampleClientNexus, ISampleServerNexus>(NexusType = NexusType.Client)]
public partial class SampleClientNexus
{
    public ValueTask Notify(string message) => ValueTask.CompletedTask;
}

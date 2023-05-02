using System;

namespace NexNet;

public enum NexNetHubType : int
{
    Client = 0,
    Server = 1
}

[AttributeUsage(AttributeTargets.Class)]
public class NexNetHubAttribute<THub, TProxy> : Attribute
{
    public NexNetHubType HubType { get; init; }

    public NexNetHubAttribute(NexNetHubType hubType)
    {
        HubType = hubType;
    }

    public NexNetHubAttribute()
    {
    }

}

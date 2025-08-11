using NexNet;

namespace NexNetDemo.Samples.InvocationSample;

interface IInvocationSampleClientNexus
{

}

interface IInvocationSampleServerNexus
{
    void UpdateInfo(int userId, UserStatus status, string? customStatus);
    Task UpdateInfoAndWait(int userId, UserStatus status, string? customStatus);

    Task<UserStatus> GetStatus(int userId);
}

public enum UserStatus
{
    Offline,
    Online,
    Away,
    Busy,
    DoNotDisturb,
    Invisible,
    Unknown
}

[Nexus<IInvocationSampleClientNexus, IInvocationSampleServerNexus>(NexusType = NexusType.Client)]
partial class InvocationSampleClientNexus
{

}

[Nexus<IInvocationSampleServerNexus, IInvocationSampleClientNexus>(NexusType = NexusType.Server)]
partial class InvocationSampleServerNexus
{
    private long _counter = 0;
    public void UpdateInfo(int userId, UserStatus status, string? customStatus)
    {
        // Do something with the data.
    }

    public Task UpdateInfoAndWait(int userId, UserStatus status, string? customStatus)
    {
        // Do something with the data.
        if(_counter++ % 10000 == 0)
            Console.WriteLine($"Counter: {_counter}");

        return default;
    }

    public Task<UserStatus> GetStatus(int userId)
    {
        return Task.FromResult(UserStatus.Online);
    }
}

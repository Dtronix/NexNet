namespace NexNet;

public interface IIdentity
{
    string DisplayName { get; }
}

public class DefaultIdentity : IIdentity
{
    public string DisplayName { get; set; }
}

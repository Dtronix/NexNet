using NexNet.Messages;

#pragma warning disable VSTHRD200

namespace NexNet.Inspector.Tests.Tap;

public partial interface ITapServerNexus
{
    ValueTask<byte[]> Echo(byte[] data);
}

public partial interface ITapClientNexus
{
}

/// <summary>The inspected server of the tap tests; accepts any non-empty token when authentication is on.</summary>
[Nexus<ITapServerNexus, ITapClientNexus>(NexusType = NexusType.Server)]
public partial class TapServerNexus
{
    public ValueTask<byte[]> Echo(byte[] data) => new(data);

    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken) =>
        new(authenticationToken is { Length: > 0 } ? new DefaultIdentity() : null);
}

[Nexus<ITapClientNexus, ITapServerNexus>(NexusType = NexusType.Client)]
public partial class TapClientNexus
{
}

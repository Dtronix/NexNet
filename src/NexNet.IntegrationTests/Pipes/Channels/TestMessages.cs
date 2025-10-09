using MemoryPack;
using NexNet.Pipes.Channels;

namespace NexNet.IntegrationTests.Pipes.Channels;

static class RandomStringHelper
{
    public static string Generate(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";
        return new string(Enumerable.Range(0, length)
            .Select(_ => chars[Random.Shared.Next(chars.Length)])
            .ToArray());
    }
}

abstract class NetworkMessageUnion : INexusPooledMessageUnion<NetworkMessageUnion>
{
    public static void RegisterMessages(INexusPooledMessageUnionBuilder<NetworkMessageUnion> registerer)
    {
        registerer.Add<LoginMessage>();
        registerer.Add<ChatMessage>();
        registerer.Add<DisconnectMessage>();
    }
}

[MemoryPackable]
partial class LoginMessage : NetworkMessageUnion, INexusPooledMessage<LoginMessage>, IEquatable<LoginMessage>
{
    public static byte UnionId => 0;

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    
    public static LoginMessage Rent() => INexusPooledMessage<LoginMessage>.Rent();

    public LoginMessage Randomize()
    {
        Username = RandomStringHelper.Generate(Random.Shared.Next(5, 20));
        Password = RandomStringHelper.Generate(Random.Shared.Next(8, 30));
        return this;
    }

    public bool Equals(LoginMessage? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Username == other.Username && Password == other.Password;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((LoginMessage)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Username, Password);
    }
}

[MemoryPackable]
partial class ChatMessage : NetworkMessageUnion, INexusPooledMessage<ChatMessage>, IEquatable<ChatMessage>
{
    public static byte UnionId => 1;

    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long Timestamp { get; set; }

    public ChatMessage Randomize()
    {
        Sender = RandomStringHelper.Generate(Random.Shared.Next(5, 15));
        Content = RandomStringHelper.Generate(Random.Shared.Next(20, 100));
        Timestamp = Random.Shared.NextInt64();
        return this;
    }
    
    public static ChatMessage Rent() => INexusPooledMessage<ChatMessage>.Rent();

    public bool Equals(ChatMessage? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Sender == other.Sender && Content == other.Content && Timestamp == other.Timestamp;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((ChatMessage)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Sender, Content, Timestamp);
    }
}

[MemoryPackable]
partial class DisconnectMessage : NetworkMessageUnion, INexusPooledMessage<DisconnectMessage>, IEquatable<DisconnectMessage>
{
    public static byte UnionId => 2;
    public string Reason { get; set; } = string.Empty;
    public int ErrorCode { get; set; }

    public DisconnectMessage Randomize()
    {
        ErrorCode = Random.Shared.Next();
        Reason = RandomStringHelper.Generate(Random.Shared.Next(10, 50));
        return this;
    }
    
    public static DisconnectMessage Rent() => INexusPooledMessage<DisconnectMessage>.Rent();

    public bool Equals(DisconnectMessage? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Reason == other.Reason && ErrorCode == other.ErrorCode;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((DisconnectMessage)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Reason, ErrorCode);
    }
}

[MemoryPackable]
partial class StandAloneMessage : NexusPooledMessageBase<StandAloneMessage>, IEquatable<StandAloneMessage>
{
    public string Reason { get; set; } = string.Empty;
    public int ErrorCode { get; set; }

    public StandAloneMessage Randomize()
    {
        ErrorCode = Random.Shared.Next();
        Reason = RandomStringHelper.Generate(Random.Shared.Next(10, 50));
        return this;
    }

    public bool Equals(StandAloneMessage? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Reason == other.Reason && ErrorCode == other.ErrorCode;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((StandAloneMessage)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Reason, ErrorCode);
    }
}

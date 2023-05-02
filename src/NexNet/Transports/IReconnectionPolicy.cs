using System;

namespace NexNet.Transports;

public interface IReconnectionPolicy
{
    TimeSpan? ReconnectDelay(int retryCount);
}

using NexNet.Pipes;

namespace NexNetDemo.Websocket.Shared;

public partial interface IClientNexus
{
    void ClientVoid();
    void ClientVoidWithParam(int id);
    ValueTask ClientTask();
    ValueTask ClientTaskWithParam(int data);
    ValueTask<int> ClientTaskValue();
    ValueTask<int> ClientTaskValueWithParam(int data);
    ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken);
    ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask<int> ClientTaskValueWithCancellation(CancellationToken cancellationToken);
    ValueTask<int> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe);
}



public partial interface IServerNexus
{
    void ServerVoid();
    void ServerVoidWithParam(int id);
    ValueTask ServerTask();
    ValueTask ServerTaskWithParam(int data);
    ValueTask<int> ServerTaskValue();
    ValueTask<int> ServerTaskValueWithParam(int data);
    ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken);
    ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithCancellation(CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask ServerTaskValueWithDuplexPipe(INexusDuplexPipe pipe);

    ValueTask ServerData(byte[] data);
}

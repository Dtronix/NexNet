namespace NexNet.IntegrationTests.Generator;

class MyClass
{
    //[Test]
    public void Test()
    {
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace NexNetDemo;

interface IClientHub
{
    void Update();
    //void UpdateData(string data);
    ValueTask<int> GetTask();
    ValueTask<int> GetTaskAgain();
}

interface IServerHub
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
}

[NexNetHub<IServerHub, IClientHub>(NexNetHubType.Server)]
partial class ServerHub : IServerHub
{
}

[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub : IClientHub
{
}
"""); 

        /*
        var diagnostic = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
namespace TestNamespace;
interface IClientHub
{ 
void ClientUpdate();
void ClientUpdate2(int pa); 
ValueTask ClientUpdate3(); 
ValueTask ClientUpdate4(int myValue); 
ValueTask<int> ClientUpdate5(int myValue, string val2);
ValueTask ClientUpdateCancel(int myValue, CancellationToken cancellationToken); 
}
interface IServerHub { void ServerUpdate(); }

[NexNetHub<IClientHub, IServerHub>(NexNetHubType.Client)]
partial class ClientHub { }
""");*/
    }
}


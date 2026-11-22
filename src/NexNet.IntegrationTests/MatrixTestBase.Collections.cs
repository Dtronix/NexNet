using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.InteropServer;
using NexNet.Pipes.Broadcast;

namespace NexNet.IntegrationTests;

/// <summary>Synchronized collection helpers of <see cref="MatrixTestBase"/> (P01f).</summary>
/// <remarks>
/// The client's list is read through <see cref="IRemoteList{T}"/> snapshots and its change events through a watch
/// (<see cref="WatchAsync"/>), its mode, state and disabled signal through <see cref="IRemoteList{T}.GetStateAsync"/>.
/// Tests of C#-only list members (<c>CopyTo</c>) drive the C# client directly (<see cref="InProc"/>) and run on C# rows
/// only (<c>Languages = [ClientLanguages.CSharp]</c>).
/// </remarks>
internal abstract partial class MatrixTestBase
{
    /// <summary>Skip reason of tests that install a change handler on the client's list that throws.</summary>
    protected const string ThrowingListHandler = "the harness cannot install a throwing change handler on the client's list";

    /// <summary>The name of the bidirectional collection most tests use.</summary>
    protected const string IntListBi = nameof(IServerNexus.IntListBi);

    /// <summary>
    /// Starts a dedicated interop server of <see cref="ServerNexus"/> (<see cref="StartServerAsync"/>), then creates and
    /// connects the client of <paramref name="target"/>.
    /// </summary>
    protected async Task<CollectionSession> ConnectAsync(ClientTarget target)
    {
        var server = await StartServerAsync();
        IClientUnderTest? client = null;
        try
        {
            client = await ConnectAsync(target, server);
            var nexus = await ServerNexusAsync();
            return new CollectionSession(server, client, nexus);
        }
        catch
        {
            if (client != null)
                await client.DisposeAsync();
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>The items the client's list currently holds.</summary>
    protected static async Task<int[]> ItemsAsync(IRemoteList<int> list)
        => (await list.SnapshotAsync().AsTask().Timeout(T(1))).ToArray();

    /// <summary>
    /// Starts recording the change events of the client's list. Dispose the watch before the test's leak audit
    /// (<see cref="EndAsync(IClientUnderTest)"/>): a watch still attached counts as a leaked subscription.
    /// </summary>
    protected static async Task<IRemoteListWatch<int>> WatchAsync(IRemoteList<int> list)
        => await list.WatchAsync().AsTask().Timeout(T(1));

    /// <summary>Waits until the client's list raised at least <paramref name="count"/> change events of <paramref name="action"/>.</summary>
    protected static async Task WaitForChangesAsync(IRemoteListWatch<int> watch, NexusCollectionChangedAction action, int count = 1)
        => await watch.WaitForAsync(action, count, TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)));

    /// <summary>Waits until the client's list holds exactly <paramref name="expected"/>, in order, and returns it.</summary>
    protected static async Task<int[]> WaitForItemsAsync(IRemoteList<int> list, params int[] expected)
        => (await list.WaitForAsync(items => items.SequenceEqual(expected), TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)))).ToArray();

    /// <summary>Waits until the client's list holds <paramref name="count"/> items and returns them.</summary>
    protected static async Task<int[]> WaitForCountAsync(IRemoteList<int> list, int count)
        => (await list.WaitForAsync(items => items.Count == count, TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)))).ToArray();

    /// <summary>
    /// Disables the server collection's acknowledgments, so a client operation stays pending after the server applied it.
    /// </summary>
    protected static void DoNotSendAck(INexusList<int> serverList)
        => ((INexusBroadcastServerTestModifier)serverList).DoNotSendAck = true;

    /// <summary>A client and its dedicated server.</summary>
    protected sealed class CollectionSession(
        InteropServer<ServerNexus, ServerNexus.ClientProxy> server,
        IClientUnderTest client,
        ServerNexus nexus) : IAsyncDisposable
    {
        /// <summary>The interop server.</summary>
        public InteropServer<ServerNexus, ServerNexus.ClientProxy> Server { get; } = server;

        /// <summary>The client under test.</summary>
        public IClientUnderTest Client { get; } = client;

        /// <summary>The server nexus of the client's session.</summary>
        public ServerNexus Nexus { get; } = nexus;

        /// <summary>The client's view of <c>IntListBi</c>.</summary>
        public IRemoteList<int> List => Client.List<int>(IntListBi);

        /// <summary>The server's <c>IntListBi</c>.</summary>
        public INexusList<int> ServerList => Nexus.IntListBi;

        /// <summary>Disconnects the client when it is still connected, then asserts its leak audit is clean.</summary>
        public Task EndAsync() => MatrixTestBase.EndAsync(Client);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
            }
            finally
            {
                await Server.DisposeAsync();
            }
        }
    }
}

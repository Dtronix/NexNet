using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.InteropServer;
using NexNet.Serialization;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A client that enables a list after the server already holds items gets them in the reset, whatever the item type.
/// Only numeric lists have a registered <c>Memory&lt;T&gt;</c> formatter, so a list of a <c>[NexusObject]</c> type or of
/// strings used to fail its snapshot and close the pipe (I35).
/// </summary>
internal class NexusCollectionListResetItemTypeTests : NexusCollectionBaseTests
{
    private static readonly ListResetItem[] Items = Enumerable.Range(0, 45)
        .Select(i => new ListResetItem { Id = i, Name = "item " + i })
        .ToArray();

    // More than one 40-value ResetValues chunk, a null and a non-ASCII value.
    private static readonly string?[] Strings = Enumerable.Range(0, 43).Select(i => (string?)("s" + i))
        .Append(null)
        .Append("été")
        .ToArray();

    [Test]
    public Task ServerToClient_NexusObject_SecondClientReceivesTheList()
        => SecondClientReceivesTheList(n => n.ItemsSC, p => p.ItemsSC, Items, ItemComparer.Instance);

    [Test]
    public Task BiDirectional_NexusObject_SecondClientReceivesTheList()
        => SecondClientReceivesTheList(n => n.ItemsBi, p => p.ItemsBi, Items, ItemComparer.Instance);

    [Test]
    public Task ServerToClient_String_SecondClientReceivesTheList()
        => SecondClientReceivesTheList(n => n.StringsSC, p => p.StringsSC, Strings, EqualityComparer<string?>.Default);

    [Test]
    public Task BiDirectional_String_SecondClientReceivesTheList()
        => SecondClientReceivesTheList(n => n.StringsBi, p => p.StringsBi, Strings, EqualityComparer<string?>.Default);

    [Test]
    public async Task Relay_NexusObject_ClientReceivesTheList()
    {
        // The parent's reset reaches the relay (server snapshot, client decode), then the relay's own snapshot reaches
        // its client.
        await using var parent = await InteropServerHost.StartAsync<ListResetServerNexus, ListResetServerNexus.ClientProxy>(
            () => new ListResetServerNexus()).Timeout(T(TestTimeouts.Connect));
        var source = ServerList(parent, n => n.ItemsBi);
        foreach (var item in Items)
            await source.AddAsync(item).Timeout(T(1));

        await using var pool = new NexusClientPool<ListResetClientNexus, ListResetClientNexus.ServerProxy>(
            new NexusClientPoolConfig(parent.CreateClientConfig(InteropEndpointKind.Tcp)));
        var connector = pool.GetCollectionConnector(p => p.ItemsBi);
        await using var relay = await InteropServerHost.StartAsync<ListResetServerNexus, ListResetServerNexus.ClientProxy>(
            () => new ListResetServerNexus(),
            configureCollections: n => n.ItemsRelay.ConfigureRelay(connector)).Timeout(T(TestTimeouts.Connect));

        var relayList = ServerList(relay, n => n.ItemsRelay);
        await relayList.ReadyTask.Timeout(T(TestTimeouts.Connect));
        await Assert.That(relayList.ToArray()).IsEquivalentTo(Items, ItemComparer.Instance, CollectionOrdering.Matching);

        var (client, _) = CreateClient<ListResetClientNexus, ListResetClientNexus.ServerProxy>(
            relay.CreateClientConfig(InteropEndpointKind.Tcp));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var list = client.Proxy.ItemsRelay;
        await list.ConnectAsync().AsTask().Timeout(T(TestTimeouts.CollectionEnable));
        await Assert.That(list.ToArray()).IsEquivalentTo(Items, ItemComparer.Instance, CollectionOrdering.Matching);
        await Assert.That(list.DisabledTask.IsCompleted).IsFalse();
    }

    private async Task SecondClientReceivesTheList<TItem>(
        Func<ListResetServerNexus, INexusList<TItem>> serverList,
        Func<ListResetClientNexus.ServerProxy, INexusList<TItem>> clientList,
        TItem[] items,
        IEqualityComparer<TItem> comparer)
    {
        await using var server = await InteropServerHost.StartAsync<ListResetServerNexus, ListResetServerNexus.ClientProxy>(
            () => new ListResetServerNexus()).Timeout(T(TestTimeouts.Connect));

        // The first client's reset is empty; it then follows the items as changes.
        var (first, _) = CreateClient<ListResetClientNexus, ListResetClientNexus.ServerProxy>(
            server.CreateClientConfig(InteropEndpointKind.Tcp));
        await first.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var firstList = clientList(first.Proxy);
        await firstList.ConnectAsync().AsTask().Timeout(T(TestTimeouts.CollectionEnable));

        var source = ServerList(server, serverList);
        foreach (var item in items)
            await source.AddAsync(item).Timeout(T(1));
        await WaitForCount(firstList, items.Length);
        await Assert.That(firstList.ToArray()).IsEquivalentTo(items, comparer, CollectionOrdering.Matching);

        // The second client's reset carries every item.
        var (second, _) = CreateClient<ListResetClientNexus, ListResetClientNexus.ServerProxy>(
            server.CreateClientConfig(InteropEndpointKind.Tcp));
        await second.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var secondList = clientList(second.Proxy);
        await secondList.ConnectAsync().AsTask().Timeout(T(TestTimeouts.CollectionEnable));
        await Assert.That(secondList.ToArray()).IsEquivalentTo(items, comparer, CollectionOrdering.Matching);
        await Assert.That(secondList.DisabledTask.IsCompleted).IsFalse();
        await Assert.That(firstList.DisabledTask.IsCompleted).IsFalse();
    }

    private static INexusList<TItem> ServerList<TItem>(
        InteropServer<ListResetServerNexus, ListResetServerNexus.ClientProxy> server,
        Func<ListResetServerNexus, INexusList<TItem>> select)
    {
        using var owner = server.Server.ContextProvider.Rent();
        return select(owner.Collections);
    }

    private static async Task WaitForCount<TItem>(INexusList<TItem> list, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(T(TestTimeouts.CollectionEvent));
        while (list.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private sealed class ItemComparer : IEqualityComparer<ListResetItem>
    {
        public static readonly ItemComparer Instance = new();

        public bool Equals(ListResetItem? x, ListResetItem? y)
            => x is null ? y is null : y is not null && x.Id == y.Id && x.Name == y.Name;

        public int GetHashCode(ListResetItem obj) => obj.Id;
    }
}

[NexusObject]
public partial class ListResetItem
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public string Name { get; set; } = "";
}

public partial interface IListResetClientNexus
{
    void Ping();
}

public partial interface IListResetServerNexus
{
    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<ListResetItem> ItemsSC { get; }

    [NexusCollection(NexusCollectionMode.BiDirectional)]
    INexusList<ListResetItem> ItemsBi { get; }

    [NexusCollection(NexusCollectionMode.Relay)]
    INexusList<ListResetItem> ItemsRelay { get; }

    [NexusCollection(NexusCollectionMode.ServerToClient)]
    INexusList<string?> StringsSC { get; }

    [NexusCollection(NexusCollectionMode.BiDirectional)]
    INexusList<string?> StringsBi { get; }
}

[Nexus<IListResetClientNexus, IListResetServerNexus>(NexusType = NexusType.Client)]
public partial class ListResetClientNexus
{
    public void Ping()
    {
    }
}

[Nexus<IListResetServerNexus, IListResetClientNexus>(NexusType = NexusType.Server)]
public partial class ListResetServerNexus
{
}

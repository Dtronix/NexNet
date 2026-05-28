using System;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamTransportPoolTests
{
    [Test]
    public void DefaultMaxSize_Is16()
    {
        Assert.That(NexusStreamTransportPool.DefaultMaxSize, Is.EqualTo(16));
    }

    [Test]
    public void Constructor_ThrowsOnNullFactory()
    {
        Assert.Throws<ArgumentNullException>(() => new NexusStreamTransportPool(null!));
    }

    [Test]
    public void Constructor_ThrowsOnZeroMaxSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NexusStreamTransportPool(() => CreateMockTransport(), 0));
    }

    [Test]
    public void Constructor_ThrowsOnNegativeMaxSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NexusStreamTransportPool(() => CreateMockTransport(), -1));
    }

    [Test]
    public void Constructor_SetsMaxSize()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport(), 8);
        Assert.That(pool.MaxSize, Is.EqualTo(8));
    }

    [Test]
    public void InitialCount_IsZero()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport());
        Assert.That(pool.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task Rent_CallsFactoryWhenEmpty()
    {
        var factoryCalled = false;
        using var pool = new NexusStreamTransportPool(() =>
        {
            factoryCalled = true;
            return CreateMockTransport();
        });

        await using var transport = pool.Rent();

        Assert.That(factoryCalled, Is.True);
        Assert.That(transport, Is.Not.Null);
    }

    [Test]
    public void Rent_ReturnsRentedTransport()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport());

        var transport = pool.Rent();

        Assert.That(transport, Is.InstanceOf<IRentedNexusStreamTransport>());
        Assert.That(transport.IsReturned, Is.False);
    }

    [Test]
    public void Return_AddsToPool()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport(), 4);

        var inner = CreateMockTransport();
        pool.Return(inner);

        Assert.That(pool.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Return_ReusesFromPool()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport(), 4);

        // Return a transport to the pool
        var inner = CreateMockTransport();
        pool.Return(inner);

        Assert.That(pool.Count, Is.EqualTo(1));

        // Rent should reuse
        await using var rented = pool.Rent();

        Assert.That(pool.Count, Is.EqualTo(0));
    }

    [Test]
    public void Return_DisposesWhenAtCapacity()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport(), 2);

        // Fill the pool
        pool.Return(CreateMockTransport());
        pool.Return(CreateMockTransport());

        Assert.That(pool.Count, Is.EqualTo(2));

        // This should be disposed, not added
        pool.Return(CreateMockTransport());

        Assert.That(pool.Count, Is.EqualTo(2)); // Still at max
    }

    [Test]
    public void Dispose_ClearsPool()
    {
        var pool = new NexusStreamTransportPool(() => CreateMockTransport(), 4);

        pool.Return(CreateMockTransport());
        pool.Return(CreateMockTransport());

        Assert.That(pool.Count, Is.EqualTo(2));

        pool.Dispose();

        Assert.That(pool.Count, Is.EqualTo(0));
    }

    [Test]
    public void Dispose_ThrowsOnRent()
    {
        var pool = new NexusStreamTransportPool(() => CreateMockTransport());
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
    }

    [Test]
    public void RentedTransport_Dispose_ReturnsToPool()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport(), 4);

        var rented = pool.Rent();
        Assert.That(pool.Count, Is.EqualTo(0));

        rented.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert.That(rented.IsReturned, Is.True);
        // Note: Count might be 1 if the transport was returned successfully
    }

    [Test]
    public void RentedTransport_ThrowsAfterReturned()
    {
        using var pool = new NexusStreamTransportPool(() => CreateMockTransport());

        var rented = pool.Rent();
        rented.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await rented.OpenAsync("test", StreamAccessMode.Read));
    }

    private static NexusStreamTransport CreateMockTransport()
    {
        // Create a mock duplex pipe for testing
        var (clientPipe, _) = TestHelpers.CreatePipePair();
        return new NexusStreamTransport(clientPipe);
    }
}

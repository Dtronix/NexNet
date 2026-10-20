using System;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

/// <summary>
/// Tests for NexusStreamTransport.
/// These tests focus on validation and error cases that don't require
/// full transport-to-transport communication.
/// </summary>
[TestFixture]
public class NexusStreamTransportTests
{
    [Test]
    public void Constructor_NullPipe_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NexusStreamTransport(null!));
    }

    [Test]
    public void InitialState_IsNone()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        Assert.That(transport.State, Is.EqualTo(NexusStreamState.None));
    }

    [Test]
    public async Task OpenAsync_NullResourceId_Throws()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await transport.OpenAsync(null!, StreamAccessMode.Read));

        await pipe.CleanupAsync();
    }

    [Test]
    public async Task OpenAsync_EmptyResourceId_Throws()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await transport.OpenAsync("", StreamAccessMode.Read));

        await pipe.CleanupAsync();
    }

    [Test]
    public async Task Dispose_FromNone_TransitionsToClosedAsync()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        await transport.DisposeAsync();

        Assert.That(transport.State, Is.EqualTo(NexusStreamState.Closed));
        await pipe.CleanupAsync();
    }

    [Test]
    public async Task Dispose_Twice_IsIdempotent()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        await transport.DisposeAsync();
        await transport.DisposeAsync(); // Should not throw

        Assert.That(transport.State, Is.EqualTo(NexusStreamState.Closed));
        await pipe.CleanupAsync();
    }

    [Test]
    public async Task ReadyTask_ComesFromPipe()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        Assert.That(transport.ReadyTask, Is.SameAs(pipe.ReadyTask));

        await pipe.CleanupAsync();
    }

    [Test]
    public void ProvideFileAsync_ThrowsArgumentNullException_WhenPathIsNull()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await transport.ProvideFileAsync(null!));
    }

    [Test]
    public void ProvideStreamAsync_ThrowsArgumentNullException_WhenStreamIsNull()
    {
        var pipe = new MockNexusDuplexPipe();
        var transport = new NexusStreamTransport(pipe);

        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await transport.ProvideStreamAsync(null!));
    }
}

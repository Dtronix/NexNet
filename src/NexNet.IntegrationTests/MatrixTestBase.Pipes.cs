using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Pipes;

namespace NexNet.IntegrationTests;

/// <summary>Pipe, channel and flow-control helpers of <see cref="MatrixTestBase"/> (P01e).</summary>
/// <remarks>
/// A pipe the server opens to the client reaches the client's <c>ClientTaskValueWithDuplexPipe</c>, which the test
/// scripts with the hosts' built-in handlers (<c>pipe.echo</c>, <c>pipe.drain</c>, <c>pipe.readExact</c>). A handler
/// that ends with <see cref="ThenReport{TArgs, TResult}"/> calls the server's <c>ServerTaskWithParam</c> once it is
/// done, which the test waits for with <see cref="WaitForReportAsync"/>.
/// </remarks>
internal abstract partial class MatrixTestBase
{
    /// <summary>The bytes most pipe tests send.</summary>
    protected static readonly byte[] Data = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    /// <summary>
    /// Adds a step that calls the server's <c>ServerTaskWithParam(<paramref name="value"/>)</c> once the steps before it
    /// are done (<see cref="WaitForReportAsync"/>).
    /// </summary>
    protected static ScriptSlot<TArgs, TResult> ThenReport<TArgs, TResult>(ScriptSlot<TArgs, TResult> slot, int value)
        => slot.Invoke(Remote_IServerNexus.RemoteMethods.ServerTaskWithParam, new JsonArray(TypedCodecs.Get<int>().Encode(value)));

    /// <summary>Waits for the next <see cref="ThenReport{TArgs, TResult}"/> call and asserts its value.</summary>
    protected async Task WaitForReportAsync(int expected, double seconds = 1)
    {
        var value = await _reports.Reader.ReadAsync().AsTask().Timeout(T(seconds));
        await Assert.That(value).IsEqualTo(expected);
    }

    /// <summary>The options of the <c>pipe.readExact</c> handler.</summary>
    protected static JsonObject ReadExact(long length) => new() { ["length"] = length };

    /// <summary>Opens a pipe from the server to the client (the client's registrant role) and waits until it is ready.</summary>
    protected static async Task<IRentedNexusDuplexPipe> OpenToClientAsync(ServerNexus nexus)
    {
        var pipe = nexus.Context.CreatePipe();
        try
        {
            await nexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.ReadyTask.Timeout(T(1));
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    /// <summary>Reads <paramref name="input"/> until the other side completes and returns every byte read.</summary>
    protected static async Task<byte[]> ReadToEndAsync(PipeReader input, double seconds = 1)
    {
        var received = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await input.ReadAsync().AsTask().Timeout(T(seconds));
            foreach (var segment in result.Buffer)
                received.Write(segment.Span);
            input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                return received.WrittenSpan.ToArray();
        }
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes from <paramref name="input"/>.</summary>
    protected static async Task<byte[]> ReadExactAsync(PipeReader input, int length, double seconds = 1)
    {
        var result = await input.ReadAtLeastAsync(length).AsTask().Timeout(T(seconds));
        var data = result.Buffer.Slice(0, Math.Min(length, result.Buffer.Length)).ToArray();
        input.AdvanceTo(result.Buffer.GetPosition(data.Length));
        return data;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Collections;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.Uds;
using NexNet.Transports.WebSocket;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Canceling a forward while the destination waits for send credit: every byte is either delivered or left on the source.
/// </summary>
public class PipeForwardingCancellationTests
{
    [Test]
    public async Task ForwardToAsync_CanceledWhileDestinationWaitsForCredit_LosesNoBytes()
    {
        const int total = 4 * 1024 * 1024;
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nxprobe_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        var endPoint = new System.Net.Sockets.UnixDomainSocketEndPoint(path);
        var source = new System.Threading.Tasks.TaskCompletionSource<NexNet.Pipes.INexusDuplexPipe>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = new System.Threading.Tasks.TaskCompletionSource<NexNet.Pipes.INexusDuplexPipe>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
    
        var serverConfig = new NexNet.Transports.Uds.UdsServerConfig { EndPoint = endPoint, SessionBudget = 64 * 1024 * 1024 };
        var server = new NexNet.NexusServer<NexNet.IntegrationTests.TestInterfaces.ServerNexus, NexNet.IntegrationTests.TestInterfaces.ServerNexus.ClientProxy>(serverConfig, () =>
        {
            var nexus = new NexNet.IntegrationTests.TestInterfaces.ServerNexus();
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                (System.Threading.Interlocked.Increment(ref arrived) == 1 ? source : destination).TrySetResult(pipe);
                await done.Task.ConfigureAwait(false);
            };
            return nexus;
        });
        await server.StartAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
        var a = new NexNet.NexusClient<NexNet.IntegrationTests.TestInterfaces.ClientNexus, NexNet.IntegrationTests.TestInterfaces.ClientNexus.ServerProxy>(new NexNet.Transports.UdsClientConfig { EndPoint = endPoint }, new NexNet.IntegrationTests.TestInterfaces.ClientNexus());
        var b = new NexNet.NexusClient<NexNet.IntegrationTests.TestInterfaces.ClientNexus, NexNet.IntegrationTests.TestInterfaces.ClientNexus.ServerProxy>(new NexNet.Transports.UdsClientConfig { EndPoint = endPoint }, new NexNet.IntegrationTests.TestInterfaces.ClientNexus());
        try
        {
            await a.ConnectAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
            await b.ConnectAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
            var aPipe = a.CreatePipe();
            await a.Proxy.ServerTaskValueWithDuplexPipe(aPipe);
            await aPipe.ReadyTask.WaitAsync(System.TimeSpan.FromSeconds(5));
            var serverSource = await source.Task.WaitAsync(System.TimeSpan.FromSeconds(5));
            var bPipe = b.CreatePipe();
            await b.Proxy.ServerTaskValueWithDuplexPipe(bPipe);
            await bPipe.ReadyTask.WaitAsync(System.TimeSpan.FromSeconds(5));
            var serverDestination = await destination.Task.WaitAsync(System.TimeSpan.FromSeconds(5));
            await serverSource.ReadyTask.WaitAsync(System.TimeSpan.FromSeconds(5));
            await serverDestination.ReadyTask.WaitAsync(System.TimeSpan.FromSeconds(5));
    
            using var cts = new System.Threading.CancellationTokenSource();
            var forward = NexNet.Pipes.NexusPipeForwardingExtensions.ForwardToAsync(serverSource, serverDestination, cts.Token).AsTask();
            var writing = System.Threading.Tasks.Task.Run(async () =>
            {
                var chunk = new byte[64 * 1024];
                for (var offset = 0; offset < total; offset += chunk.Length)
                    await aPipe.Output.WriteAsync(chunk);
                await aPipe.Output.CompleteAsync();
            });
    
            var waiting = false;
            for (var i = 0; i < 200 && !waiting; i++)
            {
                await System.Threading.Tasks.Task.Delay(25);
                waiting = System.Linq.Enumerable.Any(System.Linq.Enumerable.SelectMany(server.GetSnapshot().Sessions, s => s.Pipes), p => p.WaitingForCredit != null);
            }
            NUnit.Framework.Assert.That(waiting, NUnit.Framework.Is.True, "The forwarder never waited for the destination's credit.");
    
            cts.Cancel();
            try { await forward.WaitAsync(System.TimeSpan.FromSeconds(5)); } catch (System.OperationCanceledException) { }
    
            long atServer = 0;
            while (true)
            {
                var r = await serverSource.Input.ReadAsync().AsTask().WaitAsync(System.TimeSpan.FromSeconds(10));
                atServer += r.Buffer.Length;
                serverSource.Input.AdvanceTo(r.Buffer.End);
                if (r.IsCompleted) break;
            }
            await writing.WaitAsync(System.TimeSpan.FromSeconds(10));
    
            await serverDestination.Output.CompleteAsync();
            long atB = 0;
            while (true)
            {
                var r = await bPipe.Input.ReadAsync().AsTask().WaitAsync(System.TimeSpan.FromSeconds(10));
                atB += r.Buffer.Length;
                bPipe.Input.AdvanceTo(r.Buffer.End);
                if (r.IsCompleted) break;
            }
    
            NUnit.Framework.Assert.That(atB + atServer, NUnit.Framework.Is.EqualTo(total), $"Delivered to B: {atB}, left on the source: {atServer}.");
        }
        finally
        {
            done.TrySetResult();
            _ = a.DisconnectAsync();
            _ = b.DisconnectAsync();
            _ = server.StopAsync();
        }
    }
}

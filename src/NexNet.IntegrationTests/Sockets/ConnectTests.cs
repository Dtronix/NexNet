using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NexNet.Internals.Pipelines;
using NexNet.Internals.Pipelines.Internal;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Sockets
{
    [TestFixture]
    public class ConnectTests
    {
        private readonly BufferedTestLogger _logger = new();

        [SetUp]
        public void SetUp()
        {
            _logger.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            _logger.FlushOnFailure();
        }

        [Test]
        public void CanCheckDependencies()
        {
            SocketConnection.AssertDependencies();
        }

        [Test]
        public async Task Connect()
        {
            var timeout = Task.Delay(6000);
            var code = ConnectImpl();
            var first = await Task.WhenAny(timeout, code);
            if (first == timeout) Throw.Timeout("unknown timeout");
            await first; // check outcome
        }

        private async Task ConnectImpl()
        {
            int port = 16320 + new Random().Next(100);
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            object waitForRunning = new object();
            Task<string> server;
            _logger.Log("Starting server...");
            lock (waitForRunning)
            {
                server = Task.Run(() => SyncEchoServer(waitForRunning, endpoint));
                if (!Monitor.Wait(waitForRunning, 5000))
                    Throw.Timeout("Server didn't start");
            }

            if (server.IsFaulted)
            {
                await server; // early exit if broken
            }

            string actual;
            _logger.Log("connecting...");
            using var conn = await SocketConnection.ConnectAsync(endpoint,
                connectionOptions: SocketConnectionOptions.ZeroLengthReads).ConfigureAwait(false);
            var data = Encoding.ASCII.GetBytes("Hello, world!");
            _logger.Log("sending message...");
            await conn.Output.WriteAsync(data).ConfigureAwait(false);

            Assert.That(conn.Output.CanGetUnflushedBytes, Is.True, "conn.Output.CanGetUnflushedBytes");

            _logger.Log("completing output");
            conn.Output.Complete();

            _logger.Log("awaiting server...");
            actual = await server;

            Assert.That(actual, Is.EqualTo("Hello, world!"));

            string returned;
            _logger.Log("buffering response...");
            while (true)
            {
                var result = await conn.Input.ReadAsync().ConfigureAwait(false);

                var buffer = result.Buffer;
                _logger.Log($"received {buffer.Length} bytes");
                if (result.IsCompleted)
                {
                    returned = Encoding.ASCII.GetString(result.Buffer.ToArray());
                    _logger.Log($"received: '{returned}'");
                    break;
                }

                _logger.Log("advancing");
                conn.Input.AdvanceTo(buffer.Start, buffer.End);
            }

            Assert.That(returned, Is.EqualTo("!dlrow ,olleH"));

            _logger.Log("disposing");
        }

        private Task<string> SyncEchoServer(object ready, IPEndPoint endpoint)
        {
            try
            {
                var listener = new TcpListener(endpoint);
                _logger.Log($"[Server] starting on {endpoint}...");
                listener.Start();
                lock (ready)
                {
                    Monitor.PulseAll(ready);
                }
                _logger.Log("[Server] running; waiting for connection...");
                string s;
                using (var socket = listener.AcceptSocket())
                {
                    _logger.Log($"[Server] accepted connection");
                    using var ns = new NetworkStream(socket);
                    using (var reader = new StreamReader(ns, Encoding.ASCII, false, 1024, true))
                    using (var writer = new StreamWriter(ns, Encoding.ASCII, 1024, true))
                    {
                        s = reader.ReadToEnd();
                        _logger.Log($"[Server] received '{s}'; replying in reverse...");
                        char[] chars = s.ToCharArray();
                        Array.Reverse(chars);
                        var t = new string(chars);
                        writer.Write(t);
                    }
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                }
                _logger.Log($"[Server] shutting down");
                listener.Stop();
                return Task.FromResult(s);
            }
            catch (Exception ex)
            {
                _logger.Log($"[Server] faulted: {ex.Message}");
                lock (ready)
                {
                    Monitor.PulseAll(ready);
                }
                return Task.FromException<string>(ex);
            }
        }
    }
}

using NexNet;
using NexNet.Transports;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace NexNetStressTest;

class Program
{
    private static readonly StressTestConfiguration _config = new();
    private static readonly StressTestMetrics _metrics = new();
    private static volatile bool _stopRequested = false;
    private static dynamic? _server;

    static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _stopRequested = true;
            Console.WriteLine("\nStop requested. Shutting down gracefully...");
        };

        ParseArguments(args);
        _config.PrintConfiguration();

        Console.WriteLine("\nPress any key to start the stress test, or Ctrl+C to exit...");
        Console.ReadKey();

        try
        {
            await RunStressTestAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during stress test: {ex.Message}");
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    private static void ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) break;

            var key = args[i].ToLower();
            var value = args[i + 1];

            switch (key)
            {
                case "--connections":
                    if (int.TryParse(value, out var connections))
                        _config.ConcurrentConnections = connections;
                    break;
                case "--fire-and-forget":
                    if (int.TryParse(value, out var fireAndForget))
                        _config.FireAndForgetInvocationsPerConnection = fireAndForget;
                    break;
                case "--return-value":
                    if (int.TryParse(value, out var returnValue))
                        _config.ReturnValueInvocationsPerConnection = returnValue;
                    break;
                case "--duration":
                    if (int.TryParse(value, out var duration))
                        _config.TestDurationSeconds = duration;
                    break;
                case "--port":
                    if (int.TryParse(value, out var port))
                        _config.ServerPort = port;
                    break;
                case "--tls":
                    if (bool.TryParse(value, out var tls))
                        _config.UseTls = tls;
                    break;
            }
        }
    }

    private static async Task RunStressTestAsync()
    {
        // Start server
        Console.WriteLine("Starting server...");
        await StartServerAsync();

        if (_stopRequested) return;

        // Wait for server to be ready
        await Task.Delay(1000);

        Console.WriteLine("Starting stress test...");
        _metrics.Start();

        var progressTask = StartProgressReporting();
        var clientTasks = new List<Task>();

        // Create and start client connections
        for (int i = 0; i < _config.ConcurrentConnections && !_stopRequested; i++)
        {
            var clientId = i;
            var task = RunClientAsync(clientId);
            clientTasks.Add(task);
        }

        // Wait for all clients to complete or timeout
        var completionTask = Task.WhenAll(clientTasks);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_config.TestDurationSeconds + _config.WarmupDurationSeconds + 10));
        
        await Task.WhenAny(completionTask, timeoutTask);

        _stopRequested = true;
        _metrics.Stop();

        await progressTask;

        // Stop server
        if (_server != null)
        {
            Console.WriteLine("Stopping server...");
            await _server.StopAsync();
        }

        // Print final results
        var results = _metrics.GetResults();
        results.PrintResults();
    }

    private static async Task StartServerAsync()
    {
        ServerConfig serverConfig;
        
        if (_config.UseTls)
        {
            serverConfig = new TcpTlsServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Parse(_config.ServerAddress), _config.ServerPort),
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certPass")
                },
            };
        }
        else
        {
            serverConfig = new TcpServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Parse(_config.ServerAddress), _config.ServerPort)
            };
        }

        _server = StressTestServerNexus.CreateServer(serverConfig, () => new StressTestServerNexus());
        await _server.StartAsync();
        Console.WriteLine($"Server started on {_config.ServerAddress}:{_config.ServerPort} (TLS: {_config.UseTls})");
    }

    private static async Task RunClientAsync(int clientId)
    {
        StressTestClientNexus? clientNexus = null;
        NexusClient<StressTestClientNexus, StressTestClientNexus.ServerProxy> client = null!;

        try
        {
            var connectionStopwatch = Stopwatch.StartNew();
            
            // Create and connect client
            ClientConfig clientConfig;
            
            if (_config.UseTls)
            {
                clientConfig = new TcpTlsClientConfig()
                {
                    EndPoint = new IPEndPoint(IPAddress.Parse(_config.ServerAddress), _config.ServerPort),
                    SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
                    {
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        AllowRenegotiation = false,
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
                    }
                };
            }
            else
            {
                clientConfig = new TcpClientConfig()
                {
                    EndPoint = new IPEndPoint(IPAddress.Parse(_config.ServerAddress), _config.ServerPort)
                };
            }

            clientNexus = new StressTestClientNexus();
            client = StressTestClientNexus.CreateClient(clientConfig, clientNexus);
            var connectionResult = await client.TryConnectAsync();

            connectionStopwatch.Stop();

            if (connectionResult.Success)
            {
                _metrics.IncrementCounter("ConnectionsSuccessful");
                _metrics.RecordConnectionTime(connectionStopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                _metrics.IncrementCounter("ConnectionsFailed");
                return;
            }

            // Warmup period
            await Task.Delay(TimeSpan.FromSeconds(_config.WarmupDurationSeconds));

            if (_stopRequested) return;

            // Run stress test operations
            var tasks = new List<Task>();

            // Fire-and-forget operations
            for (int i = 0; i < _config.FireAndForgetInvocationsPerConnection && !_stopRequested; i++)
            {
                var opIndex = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        
                        switch (opIndex % 3)
                        {
                            case 0:
                                client.Proxy.FireAndForgetSimple(opIndex);
                                break;
                            case 1:
                                client.Proxy.FireAndForgetWithString($"Client{clientId}_Op{opIndex}");
                                break;
                            case 2:
                                client.Proxy.FireAndForgetComplex(opIndex, $"Data{opIndex}", DateTime.UtcNow);
                                break;
                        }
                        
                        stopwatch.Stop();
                        _metrics.RecordLatency(stopwatch.Elapsed.TotalMilliseconds);
                        _metrics.IncrementCounter("FireAndForgetInvocations");
                    }
                    catch
                    {
                        _metrics.IncrementCounter("InvocationErrors");
                    }
                }));
            }

            // Return value operations
            for (int i = 0; i < _config.ReturnValueInvocationsPerConnection && !_stopRequested; i++)
            {
                var opIndex = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        
                        switch (opIndex % 3)
                        {
                            case 0:
                                await client.Proxy.GetNextNumber();
                                break;
                            case 1:
                                await client.Proxy.ProcessData($"Client{clientId}_Data{opIndex}");
                                break;
                            case 2:
                                await client.Proxy.ComplexOperation(_config.ComplexOperationIterations, $"Payload{opIndex}");
                                break;
                        }
                        
                        stopwatch.Stop();
                        _metrics.RecordLatency(stopwatch.Elapsed.TotalMilliseconds);
                        _metrics.IncrementCounter("ReturnValueInvocations");
                    }
                    catch
                    {
                        _metrics.IncrementCounter("InvocationErrors");
                    }
                }));
            }

            // Wait for all operations to complete
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _metrics.IncrementCounter("ConnectionsFailed");
            Console.WriteLine($"Client {clientId} error: {ex.Message}");
        }
        finally
        {
            if(client != null)
                await client.DisposeAsync();
        }
    }

    private static async Task StartProgressReporting()
    {
        if (!_config.ReportProgress) return;

        while (!_stopRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.ProgressReportIntervalSeconds));
            
            if (_stopRequested) break;

            var connections = _metrics.GetCounter("ConnectionsSuccessful");
            var fireAndForget = _metrics.GetCounter("FireAndForgetInvocations");
            var returnValue = _metrics.GetCounter("ReturnValueInvocations");
            var errors = _metrics.GetCounter("InvocationErrors");

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connections: {connections}, F&F: {fireAndForget:N0}, RV: {returnValue:N0}, Errors: {errors}");
        }
    }
}

using System.IO;
using System.Net.Sockets;
using NexNet.Transports;
using NexNet.Transports.Uds;
using System.Threading.Tasks;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Sockets;

/// <summary>
/// UDS server socket file handling: <see cref="UdsServerConfig.SocketFileMode"/>,
/// <see cref="UdsServerConfig.RemoveStaleSocketFile"/> and removal of the socket file on a graceful stop.
/// </summary>
internal class UdsSocketFileTests : BaseTests
{
    private DirectoryInfo _dir = null!;
    private string _path = null!;

    public override void SetUp()
    {
        base.SetUp();
        _dir = Directory.CreateTempSubdirectory("udsf");
        _path = Path.Combine(_dir.FullName, "s.sock");
        CurrentUdsPath = new UnixDomainSocketEndPoint(_path);
    }

    public override void TearDown()
    {
        base.TearDown();
        try { _dir.Delete(true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Leaves a socket file at <paramref name="path"/> that nothing listens on, as a crashed server does.
    /// .NET deletes the file it bound when the socket closes, so the socket is bound under another name and the
    /// file is moved into place first.
    /// </summary>
    private static async Task CreateStaleSocketFile(string path)
    {
        var tmp = path + ".tmp";
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP))
        {
            socket.Bind(new UnixDomainSocketEndPoint(tmp));
            File.Move(tmp, path);
        }

        await Assert.That(File.Exists(path)).IsTrue().Because("stale socket file was not created");
    }

    private UdsServerConfig CreateUdsServerConfig() => (UdsServerConfig)CreateServerConfig(Type.Uds);

    [Test]
    public async Task Defaults_AreOff()
    {
        var config = new UdsServerConfig();
        await Assert.That(config.RemoveStaleSocketFile).IsFalse();
        await Assert.That(config.SocketFileMode).IsNull();
    }

    [Test]
    public async Task StaleSocketFile_WithoutOption_FailsAsToday()
    {
        CreateStaleSocketFile(_path);
        var config = CreateUdsServerConfig();
        var server = CreateServer(config, null);

        var ex = await await Assert.ThrowsAsync<TransportException>(() => server.StartAsync().Timeout(1));
        await Assert.That(ex!.Error).IsEqualTo(TransportError.AddressInUse);
        await Assert.That(File.Exists(_path)).IsTrue();
    }

    [Test]
    public async Task StaleSocketFile_IsRemoved_AndServerStarts()
    {
        CreateStaleSocketFile(_path);
        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var (server, client, _) = CreateServerClient(config, CreateClientConfig(Type.Uds), startServer: false);

        await server.StartAsync().Timeout(2);
        await client.ConnectAsync().Timeout(1);

        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    public async Task LiveServer_IsNotDisturbed()
    {
        var (live, client, _) = CreateServerClient(CreateUdsServerConfig(), CreateClientConfig(Type.Uds), startServer: false);
        await live.StartAsync().Timeout(1);

        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var second = CreateServer(config, null);

        var ex = await await Assert.ThrowsAsync<TransportException>(() => second.StartAsync().Timeout(2));
        await Assert.That(ex!.Error).IsEqualTo(TransportError.AddressInUse);
        await Assert.That(File.Exists(_path)).IsTrue().Because("the live server's socket file was deleted");

        // The live server still accepts connections on its endpoint.
        await client.ConnectAsync().Timeout(1);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    public async Task RegularFileAtPath_IsNotDeleted()
    {
        await File.WriteAllTextAsync(_path, "not a socket");
        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var server = CreateServer(config, null);

        await Assert.ThrowsAsync<TransportException>(() => server.StartAsync().Timeout(2));
        await Assert.That(File.Exists(_path)).IsTrue().Because("a regular file was deleted");
        await Assert.That(await File.ReadAllTextAsync(_path)).IsEqualTo("not a socket");
    }

    [Test]
    public async Task SymlinkAtPath_IsNotDeleted()
    {
        var target = Path.Combine(_dir.FullName, "target.txt");
        await File.WriteAllTextAsync(target, "target");
        try
        {
            File.CreateSymbolicLink(_path, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Skip.Test($"Symbolic links cannot be created here: {e.Message}");
        }

        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var server = CreateServer(config, null);
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        Assert.That(async () => await server.StartAsync().Timeout(2), Throws.Exception);
        await Assert.That(new FileInfo(_path).LinkTarget).IsNotNull().Because("a symbolic link at the path was deleted");
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("target");
    }

    [Test]
    public async Task DirectoryAtPath_IsNotDeleted()
    {
        Directory.CreateDirectory(_path);
        File.WriteAllText(Path.Combine(_path, "keep.txt"), "keep");
        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var server = CreateServer(config, null);
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        Assert.That(async () => await server.StartAsync().Timeout(2), Throws.Exception);
        await Assert.That(File.Exists(Path.Combine(_path, "keep.txt"))).IsTrue().Because("a directory was deleted");
    }

    [Test]
    public async Task SocketFileMode_IsAppliedOnUnix_RejectedOnWindows()
    {
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var config = CreateUdsServerConfig();
        config.SocketFileMode = mode;

        if (OperatingSystem.IsWindows())
        {
            var server = CreateServer(config, null);
            var ex = await await Assert.ThrowsAsync<PlatformNotSupportedException>(() => server.StartAsync().Timeout(1));
            await Assert.That(ex!.Message).Contains(nameof(UdsServerConfig.SocketFileMode));
            return;
        }

        var (factory, client, _) = CreateServerClient(config, CreateClientConfig(Type.Uds), startServer: false);
        await factory.StartAsync().Timeout(1);

        await Assert.That(File.GetUnixFileMode(_path)).IsEqualTo(mode);
        await client.ConnectAsync().Timeout(1);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    public async Task GracefulStop_DeletesSocketFile()
    {
        var server = CreateServer(CreateUdsServerConfig(), null);
        await server.StartAsync().Timeout(1);
        await Assert.That(File.Exists(_path)).IsTrue();

        await server.StopAsync().Timeout(1);
        await Assert.That(File.Exists(_path)).IsFalse();
    }

    [Test]
    public async Task GracefulStop_LeavesAReplacedFileAlone()
    {
        var server = CreateServer(CreateUdsServerConfig(), null);
        await server.StartAsync().Timeout(1);

        // Someone else removed the socket file and put another file at the path while the server ran.
        File.Delete(_path);
        await File.WriteAllTextAsync(_path, "replacement");

        await server.StopAsync().Timeout(1);
        await Assert.That(File.Exists(_path)).IsTrue().Because("the server deleted a file it did not create");
        await Assert.That(await File.ReadAllTextAsync(_path)).IsEqualTo("replacement");
    }
}
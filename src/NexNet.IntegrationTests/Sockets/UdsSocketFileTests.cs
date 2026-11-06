using System.Net.Sockets;
using NexNet.Transports;
using NexNet.Transports.Uds;
using NUnit.Framework;
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
    private static void CreateStaleSocketFile(string path)
    {
        var tmp = path + ".tmp";
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP))
        {
            socket.Bind(new UnixDomainSocketEndPoint(tmp));
            File.Move(tmp, path);
        }

        Assert.That(File.Exists(path), Is.True, "stale socket file was not created");
    }

    private UdsServerConfig CreateUdsServerConfig() => (UdsServerConfig)CreateServerConfig(Type.Uds);

    [Test]
    public void Defaults_AreOff()
    {
        var config = new UdsServerConfig();
        Assert.That(config.RemoveStaleSocketFile, Is.False);
        Assert.That(config.SocketFileMode, Is.Null);
    }

    [Test]
    public async Task StaleSocketFile_WithoutOption_FailsAsToday()
    {
        CreateStaleSocketFile(_path);
        var config = CreateUdsServerConfig();
        var server = CreateServer(config, null);

        var ex = await Assert.ThrowsAsync<TransportException>(() => server.StartAsync().Timeout(1));
        Assert.That(ex!.Error, Is.EqualTo(TransportError.AddressInUse));
        Assert.That(File.Exists(_path), Is.True);
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

        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [Test]
    public async Task LiveServer_IsNotDisturbed()
    {
        var (live, client, _) = CreateServerClient(CreateUdsServerConfig(), CreateClientConfig(Type.Uds), startServer: false);
        await live.StartAsync().Timeout(1);

        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var second = CreateServer(config, null);

        var ex = await Assert.ThrowsAsync<TransportException>(() => second.StartAsync().Timeout(2));
        Assert.That(ex!.Error, Is.EqualTo(TransportError.AddressInUse));
        Assert.That(File.Exists(_path), Is.True, "the live server's socket file was deleted");

        // The live server still accepts connections on its endpoint.
        await client.ConnectAsync().Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [Test]
    public async Task RegularFileAtPath_IsNotDeleted()
    {
        await File.WriteAllTextAsync(_path, "not a socket");
        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var server = CreateServer(config, null);

        await Assert.ThrowsAsync<TransportException>(() => server.StartAsync().Timeout(2));
        Assert.That(File.Exists(_path), Is.True, "a regular file was deleted");
        Assert.That(await File.ReadAllTextAsync(_path), Is.EqualTo("not a socket"));
    }

    [Test]
    public async Task DirectoryAtPath_IsNotDeleted()
    {
        Directory.CreateDirectory(_path);
        File.WriteAllText(Path.Combine(_path, "keep.txt"), "keep");
        var config = CreateUdsServerConfig();
        config.RemoveStaleSocketFile = true;
        var server = CreateServer(config, null);

        Assert.That(async () => await server.StartAsync().Timeout(2), Throws.Exception);
        Assert.That(File.Exists(Path.Combine(_path, "keep.txt")), Is.True, "a directory was deleted");
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
            var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => server.StartAsync().Timeout(1));
            Assert.That(ex!.Message, Does.Contain(nameof(UdsServerConfig.SocketFileMode)));
            return;
        }

        var (factory, client, _) = CreateServerClient(config, CreateClientConfig(Type.Uds), startServer: false);
        await factory.StartAsync().Timeout(1);

        Assert.That(File.GetUnixFileMode(_path), Is.EqualTo(mode));
        await client.ConnectAsync().Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [Test]
    public async Task GracefulStop_DeletesSocketFile()
    {
        var server = CreateServer(CreateUdsServerConfig(), null);
        await server.StartAsync().Timeout(1);
        Assert.That(File.Exists(_path), Is.True);

        await server.StopAsync().Timeout(1);
        Assert.That(File.Exists(_path), Is.False);
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
        Assert.That(File.Exists(_path), Is.True, "the server deleted a file it did not create");
        Assert.That(await File.ReadAllTextAsync(_path), Is.EqualTo("replacement"));
    }
}

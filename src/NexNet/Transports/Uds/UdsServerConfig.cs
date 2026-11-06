using System;
using System.IO;
using System.Net.Sockets;
using NexNet.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.Uds;

/// <summary>
/// Configurations for the server to allow connections from Unix Domain Socket (UDS) NexNet clients.
/// </summary>
public sealed class UdsServerConfig : ServerConfig
{
    /// <inheritdoc />
    protected internal override string TransportName => "uds";

    private UnixDomainSocketEndPoint _endPoint = null!;

    /// <summary>
    /// Endpoint to bind to.
    /// </summary>
    public UnixDomainSocketEndPoint EndPoint
    {
        get => _endPoint;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _endPoint = value;
        }
    }

    /// <summary>
    /// The permissions given to the socket file right after it is created, before the server listens. Null (the
    /// default) leaves the mode the process umask gives. Unix only: on Windows a non-null value makes
    /// <see cref="NexusServer{TServerNexus,TClientProxy}.StartAsync"/> throw <see cref="PlatformNotSupportedException"/>;
    /// use the folder's ACLs there instead. Ignored for abstract socket addresses, which have no file.
    /// </summary>
    public UnixFileMode? SocketFileMode { get; set; }

    /// <summary>
    /// When true, a socket file left at <see cref="EndPoint"/> by a server that is no longer running (for example
    /// after a crash) is deleted before the server binds. The file is deleted only when a connection attempt to it is
    /// refused and the path is confirmed to be a socket file; a regular file, directory, link or any file whose type
    /// cannot be determined is never deleted, and start fails as it does with the option off. If a server is
    /// listening on the path, start fails with <see cref="TransportError.AddressInUse"/> and the file is left alone.
    /// Another server binding the same path between the check and the delete is not detected. Default false.
    /// </summary>
    public bool RemoveStaleSocketFile { get; set; }

    /// <inheritdoc />
    protected override async ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        if (SocketFileMode != null && OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                $"{nameof(UdsServerConfig)}.{nameof(SocketFileMode)} is not supported on Windows; leave it null and secure the socket's folder with ACLs.");

        var path = UdsSocketFile.GetFilePath(EndPoint);
        if (path == null)
            return SocketTransportListener.Create(this, EndPoint, SocketType.Stream, ProtocolType.IP);

        if (RemoveStaleSocketFile && (File.Exists(path) || Directory.Exists(path)))
            await RemoveStaleFileAsync(path, cancellationToken).ConfigureAwait(false);

        return CreateFileListener(path);
    }

    private async ValueTask RemoveStaleFileAsync(string path, CancellationToken cancellationToken)
    {
        // Probe, type check and delete run back to back to keep the race window small.
        var probe = await UdsSocketFile.ProbeAsync(path, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        if (probe == UdsSocketFile.ProbeResult.Listening)
            throw new TransportException(TransportError.AddressInUse,
                $"A server is already listening on the socket file '{path}'.", null);

        if (probe != UdsSocketFile.ProbeResult.Refused || !UdsSocketFile.IsSocketFile(path))
            return; // Not provably stale: Bind fails as it does without the option.

        try
        {
            File.Delete(path);
            Logger?.LogInfo($"Removed stale socket file '{path}'.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger?.LogWarning(e, $"Could not remove stale socket file '{path}'.");
        }
    }

    private ITransportListener CreateFileListener(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
        UdsSocketFile.FileIdentity? identity = null;
        try
        {
            // The wrapper keeps .NET from deleting the path on close; the identity check below does it instead.
            socket.Bind(new UdsNoAutoDeleteEndPoint(EndPoint));
            identity = UdsSocketFile.TryGetIdentity(path);

            if (SocketFileMode is { } mode && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, mode);

            socket.Listen(AcceptorBacklog);
        }
        catch (Exception e)
        {
            socket.Dispose();
            UdsSocketFile.DeleteIfSame(path, identity);
            if (e is SocketException se)
                throw new TransportException(se.SocketErrorCode, se.Message, se);
            throw;
        }

        return new SocketTransportListener(this, socket, () => UdsSocketFile.DeleteIfSame(path, identity));
    }
    
    /// <summary>
    /// Sets the server mode.
    /// </summary>
    public UdsServerConfig()
        : base(ServerConnectionMode.Listener)
    {
        
    }
}

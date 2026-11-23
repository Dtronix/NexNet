using System.Security.Cryptography;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.Inspector.Tap;

/// <summary>What an inspector endpoint serves to a viewer or the CLI.</summary>
internal partial interface IInspectorEndpointNexus
{
    /// <summary>API version, role, recording policy and schema hash.</summary>
    ValueTask<InspectorServerInfo> GetServerInfo();

    /// <summary>The live tapped sessions.</summary>
    ValueTask<InspectorSessionInfo[]> ListSessions();

    /// <summary>Validates an attach and arms it for the next <see cref="OpenStream"/>.</summary>
    ValueTask<InspectorStatus> Attach(InspectorAttachRequest request);

    /// <summary>Validates a record-all and arms it for the next <see cref="OpenStream"/>.</summary>
    ValueTask<InspectorStatus> RecordAll(InspectorRecordRequest request);

    /// <summary>
    /// Streams the armed attach or record-all as <c>.nntl</c> blocks over <paramref name="pipe"/> until detach, the
    /// session's close (attach), or the viewer completing the pipe. NexNet pipe methods return no value, which is why
    /// the request is validated by a separate call first.
    /// </summary>
    ValueTask OpenStream(INexusDuplexPipe pipe);

    /// <summary>Ends the current stream (its last blocks and footer are still sent) and disarms.</summary>
    ValueTask Detach();
}

/// <summary>Methods the endpoint calls on a viewer: none; everything flows through the stream pipe.</summary>
internal partial interface IInspectorViewerNexus
{
}

/// <summary>One viewer's session on the inspector endpoint. At most one attach or record-all streams at a time.</summary>
[Nexus<IInspectorEndpointNexus, IInspectorViewerNexus>(NexusType = NexusType.Server)]
internal partial class InspectorEndpointNexus
{
    private readonly NexusInspector _inspector;
    private readonly byte[] _token;
    private readonly Lock _gate = new();
    private InspectorAttachRequest? _armedAttach;
    private InspectorRecordRequest? _armedRecord;
    private TapSink? _active;

    public InspectorEndpointNexus(NexusInspector inspector, byte[] token)
    {
        _inspector = inspector;
        _token = token;
    }

    protected override ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken)
    {
        var presented = authenticationToken ?? ReadOnlyMemory<byte>.Empty;
        IIdentity? identity = CryptographicOperations.FixedTimeEquals(presented.Span, _token)
            ? new DefaultIdentity { DisplayName = "inspector" }
            : null;
        return new ValueTask<IIdentity?>(identity);
    }

    public ValueTask<InspectorServerInfo> GetServerInfo() => new(_inspector.GetServerInfo());

    public ValueTask<InspectorSessionInfo[]> ListSessions() => new(_inspector.GetSessions());

    public ValueTask<InspectorStatus> Attach(InspectorAttachRequest request)
    {
        var status = _inspector.CheckAttach(request, out _);
        if (status != InspectorStatus.Ok)
            return new ValueTask<InspectorStatus>(status);

        lock (_gate)
        {
            if (_active != null)
                return new ValueTask<InspectorStatus>(InspectorStatus.Busy);
            _armedAttach = request;
            _armedRecord = null;
        }

        return new ValueTask<InspectorStatus>(InspectorStatus.Ok);
    }

    public ValueTask<InspectorStatus> RecordAll(InspectorRecordRequest request)
    {
        var status = _inspector.CheckRecordAll();
        if (status != InspectorStatus.Ok)
            return new ValueTask<InspectorStatus>(status);

        lock (_gate)
        {
            if (_active != null)
                return new ValueTask<InspectorStatus>(InspectorStatus.Busy);
            _armedRecord = request;
            _armedAttach = null;
        }

        return new ValueTask<InspectorStatus>(InspectorStatus.Ok);
    }

    public async ValueTask OpenStream(INexusDuplexPipe pipe)
    {
        InspectorAttachRequest? attach;
        InspectorRecordRequest? record;
        lock (_gate)
        {
            if (_active != null)
                return;
            attach = _armedAttach;
            record = _armedRecord;
            _armedAttach = null;
            _armedRecord = null;
        }

        var output = new PipeSinkOutput(pipe.Output);
        TapSink? sink = null;
        if (attach != null)
        {
            // Checked again: the session may have gone, or the inspector been disposed, since Attach.
            if (_inspector.CheckAttach(attach, out var session) == InspectorStatus.Ok)
                sink = _inspector.StartAttach(session!, attach, output);
        }
        else if (record != null && _inspector.CheckRecordAll() == InspectorStatus.Ok)
        {
            sink = _inspector.StartRecordAll(output, record.Overflow, record.Snapshot);
        }

        if (sink == null)
            return;

        lock (_gate)
            _active = sink;

        _ = WatchViewerAsync(pipe, sink);
        await sink.Completion.ConfigureAwait(false);

        lock (_gate)
        {
            if (_active == sink)
                _active = null;
        }
    }

    public async ValueTask Detach()
    {
        TapSink? sink;
        lock (_gate)
        {
            _armedAttach = null;
            _armedRecord = null;
            sink = _active;
        }

        if (sink == null)
            return;
        sink.Stop();
        await sink.Completion.ConfigureAwait(false);
    }

    protected override async ValueTask OnDisconnected(DisconnectReason reason) => await Detach().ConfigureAwait(false);

    /// <summary>The viewer completing its end of the pipe means it is done: the sink stops.</summary>
    private static async Task WatchViewerAsync(INexusDuplexPipe pipe, TapSink sink)
    {
        try
        {
            while (true)
            {
                var result = await pipe.Input.ReadAsync().ConfigureAwait(false);
                pipe.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
        }
        catch (Exception)
        {
            // A failed read means the viewer or its connection is gone, which ends the stream the same way.
        }

        sink.Stop();
    }
}

/// <summary>The viewer side of the endpoint; it serves nothing.</summary>
[Nexus<IInspectorViewerNexus, IInspectorEndpointNexus>(NexusType = NexusType.Client)]
internal partial class InspectorViewerNexus
{
}

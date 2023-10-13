using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using NexNet.Transports;
#pragma warning disable CA1416

namespace NexNet.Quic;

internal static class QuicHelpers
{
    public static TransportError GetTransportError(QuicError error)
    {
        return error switch
        {
            QuicError.InternalError => TransportError.InternalError,
            QuicError.ConnectionAborted => TransportError.ConnectionAborted,
            QuicError.StreamAborted => TransportError.StreamAborted,
            QuicError.AddressInUse => TransportError.AddressInUse,
            QuicError.InvalidAddress => TransportError.InvalidAddress,
            QuicError.ConnectionTimeout => TransportError.ConnectionTimeout,
            QuicError.HostUnreachable => TransportError.Unreachable,
            QuicError.ConnectionRefused => TransportError.ConnectionRefused,
            QuicError.VersionNegotiationError => TransportError.VersionNegotiationError,
            QuicError.ConnectionIdle => TransportError.ConnectionIdle,
            QuicError.ProtocolError => TransportError.ProtocolError,
            QuicError.OperationAborted => TransportError.OperationAborted,
            _ => TransportError.InternalError,
        };
    }
}

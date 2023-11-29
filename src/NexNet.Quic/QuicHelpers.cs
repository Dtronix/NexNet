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
            QuicError.AlpnInUse => TransportError.AddressInUse,
            QuicError.ConnectionTimeout => TransportError.ConnectionTimeout,
            QuicError.ConnectionRefused => TransportError.ConnectionRefused,
            QuicError.VersionNegotiationError => TransportError.VersionNegotiationError,
            QuicError.ConnectionIdle => TransportError.ConnectionIdle,
            QuicError.OperationAborted => TransportError.OperationAborted,
            QuicError.Success => TransportError.Success,
            QuicError.TransportError => TransportError.ProtocolError,
            QuicError.CallbackError => TransportError.InternalError,
            _ => TransportError.InternalError
        };
    }
}

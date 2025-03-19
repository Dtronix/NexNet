// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NexNet.Transports.HttpSocket.Internals;

[Serializable]
[System.Runtime.CompilerServices.TypeForwardedFrom("System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")]
public sealed class HttpSocketException : Win32Exception
{
    private readonly HttpSocketError _webSocketErrorCode;

    public HttpSocketException()
        : this(Marshal.GetLastPInvokeError())
    {
    }

    public HttpSocketException(HttpSocketError error)
        : this(error, GetErrorMessage(error))
    {
    }

    public HttpSocketException(HttpSocketError error, string? message) : base(message ?? Strings.net_HttpSockets_Generic)
    {
        _webSocketErrorCode = error;
    }

    public HttpSocketException(HttpSocketError error, Exception? innerException)
        : this(error, GetErrorMessage(error), innerException)
    {
    }

    public HttpSocketException(HttpSocketError error, string? message, Exception? innerException)
        : base(message ?? Strings.net_HttpSockets_Generic, innerException)
    {
        _webSocketErrorCode = error;
    }

    public HttpSocketException(int nativeError)
        : base(nativeError)
    {
        _webSocketErrorCode = !Succeeded(nativeError) ? HttpSocketError.NativeError : HttpSocketError.Success;
        SetErrorCodeOnError(nativeError);
    }

    public HttpSocketException(int nativeError, string? message)
        : base(nativeError, message)
    {
        _webSocketErrorCode = !Succeeded(nativeError) ? HttpSocketError.NativeError : HttpSocketError.Success;
        SetErrorCodeOnError(nativeError);
    }

    public HttpSocketException(int nativeError, Exception? innerException)
        : base(Strings.net_HttpSockets_Generic, innerException)
    {
        _webSocketErrorCode = !Succeeded(nativeError) ? HttpSocketError.NativeError : HttpSocketError.Success;
        SetErrorCodeOnError(nativeError);
    }

    public HttpSocketException(HttpSocketError error, int nativeError)
        : this(error, nativeError, GetErrorMessage(error))
    {
    }

    public HttpSocketException(HttpSocketError error, int nativeError, string? message)
        : base(message ?? Strings.net_HttpSockets_Generic)
    {
        _webSocketErrorCode = error;
        SetErrorCodeOnError(nativeError);
    }

    public HttpSocketException(HttpSocketError error, int nativeError, Exception? innerException)
        : this(error, nativeError, GetErrorMessage(error), innerException)
    {
    }

    public HttpSocketException(HttpSocketError error, int nativeError, string? message, Exception? innerException)
        : base(message ?? Strings.net_HttpSockets_Generic, innerException)
    {
        _webSocketErrorCode = error;
        SetErrorCodeOnError(nativeError);
    }

    public HttpSocketException(string? message)
        : base(message ?? Strings.net_HttpSockets_Generic)
    {
    }

    public HttpSocketException(string? message, Exception? innerException)
        : base(message ?? Strings.net_HttpSockets_Generic, innerException)
    {
    }
        
    /*

    [Obsolete(Obsoletions.LegacyFormatterImplMessage, DiagnosticId = Obsoletions.LegacyFormatterImplDiagId, UrlFormat = Obsoletions.SharedUrlFormat)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    private HttpSocketException(SerializationInfo serializationInfo, StreamingContext streamingContext)
        : base(serializationInfo, streamingContext)
    {
    }

    [Obsolete(Obsoletions.LegacyFormatterImplMessage, DiagnosticId = Obsoletions.LegacyFormatterImplDiagId, UrlFormat = Obsoletions.SharedUrlFormat)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(HttpSocketErrorCode), _webSocketErrorCode);
    }*/

    public override int ErrorCode
    {
        get
        {
            return base.NativeErrorCode;
        }
    }

    public HttpSocketError HttpSocketErrorCode
    {
        get
        {
            return _webSocketErrorCode;
        }
    }

    private static string GetErrorMessage(HttpSocketError error) =>
        // Provide a canned message for the error type.
        error switch
        {
            HttpSocketError.InvalidMessageType => string.Format(Strings.net_HttpSockets_InvalidMessageType_Generic,
                   $"{nameof(HttpSocket)}.{nameof(HttpSocket.CloseAsync)}",
                   $"{nameof(HttpSocket)}.{nameof(HttpSocket.CloseOutputAsync)}"),
            HttpSocketError.Faulted => Strings.net_HttpSockets_HttpSocketBaseFaulted,
            HttpSocketError.NotAHttpSocket => Strings.net_HttpSockets_NotAHttpSocket_Generic,
            HttpSocketError.UnsupportedVersion => Strings.net_HttpSockets_UnsupportedHttpSocketVersion_Generic,
            HttpSocketError.UnsupportedProtocol => Strings.net_HttpSockets_UnsupportedProtocol_Generic,
            HttpSocketError.HeaderError => Strings.net_HttpSockets_HeaderError_Generic,
            HttpSocketError.ConnectionClosedPrematurely => Strings.net_HttpSockets_ConnectionClosedPrematurely_Generic,
            HttpSocketError.InvalidState => Strings.net_HttpSockets_InvalidState_Generic,
            _ => Strings.net_HttpSockets_Generic,
        };

    // Set the error code only if there is an error (i.e. nativeError >= 0). Otherwise the code fails during deserialization
    // as the Exception..ctor() throws on setting HResult to 0. The default for HResult is -2147467259.
    private void SetErrorCodeOnError(int nativeError)
    {
        if (!Succeeded(nativeError))
        {
            HResult = nativeError;
        }
    }

    private static bool Succeeded(int hr)
    {
        return (hr >= 0);
    }
}
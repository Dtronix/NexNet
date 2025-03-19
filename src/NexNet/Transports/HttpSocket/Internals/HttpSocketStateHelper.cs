// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

namespace NexNet.Transports.HttpSocket.Internals;

internal static class HttpSocketStateHelper
{
    /// <summary>Valid states to be in when calling SendAsync.</summary>
    internal const ManagedHttpSocketStates ValidSendStates = ManagedHttpSocketStates.Open | ManagedHttpSocketStates.CloseReceived;
    /// <summary>Valid states to be in when calling ReceiveAsync.</summary>
    internal const ManagedHttpSocketStates ValidReceiveStates = ManagedHttpSocketStates.Open | ManagedHttpSocketStates.CloseSent;
    /// <summary>Valid states to be in when calling CloseOutputAsync.</summary>
    internal const ManagedHttpSocketStates ValidCloseOutputStates = ManagedHttpSocketStates.Open | ManagedHttpSocketStates.CloseReceived;
    /// <summary>Valid states to be in when calling CloseAsync.</summary>
    internal const ManagedHttpSocketStates ValidCloseStates = ManagedHttpSocketStates.Open | ManagedHttpSocketStates.CloseReceived | ManagedHttpSocketStates.CloseSent;

    internal static bool IsValidSendState(HttpSocketState state) => ValidSendStates.HasFlag(ToFlag(state));

    internal static void ThrowIfInvalidState(HttpSocketState currentState, bool isDisposed, Exception? innerException, ManagedHttpSocketStates validStates)
    {
        ManagedHttpSocketStates state = ToFlag(currentState);

        if ((state & validStates) == 0)
        {
            string invalidStateMessage = string.Format(Strings.net_HttpSockets_InvalidState, currentState, validStates);;

            throw new HttpSocketException(HttpSocketError.InvalidState, invalidStateMessage, innerException);
        }

        if (innerException is not null)
        {
            Debug.Assert(state == ManagedHttpSocketStates.Aborted);
            throw new OperationCanceledException(nameof(HttpSocketState.Aborted), innerException);
        }

        // Ordering is important to maintain .NET 4.5 HttpSocket implementation exception behavior.
        ObjectDisposedException.ThrowIf(isDisposed, typeof(HttpSocket));
    }

    private static ManagedHttpSocketStates ToFlag(HttpSocketState value)
    {
        ManagedHttpSocketStates flag = (ManagedHttpSocketStates)(1 << (int)value);
        Debug.Assert(Enum.IsDefined(flag));
        return flag;
    }
}
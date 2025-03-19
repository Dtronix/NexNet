// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace NexNet.Transports.HttpSocket.Internals;

[Flags]
internal enum ManagedHttpSocketStates
{
    None = 0,

    // HttpSocketState.None = 0       -- this state is invalid for the managed implementation
    // HttpSocketState.Connecting = 1 -- this state is invalid for the managed implementation
    Open = 0x04,           // HttpSocketState.Open = 2
    CloseSent = 0x08,      // HttpSocketState.CloseSent = 3
    CloseReceived = 0x10,  // HttpSocketState.CloseReceived = 4
    Closed = 0x20,         // HttpSocketState.Closed = 5
    Aborted = 0x40,        // HttpSocketState.Aborted = 6

    All = Open | CloseSent | CloseReceived | Closed | Aborted
}
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NexNet.Transports.HttpSocket.Internals;

internal sealed partial class NetEventSource
    {
        private const int AssociateEventId = 3;

        /// <summary>Logs a relationship between two objects.</summary>
        /// <param name="first">The first object.</param>
        /// <param name="second">The second object.</param>
        /// <param name="memberName">The calling member.</param>
        [NonEvent]
        public static void Associate(object first, object second, [CallerMemberName] string? memberName = null) =>
            Associate(first, first, second, memberName);

        /// <summary>Logs a relationship between two objects.</summary>
        /// <param name="thisOrContextObject">`this`, or another object that serves to provide context for the operation.</param>
        /// <param name="first">The first object.</param>
        /// <param name="second">The second object.</param>
        /// <param name="memberName">The calling member.</param>
        [NonEvent]
        public static void Associate(object? thisOrContextObject, object first, object second, [CallerMemberName] string? memberName = null) =>
            Log.Associate(IdOf(thisOrContextObject), memberName, IdOf(first), IdOf(second));

        [Event(AssociateEventId, Level = EventLevel.Informational, Keywords = Keywords.Default, Message = "[{2}]<-->[{3}]")]
        private void Associate(string thisOrContextObject, string? memberName, string first, string second)
        {
            Debug.Assert(IsEnabled());
            WriteEvent(AssociateEventId, thisOrContextObject, memberName ?? MissingMember, first, second);
        }

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
                   Justification = EventSourceSuppressMessage)]
        [NonEvent]
        private unsafe void WriteEvent(int eventId, string? arg1, string? arg2, string? arg3, string? arg4)
        {
            arg1 ??= "";
            arg2 ??= "";
            arg3 ??= "";
            arg4 ??= "";

            fixed (char* string1Bytes = arg1)
            fixed (char* string2Bytes = arg2)
            fixed (char* string3Bytes = arg3)
            fixed (char* string4Bytes = arg4)
            {
                const int NumEventDatas = 4;
                EventData* descrs = stackalloc EventData[NumEventDatas];

                descrs[0] = new EventData
                {
                    DataPointer = (IntPtr)string1Bytes,
                    Size = ((arg1.Length + 1) * 2)
                };
                descrs[1] = new EventData
                {
                    DataPointer = (IntPtr)string2Bytes,
                    Size = ((arg2.Length + 1) * 2)
                };
                descrs[2] = new EventData
                {
                    DataPointer = (IntPtr)string3Bytes,
                    Size = ((arg3.Length + 1) * 2)
                };
                descrs[3] = new EventData
                {
                    DataPointer = (IntPtr)string4Bytes,
                    Size = ((arg4.Length + 1) * 2)
                };

                WriteEventCore(eventId, NumEventDatas, descrs);
            }
        }
    }

internal sealed partial class NetEventSource : EventSource
    {
        private const string EventSourceSuppressMessage = "Parameters to this method are primitive and are trimmer safe";

        /// <summary>The single event source instance to use for all logging.</summary>
        public static readonly NetEventSource Log = new NetEventSource();

        public static class Keywords
        {
            public const EventKeywords Default = (EventKeywords)0x0001;
            public const EventKeywords Debug = (EventKeywords)0x0002;
        }

        private const string MissingMember = "(?)";
        private const string NullInstance = "(null)";
        private const string StaticMethodObject = "(static)";
        private const string NoParameters = "";

        private const int InfoEventId = 1;
        private const int ErrorEventId = 2;
        // private const int AssociateEventId = 3; // Defined in NetEventSource.Common.Associate.cs
        // private const int DumpArrayEventId = 4; // Defined in NetEventSource.Common.DumpBuffer.cs

        private const int NextAvailableEventId = 5; // Update this value whenever new events are added.  Derived types should base all events off of this to avoid conflicts.

        /// <summary>Logs an information message.</summary>
        /// <param name="thisOrContextObject">`this`, or another object that serves to provide context for the operation.</param>
        /// <param name="formattableString">The message to be logged.</param>
        /// <param name="memberName">The calling member.</param>
        [NonEvent]
        public static void Info(object? thisOrContextObject, FormattableString? formattableString = null, [CallerMemberName] string? memberName = null) =>
            Log.Info(IdOf(thisOrContextObject), memberName, formattableString != null ? Format(formattableString) : NoParameters);

        /// <summary>Logs an information message.</summary>
        /// <param name="thisOrContextObject">`this`, or another object that serves to provide context for the operation.</param>
        /// <param name="message">The message to be logged.</param>
        /// <param name="memberName">The calling member.</param>
        [NonEvent]
        public static void Info(object? thisOrContextObject, object? message, [CallerMemberName] string? memberName = null) =>
            Log.Info(IdOf(thisOrContextObject), memberName, Format(message));

        [Event(InfoEventId, Level = EventLevel.Informational, Keywords = Keywords.Default)]
        private void Info(string thisOrContextObject, string? memberName, string? message)
        {
            Debug.Assert(IsEnabled());
            WriteEvent(InfoEventId, thisOrContextObject, memberName ?? MissingMember, message);
        }

        /// <summary>Logs an error message.</summary>
        /// <param name="thisOrContextObject">`this`, or another object that serves to provide context for the operation.</param>
        /// <param name="formattableString">The message to be logged.</param>
        /// <param name="memberName">The calling member.</param>
        [NonEvent]
        public static void Error(object? thisOrContextObject, FormattableString formattableString, [CallerMemberName] string? memberName = null) =>
            Log.ErrorMessage(IdOf(thisOrContextObject), memberName, Format(formattableString));

        /// <summary>Logs an error message.</summary>
        /// <param name="thisOrContextObject">`this`, or another object that serves to provide context for the operation.</param>
        /// <param name="message">The message to be logged.</param>
        /// <param name="memberName">The calling member.</param>
        [NonEvent]
        public static void Error(object? thisOrContextObject, object message, [CallerMemberName] string? memberName = null) =>
            Log.ErrorMessage(IdOf(thisOrContextObject), memberName, Format(message));

        [Event(ErrorEventId, Level = EventLevel.Error, Keywords = Keywords.Default)]
        private void ErrorMessage(string thisOrContextObject, string? memberName, string? message)
        {
            Debug.Assert(IsEnabled());
            WriteEvent(ErrorEventId, thisOrContextObject, memberName ?? MissingMember, message);
        }

        [NonEvent]
        public static string IdOf(object? value) => value != null ? value.GetType().Name + "#" + GetHashCode(value) : NullInstance;

        [NonEvent]
        public static int GetHashCode(object? value) => value?.GetHashCode() ?? 0;

        [NonEvent]
        public static string? Format(object? value)
        {
            // If it's null, return a known string for null values
            if (value == null)
            {
                return NullInstance;
            }

            // Give another partial implementation a chance to provide its own string representation
            string? result = null;
            AdditionalCustomizedToString(value, ref result);
            if (result is not null)
            {
                return result;
            }

            // Format arrays with their element type name and length
            if (value is Array arr)
            {
                return $"{arr.GetType().GetElementType()}[{((Array)value).Length}]";
            }

            // Format ICollections as the name and count
            if (value is ICollection c)
            {
                return $"{c.GetType().Name}({c.Count})";
            }

            // Format SafeHandles as their type, hash code, and pointer value
            if (value is SafeHandle handle)
            {
                return $"{handle.GetType().Name}:{handle.GetHashCode()}(0x{handle.DangerousGetHandle():X})";
            }

            // Format IntPtrs as hex
            if (value is IntPtr)
            {
                return $"0x{value:X}";
            }

            // If the string representation of the instance would just be its type name,
            // use its id instead.
            string? toString = value.ToString();
            if (toString == null || toString == value.GetType().FullName)
            {
                return IdOf(value);
            }

            // Otherwise, return the original object so that the caller does default formatting.
            return value.ToString();
        }

        [NonEvent]
        private static string Format(FormattableString s)
        {
            switch (s.ArgumentCount)
            {
                case 0: return s.Format;
                case 1: return string.Format(s.Format, Format(s.GetArgument(0)));
                case 2: return string.Format(s.Format, Format(s.GetArgument(0)), Format(s.GetArgument(1)));
                case 3: return string.Format(s.Format, Format(s.GetArgument(0)), Format(s.GetArgument(1)), Format(s.GetArgument(2)));
                default:
                    string?[] formattedArgs = new string?[s.ArgumentCount];
                    for (int i = 0; i < formattedArgs.Length; i++)
                    {
                        formattedArgs[i] = Format(s.GetArgument(i));
                    }
                    return string.Format(s.Format, formattedArgs);
            }
        }

        static partial void AdditionalCustomizedToString(object value, ref string? result);

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
                   Justification = EventSourceSuppressMessage)]
        [NonEvent]
        private unsafe void WriteEvent(int eventId, string? arg1, string? arg2, int arg3)
        {
            arg1 ??= "";
            arg2 ??= "";

            fixed (char* arg1Ptr = arg1)
            fixed (char* arg2Ptr = arg2)
            {
                const int NumEventDatas = 3;
                EventData* descrs = stackalloc EventData[NumEventDatas];

                descrs[0] = new EventData
                {
                    DataPointer = (IntPtr)(arg1Ptr),
                    Size = (arg1.Length + 1) * sizeof(char)
                };
                descrs[1] = new EventData
                {
                    DataPointer = (IntPtr)(arg2Ptr),
                    Size = (arg2.Length + 1) * sizeof(char)
                };
                descrs[2] = new EventData
                {
                    DataPointer = (IntPtr)(&arg3),
                    Size = sizeof(int)
                };

                WriteEventCore(eventId, NumEventDatas, descrs);
            }
        }
    }

[EventSource(Name = "Private.InternalDiagnostics.HttpSocketsTests")]
internal sealed partial class NetEventSource
{
    // NOTE
    // - The 'Start' and 'Stop' suffixes on the following event names have special meaning in EventSource. They
    //   enable creating 'activities'.
    //   For more information, take a look at the following blog post:
    //   https://blogs.msdn.microsoft.com/vancem/2015/09/14/exploring-eventsource-activity-correlation-and-causation-features/
    // - A stop event's event id must be next one after its start event.

    private const int KeepAliveSentId = NextAvailableEventId;
    //private const int KeepAliveSentId = 500;
    private const int KeepAliveAckedId = KeepAliveSentId + 1;

    private const int WsTraceId = KeepAliveAckedId + 1;

    private const int CloseStartId = WsTraceId + 1;
    private const int CloseStopId = CloseStartId + 1;

    private const int ReceiveStartId = CloseStopId + 1;
    private const int ReceiveStopId = ReceiveStartId + 1;

    private const int SendStartId = ReceiveStopId + 1;
    private const int SendStopId = SendStartId + 1;

    private const int MutexEnterId = SendStopId + 1;
    private const int MutexExitId = MutexEnterId + 1;

    //
    // Keep-Alive
    //

    private const string Ping = "Ping";
    private const string Pong = "Pong";

    [Event(KeepAliveSentId, Keywords = Keywords.Debug, Level = EventLevel.Informational)]
    private void KeepAliveSent(string objName, string opcode, long payload) =>
        WriteEvent(KeepAliveSentId, objName, opcode, payload);

    [Event(KeepAliveAckedId, Keywords = Keywords.Debug, Level = EventLevel.Informational)]
    private void KeepAliveAcked(string objName, long payload) =>
        WriteEvent(KeepAliveAckedId, objName, payload);

    [NonEvent]
    public static void KeepAlivePingSent(object? obj, long payload)
    {
        Debug.Assert(Log.IsEnabled());
        Log.KeepAliveSent(IdOf(obj), Ping, payload);
    }

    [NonEvent]
    public static void UnsolicitedPongSent(object? obj)
    {
        Debug.Assert(Log.IsEnabled());
        Log.KeepAliveSent(IdOf(obj), Pong, 0);
    }

    [NonEvent]
    public static void PongResponseReceived(object? obj, long payload)
    {
        Debug.Assert(Log.IsEnabled());
        Log.KeepAliveAcked(IdOf(obj), payload);
    }

    //
    // Debug Messages
    //

    [Event(WsTraceId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void WsTrace(string objName, string memberName, string message) =>
        WriteEvent(WsTraceId, objName, memberName, message);

    [NonEvent]
    public static void TraceErrorMsg(object? obj, Exception exception, [CallerMemberName] string? memberName = null)
        => Trace(obj, $"{exception.GetType().Name}: {exception.Message}", memberName);

    [NonEvent]
    public static void TraceException(object? obj, Exception exception, [CallerMemberName] string? memberName = null)
        => Trace(obj, exception.ToString(), memberName);

    [NonEvent]
    public static void Trace(object? obj, string? message = null, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.WsTrace(IdOf(obj), memberName ?? MissingMember, message ?? memberName ?? string.Empty);
    }

    //
    // Close
    //

    [Event(CloseStartId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void CloseStart(string objName, string memberName) =>
        WriteEvent(CloseStartId, objName, memberName);

    [Event(CloseStopId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void CloseStop(string objName, string memberName) =>
        WriteEvent(CloseStopId, objName, memberName);

    [NonEvent]
    public static void CloseAsyncPrivateStarted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseStart(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void CloseAsyncPrivateCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.CloseStop(IdOf(obj), memberName ?? MissingMember);
    }

    //
    // ReceiveAsyncPrivate
    //

    [Event(ReceiveStartId, Keywords = Keywords.Debug, Level = EventLevel.Informational)]
    private void ReceiveStart(string objName, string memberName, int bufferLength) =>
        WriteEvent(ReceiveStartId, objName, memberName, bufferLength);

    [Event(ReceiveStopId, Keywords = Keywords.Debug, Level = EventLevel.Informational)]
    private void ReceiveStop(string objName, string memberName) =>
        WriteEvent(ReceiveStopId, objName, memberName);

    [NonEvent]
    public static void ReceiveAsyncPrivateStarted(object? obj, int bufferLength, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.ReceiveStart(IdOf(obj), memberName ?? MissingMember, bufferLength);
    }

    [NonEvent]
    public static void ReceiveAsyncPrivateCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.ReceiveStop(IdOf(obj), memberName ?? MissingMember);
    }

    //
    // SendFrameAsync
    //

    [Event(SendStartId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void SendStart(string objName, string memberName, string opcode, int bufferLength) =>
        WriteEvent(SendStartId, objName, memberName, opcode, bufferLength);

    [Event(SendStopId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void SendStop(string objName, string memberName) =>
        WriteEvent(SendStopId, objName, memberName);

    [NonEvent]
    public static void SendFrameAsyncStarted(object? obj, string opcode, int bufferLength, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.SendStart(IdOf(obj), memberName ?? MissingMember, opcode, bufferLength);
    }

    [NonEvent]
    public static void SendFrameAsyncCompleted(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.SendStop(IdOf(obj), memberName ?? MissingMember);
    }

    //
    // AsyncMutex
    //

    [Event(MutexEnterId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void MutexEnter(string objName, string memberName) =>
        WriteEvent(MutexEnterId, objName, memberName);

    [Event(MutexExitId, Keywords = Keywords.Debug, Level = EventLevel.Verbose)]
    private void MutexExit(string objName, string memberName) =>
        WriteEvent(MutexExitId, objName, memberName);

    [NonEvent]
    public static void MutexEntered(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.MutexEnter(IdOf(obj), memberName ?? MissingMember);
    }

    [NonEvent]
    public static void MutexExited(object? obj, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(Log.IsEnabled());
        Log.MutexExit(IdOf(obj), memberName ?? MissingMember);
    }

    //
    // WriteEvent overloads
    //

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
        Justification = EventSourceSuppressMessage)]
    [NonEvent]
    private unsafe void WriteEvent(int eventId, string arg1, string arg2, long arg3)
    {
        fixed (char* arg1Ptr = arg1)
        fixed (char* arg2Ptr = arg2)
        {
            const int NumEventDatas = 3;
            EventData* descrs = stackalloc EventData[NumEventDatas];

            descrs[0] = new EventData
            {
                DataPointer = (IntPtr)(arg1Ptr),
                Size = (arg1.Length + 1) * sizeof(char)
            };
            descrs[1] = new EventData
            {
                DataPointer = (IntPtr)(arg2Ptr),
                Size = (arg2.Length + 1) * sizeof(char)
            };
            descrs[2] = new EventData
            {
                DataPointer = (IntPtr)(&arg3),
                Size = sizeof(long)
            };

            WriteEventCore(eventId, NumEventDatas, descrs);
        }
    }

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
        Justification = EventSourceSuppressMessage)]
    [NonEvent]
    private unsafe void WriteEvent(int eventId, string arg1, string arg2, string arg3, int arg4)
    {
        fixed (char* arg1Ptr = arg1)
        fixed (char* arg2Ptr = arg2)
        fixed (char* arg3Ptr = arg3)
        {
            const int NumEventDatas = 4;
            EventData* descrs = stackalloc EventData[NumEventDatas];

            descrs[0] = new EventData
            {
                DataPointer = (IntPtr)(arg1Ptr),
                Size = (arg1.Length + 1) * sizeof(char)
            };
            descrs[1] = new EventData
            {
                DataPointer = (IntPtr)(arg2Ptr),
                Size = (arg2.Length + 1) * sizeof(char)
            };
            descrs[2] = new EventData
            {
                DataPointer = (IntPtr)(arg3Ptr),
                Size = (arg3.Length + 1) * sizeof(char)
            };
            descrs[3] = new EventData
            {
                DataPointer = (IntPtr)(&arg4),
                Size = sizeof(int)
            };

            WriteEventCore(eventId, NumEventDatas, descrs);
        }
    }

}
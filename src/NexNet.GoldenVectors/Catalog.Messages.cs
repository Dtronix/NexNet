namespace NexNet.GoldenVectors;

public static partial class GoldenGenerator
{
    /// <summary>The protocol message bodies (protocol specification §4).</summary>
    private static IEnumerable<MessageSchema> Messages()
    {
        (string, string)[] greeting =
        [
            ("version", "string"), ("serverNexusHash", "int"), ("clientNexusHash", "int"), ("authenticationToken", "byte[]"),
            ("maxMessageSize", "int"), ("invocationCredits", "int"),
        ];
        yield return new MessageSchema("ClientGreeting", 100, 6, 6, greeting);
        yield return new MessageSchema("ClientGreetingReconnection", 101, 6, 6, greeting);
        yield return new MessageSchema("ServerGreeting", 105, 4, null,
            [("version", "int"), ("clientId", "long"), ("maxMessageSize", "int"), ("invocationCredits", "int"), ("idleTimeoutMs", "uint")]);
        yield return new MessageSchema("Invocation", 110, 4, 4,
            [("invocationId", "ushort"), ("methodId", "ushort"), ("flags", "byte"), ("arguments", TypeExpr.Raw)]);
        yield return new MessageSchema("InvocationCancellation", 111, 1, 1, [("invocationId", "ushort")]);
        yield return new MessageSchema("InvocationResult", 112, 2, 3, [("invocationId", "ushort"), ("state", "byte"), ("result", TypeExpr.Raw)]);
        yield return new MessageSchema("MessageStart", 113, 4, 4, [("invocationId", "ushort"), ("kind", "byte"), ("innerType", "byte"), ("totalLength", "uint")]);
        yield return new MessageSchema("MessageAbort", 115, 3, 3, [("invocationId", "ushort"), ("kind", "byte"), ("reason", "byte")]);
        yield return new MessageSchema("InvocationCredit", 116, 1, 1, [("credits", "ushort")]);
        yield return new MessageSchema("DuplexPipeUpdateState", 120, 3, 3, [("pipeId", "ushort"), ("state", "byte"), ("window", "uint")]);
        yield return new MessageSchema("RemoteErrorDetail", null, 3, 3, [("code", "int?"), ("type", "string"), ("message", "string")]);
    }

    private static void MessageVectors(GoldenBuilder b)
    {
        b.File("messages");

        // ClientGreeting (§4.1): the golden vector of the C# ProtocolMessageTests, with the default trailing fields.
        b.BothJson("ClientGreeting", "msg.client-greeting", """["v1",300,-5,"AQI=",4194304,32]""", "maxMessageSize 4 MiB and invocationCredits 32 are the defaults");
        b.BothJson("ClientGreeting", "msg.client-greeting.no-version", """[null,0,0,"",4194304,32]""", "an unversioned server and no token: nil and an empty bin");
        b.BothJson("ClientGreetingReconnection", "msg.client-greeting-reconnection", """["v1",300,-5,"AQI=",65536,1]""");
        b.DecodeJson("ClientGreeting", "msg.client-greeting.nil-token", "96a27631cd012cfbc0ce0040000020", """["v1",300,-5,"",4194304,32]""", "a nil token is no token");
        b.RejectJson("ClientGreeting", "msg.reject.client-greeting.count-5", "95a27631cd012cfbc400ce00400000", "count", "message bodies have their exact element count");
        b.RejectJson("ClientGreeting", "msg.reject.client-greeting.trailing", "96a27631cd012cfbc400ce004000002000", "trailing");
        b.RejectJson("ClientGreeting", "msg.reject.client-greeting.token-length", "96c00000c67fffffff", "length", "the bin length is checked before the token is read");

        // ServerGreeting (§4.2): idleTimeoutMs is a trailing field.
        b.BothJson("ServerGreeting", "msg.server-greeting", """[1,4294967296,65536,128,30000]""");
        b.BothJson("ServerGreeting", "msg.server-greeting.idle-max", """[1,1,65536,1,4294967295]""");
        b.DecodeJson("ServerGreeting", "msg.server-greeting.older-server", "940101ce00010000cc80", """[1,1,65536,128,0]""", "no idleTimeoutMs: not advertised (0)");
        b.DecodeJson("ServerGreeting", "msg.server-greeting.newer-server", "970101ce00010000cc80cd1770a361626392c0c3", """[1,1,65536,128,6000]""", "fields after idleTimeoutMs are skipped");
        b.RejectJson("ServerGreeting", "msg.reject.server-greeting.count-3", "93010203", "count", "fewer than four fields");
        b.RejectJson("ServerGreeting", "msg.reject.server-greeting.c1", "92c101", "reserved");

        // Invocation (§4.3): the arguments are one embedded array.
        b.BothJson("Invocation", "msg.invocation", """[7,300,0,"kgWheA=="]""", "arguments [5, \"x\"]");
        b.BothJson("Invocation", "msg.invocation.no-arguments", """[1,2,0,"kA=="]""", "no serialized parameters: an empty array (0x90)");
        b.BothJson("Invocation", "msg.invocation.ignore-return", """[0,65535,1,"kaFh"]""", "IgnoreReturn: the id has no meaning and is 0");
        b.BothJson("Invocation", "msg.invocation.duplex-pipe", """[0,4,3,"kQE="]""", "IgnoreReturn | DuplexPipe; the pipe parameter is its initial pipe id");
        b.RejectJson("Invocation", "msg.reject.invocation.truncated-arguments", "940102009205", "truncated");
        b.RejectJson("Invocation", "msg.reject.invocation.count-3", "93010200", "count");

        // InvocationResult (§4.4): [id, state] or [id, state, result].
        b.BothJson("InvocationResult", "msg.result.none", """[3,1]""", "CompletedResult without a result (a void method)");
        b.BothJson("InvocationResult", "msg.result.nil", """[3,1,null]""", "a nil result: null, as nil is everywhere in the value model");
        b.BothJson("InvocationResult", "msg.result.value", """[65535,1,"zQPo"]""", "the result 1000");
        b.BothJson("InvocationResult", "msg.result.exception", """[3,2,"k80B9KNGb2+jYmFy"]""", "Exception with a RemoteErrorDetail [500, \"Foo\", \"bar\"]");
        b.BothJson("InvocationResult", "msg.result.unauthorized", """[3,3]""");
        b.BothJson("InvocationResult", "msg.result.canceled", """[3,4]""");
        b.RejectJson("InvocationResult", "msg.reject.result.count-1", "9103", "count");
        b.RejectJson("InvocationResult", "msg.reject.result.count-4", "940301c0c0", "count");

        // InvocationCancellation (§4.5), InvocationCredit (§4.7), MessageStart/Abort (§4.8, §4.9), DuplexPipeUpdateState (§4.6).
        b.BothJson("InvocationCancellation", "msg.cancellation", """[1000]""");
        b.BothJson("InvocationCancellation", "msg.cancellation.max", """[65535]""");
        b.RejectJson("InvocationCancellation", "msg.reject.cancellation.65536", "91ce00010000", "range", "invocation ids are uint16");
        b.RejectJson("InvocationCancellation", "msg.reject.cancellation.m1", "91ff", "range");
        b.RejectJson("InvocationCancellation", "msg.reject.cancellation.count-0", "90", "count");
        b.BothJson("InvocationCredit", "msg.credit", """[1]""");
        b.BothJson("InvocationCredit", "msg.credit.max", """[65535]""");
        b.BothJson("MessageStart", "msg.message-start.argument", """[5,0,110,70000]""", "a large argument: the inner Invocation body is 70000 bytes");
        b.BothJson("MessageStart", "msg.message-start.result", """[6,1,112,65536]""");
        b.BothJson("MessageAbort", "msg.message-abort.cancelled", """[5,0,2]""");
        b.BothJson("MessageAbort", "msg.message-abort.ack", """[5,1,3]""");
        b.BothJson("DuplexPipeUpdateState", "msg.pipe-state.ready", """[513,2,262144]""", "pipe id client 1, server 2: 1 | 2 << 8; Ready grants a 256 KiB window");
        b.BothJson("DuplexPipeUpdateState", "msg.pipe-state.refused", """[1,3,0]""");
        b.BothJson("DuplexPipeUpdateState", "msg.pipe-state.complete", """[513,0,0]""");
        b.RejectJson("DuplexPipeUpdateState", "msg.reject.pipe-state.count-2", "92cd020102", "count");

        // RemoteErrorDetail: the result of an Exception InvocationResult.
        b.BothJson("RemoteErrorDetail", "msg.error-detail", """[500,"System.InvalidOperationException","boom"]""");
        b.BothJson("RemoteErrorDetail", "msg.error-detail.empty", """[null,null,null]""");
        b.RejectJson("RemoteErrorDetail", "msg.reject.error-detail.count-2", "92c0c0", "count");
        b.RejectJson("RemoteErrorDetail", "msg.reject.error-detail.type-too-long", "93c0da0101" + Repeat("61", 257) + "c0", "value", "the type is at most 256 chars");
    }

    private static void Frames(GoldenBuilder b)
    {
        b.File("frames");

        b.Preamble("preamble", """[1]""", "magic 4e 6e 50 14, three reserved zero bytes, protocol version 1");
        b.Frame("frame.ping", """[1]""", "no length, no body");
        b.Frame("frame.disconnect-graceful", """[21]""");
        b.Frame("frame.disconnect-protocol-error", """[22]""");
        b.Frame("frame.disconnect-unassigned", """[26]""", "an unassigned code in 20-38 is still a disconnect");
        b.Frame("frame.disconnect-stalled", """[37]""");
        b.Frame("frame.client-greeting", """[100,["v1",300,-5,"AQI=",4194304,32]]""", "type, uint16 little-endian body length, body");
        b.Frame("frame.server-greeting", """[105,[1,4294967296,65536,128,30000]]""");
        b.Frame("frame.invocation", """[110,[7,300,0,"kgWheA=="]]""");
        b.Frame("frame.invocation-cancellation", """[111,[1000]]""");
        b.Frame("frame.invocation-result", """[112,[3,1,"zQPo"]]""");
        b.Frame("frame.message-start", """[113,[5,0,110,70000]]""");
        b.Frame("frame.message-abort", """[115,[5,0,2]]""");
        b.Frame("frame.invocation-credit", """[116,[4]]""");
        b.Frame("frame.pipe-state", """[120,[513,2,262144]]""");
        b.Frame("frame.pipe-write", """[50,1,2,"AQID"]""", "post-header: client pipe id, server pipe id; the body is raw bytes");
        b.Frame("frame.pipe-write.empty", """[50,1,2,""]""");
        b.Frame("frame.pipe-window-update", """[51,1,2,262144]""", "body: the credit, uint32 little-endian");
        b.Frame("frame.message-chunk", """[114,5,0,"AQID"]""", "post-header: invocation id (uint16 little-endian), kind");
    }
}

package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.NexusException;
import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.NexusRemoteInvocationException;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.NexusUnauthorizedException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.serialization.MsgPackException;
import com.dtronix.nexnet.session.SessionClosedException;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeoutException;

/** Maps Java exceptions to the error objects of the control protocol (§8) and error kinds to Java exceptions. */
final class Errors {
    /** The message of the C# client when it invokes on a disconnected session. */
    static final String NOT_CONNECTED = "Session is not connected";

    private Errors() {
    }

    /**
     * The exception behind the wrappers of futures.
     *
     * @param error the exception
     * @return the cause of a completion or execution exception, otherwise the exception
     */
    static Throwable unwrap(Throwable error) {
        Throwable current = error;
        while ((current instanceof CompletionException || current instanceof ExecutionException)
            && current.getCause() != null) {
            current = current.getCause();
        }
        return current;
    }

    /**
     * The error object of an exception.
     *
     * @param raw the exception
     * @return {@code {kind, message, detail?}}
     */
    static Map<String, Object> toError(Throwable raw) {
        Throwable e = unwrap(raw);
        Map<String, Object> detail = new LinkedHashMap<>();
        String kind;
        String message = e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage();
        if (e instanceof HostError) {
            kind = ((HostError) e).kind();
            detail.putAll(((HostError) e).detail());
        } else if (e instanceof CancellationException) {
            kind = "Canceled";
        } else if (e instanceof NexusUnauthorizedException) {
            kind = "Unauthorized";
        } else if (e instanceof NexusRemoteInvocationException) {
            NexusRemoteInvocationException r = (NexusRemoteInvocationException) e;
            kind = "RemoteInvocation";
            message = r.remoteMessage();
            if (r.remoteExceptionType() != null) {
                detail.put("type", r.remoteExceptionType());
            }
            if (r.remoteErrorCode() != null) {
                detail.put("code", (long) r.remoteErrorCode());
            }
        } else if (e instanceof NexusTransportException) {
            kind = "Transport";
            TransportError error = ((NexusTransportException) e).error();
            detail.put("reason", Names.pascal(error));
        } else if (e instanceof NexusMessageTooLargeException) {
            kind = "TooLarge";
            detail.put("size", ((NexusMessageTooLargeException) e).size());
            detail.put("limit", ((NexusMessageTooLargeException) e).limit());
        } else if (e instanceof NexusBusyException) {
            kind = "Busy";
        } else if (e instanceof MsgPackException) {
            kind = "Serialization";
        } else if (e instanceof SessionClosedException) {
            kind = "Disconnected";
            message = NOT_CONNECTED;
        } else if (e instanceof IllegalStateException && isNotConnected(e.getMessage())) {
            kind = "Disconnected";
            message = NOT_CONNECTED;
        } else if (e instanceof IndexOutOfBoundsException) {
            kind = "ArgumentOutOfRange";
        } else if (e instanceof IllegalStateException) {
            kind = "InvalidOperation";
        } else if (e instanceof IllegalArgumentException) {
            kind = HostError.BAD_REQUEST;
        } else if (e instanceof UnsupportedOperationException) {
            kind = HostError.NOT_SUPPORTED;
        } else if (e instanceof TimeoutException) {
            kind = HostError.TIMEOUT;
        } else if (e instanceof NexusException) {
            kind = "InvalidOperation";
            detail.put("type", e.getClass().getName());
        } else {
            kind = HostError.INTERNAL;
            detail.put("stack", stack(e));
        }
        Map<String, Object> error = new LinkedHashMap<>();
        error.put("kind", kind);
        error.put("message", message);
        if (!detail.isEmpty()) {
            error.put("detail", detail);
        }
        return error;
    }

    private static boolean isNotConnected(String message) {
        return "The client is not connected.".equals(message) || "The session has closed.".equals(message)
            || NOT_CONNECTED.equals(message);
    }

    /**
     * The Java exception a script {@code throw} step (or a failed forwarded call) raises for an error kind.
     *
     * @param kind the error kind
     * @param message the message
     * @return the exception
     */
    static RuntimeException nativeException(String kind, String message) {
        String text = message == null ? "" : message;
        switch (kind == null ? "" : kind) {
            case "Transport":
                return new NexusTransportException(TransportError.INTERNAL_ERROR, text, null);
            case "RemoteInvocation":
                return new NexusRemoteInvocationException(null, null, message);
            case "Unauthorized":
                return new NexusUnauthorizedException();
            case "Canceled":
                return new CancellationException(text);
            case "Disconnected":
                return new IllegalStateException(NOT_CONNECTED);
            case "Serialization":
                return new MsgPackException(text);
            case "Busy":
                return new NexusBusyException(text);
            case "TooLarge":
                return new NexusMessageTooLargeException(0, 0);
            case "ArgumentOutOfRange":
                return new IndexOutOfBoundsException(text);
            default:
                return new IllegalStateException(text);
        }
    }

    static String stack(Throwable e) {
        StringWriter writer = new StringWriter();
        e.printStackTrace(new PrintWriter(writer));
        return writer.toString();
    }
}

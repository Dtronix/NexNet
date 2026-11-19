package com.dtronix.nexnet.interop.host;

import java.util.LinkedHashMap;
import java.util.Map;

/** A failure with an explicit error kind of the control protocol (§8). */
final class HostError extends RuntimeException {
    private static final long serialVersionUID = 1L;

    static final String TIMEOUT = "Timeout";
    static final String NOT_SUPPORTED = "NotSupported";
    static final String BAD_REQUEST = "BadRequest";
    static final String UNKNOWN_HANDLE = "UnknownHandle";
    static final String INTERNAL = "Internal";
    static final String CANCELED = "Canceled";
    static final String INVALID_OPERATION = "InvalidOperation";

    private final String kind;
    private final transient Map<String, Object> detail;

    HostError(String kind, String message) {
        this(kind, message, new LinkedHashMap<>());
    }

    HostError(String kind, String message, Map<String, Object> detail) {
        super(message);
        this.kind = kind;
        this.detail = detail;
    }

    String kind() {
        return kind;
    }

    Map<String, Object> detail() {
        return detail;
    }

    static HostError badRequest(String message) {
        return new HostError(BAD_REQUEST, message);
    }

    static HostError notSupported(String message) {
        return new HostError(NOT_SUPPORTED, message);
    }

    static HostError unknownHandle(String handle) {
        return new HostError(UNKNOWN_HANDLE, "Unknown or released handle '" + handle + "'.");
    }
}

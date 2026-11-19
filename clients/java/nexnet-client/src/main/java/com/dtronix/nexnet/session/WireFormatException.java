package com.dtronix.nexnet.session;

/** A protocol message body that does not decode. */
final class WireFormatException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    WireFormatException(String message) {
        super(message);
    }
}

package com.dtronix.nexnet.serialization;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Golden vectors ({@code tests/golden}, format v1) for the tests that replay them through the runtime's real session,
 * invocation, pipe and collection code rather than through the codec (K26). The vectors and the typed-JSON helpers of
 * {@link GoldenVectorTests} and {@link GoldenCodecs}, made reachable from the other test packages.
 */
public final class GoldenFiles {
    /** The message types the session replays (greetings) and encodes (control messages, {@code ControlSender}). */
    public static final Set<String> SESSION_TYPES = set("ClientGreeting", "ClientGreetingReconnection",
        "ServerGreeting", "InvocationCancellation", "InvocationCredit", "MessageAbort", "preamble", "frame");

    /** The message types the invocation runtime replays. */
    public static final Set<String> INVOCATION_TYPES = set("Invocation", "InvocationResult",
        "InvocationCancellation", "InvocationCredit", "MessageStart", "MessageAbort", "RemoteErrorDetail");

    /** The message types the pipe runtime replays. */
    public static final Set<String> PIPE_TYPES = set("DuplexPipeUpdateState");

    /** The message types the collection runtime replays. */
    public static final Set<String> COLLECTION_TYPES = set("CollectionListMessage");

    /** The files whose every vector replays through the real code paths. */
    public static final List<String> REAL_PATH_FILES = Collections.unmodifiableList(
        Arrays.asList("messages", "frames", "collections"));

    private GoldenFiles() {
    }

    private static Set<String> set(String... values) {
        return Collections.unmodifiableSet(new HashSet<>(Arrays.asList(values)));
    }

    /** One vector line. */
    public static final class Vector {
        /** The file, without {@code .jsonl}. */
        public final String file;
        /** The unique id. */
        public final String id;
        /** The type expression, {@code frame} or {@code preamble}. */
        public final String type;
        /** {@code both}, {@code decode} or {@code reject}. */
        public final String mode;
        /** The error kind of a reject vector, or null. */
        public final String error;
        /** The typed-JSON value: null, Boolean, BigInteger, BigDecimal, String, List or Map. */
        public final Object value;
        private final byte[] bytes;

        Vector(GoldenVectorTests.Vector v) {
            this.file = v.file;
            this.id = v.id;
            this.type = v.type;
            this.mode = v.mode;
            this.error = v.error;
            this.value = v.value;
            this.bytes = v.bytes;
        }

        /** The vector's bytes (a copy). */
        public byte[] bytes() {
            return bytes.clone();
        }

        /** The value as a list. */
        public List<Object> list() {
            return GoldenFiles.list(value);
        }

        public boolean isReject() {
            return mode.equals("reject");
        }

        @Override
        public String toString() {
            return file + ": " + id;
        }
    }

    /** Every vector of every file, in file then line order. */
    public static List<Vector> all() {
        List<Vector> vectors = new ArrayList<>();
        for (GoldenVectorTests.Vector v : GoldenVectorTests.load()) {
            vectors.add(new Vector(v));
        }
        return vectors;
    }

    /**
     * The vectors of the real-path files plus the reject vectors, whose type is one of {@code types}.
     *
     * @param types the message types
     * @return the vectors
     */
    public static List<Vector> of(Set<String> types) {
        List<Vector> vectors = new ArrayList<>();
        for (Vector v : all()) {
            if ((REAL_PATH_FILES.contains(v.file) || v.file.equals("reject")) && types.contains(v.type)) {
                vectors.add(v);
            }
        }
        return vectors;
    }

    /** The vector with this id. */
    public static Vector byId(String id) {
        for (Vector v : all()) {
            if (v.id.equals(id)) {
                return v;
            }
        }
        throw new IllegalArgumentException("No golden vector " + id);
    }

    /** A typed-JSON list. */
    public static List<Object> list(Object json) {
        return GoldenCodecs.jlist(json);
    }

    /** A typed-JSON integer. */
    public static long integer(Object json) {
        return GoldenCodecs.jlong(json);
    }

    /** A typed-JSON integer in the int range. */
    public static int int32(Object json) {
        return Math.toIntExact(GoldenCodecs.jlong(json));
    }

    /** A typed-JSON byte string (base64). */
    public static byte[] bytes(Object json) {
        return GoldenCodecs.b64(json);
    }

    /** Lower-case hex. */
    public static String hex(byte[] bytes) {
        return GoldenVectorTests.hex(bytes);
    }
}

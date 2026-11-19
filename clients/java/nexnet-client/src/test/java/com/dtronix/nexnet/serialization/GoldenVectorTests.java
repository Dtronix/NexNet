package com.dtronix.nexnet.serialization;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.stream.Stream;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.TestFactory;

/**
 * Replays every golden vector of {@code tests/golden} (format v1, {@code tests/golden/README.md}) against the Java
 * codec: {@code both} vectors encode to exactly the bytes and decode to exactly the value, {@code decode} vectors
 * decode to the value (whose canonical encoding round-trips), {@code reject} vectors fail with
 * {@link MsgPackException} of the stated kind. The directory comes from the {@code nexnet.golden} system property set
 * by the Gradle build.
 */
class GoldenVectorTests {
    static final List<String> FILES = Arrays.asList(
        "primitives", "containers", "ext78", "objects", "unions", "messages", "frames", "reject");

    private static final Set<String> FIELDS = new HashSet<>(Arrays.asList(
        "id", "type", "mode", "options", "error", "note", "value", "hex"));

    /** One vector line. */
    static final class Vector {
        final String file;
        final String id;
        final String type;
        final String mode;
        final Map<String, Object> options;
        final String error;
        final Object value;
        final byte[] bytes;

        @SuppressWarnings("unchecked")
        Vector(String file, Map<String, Object> line) {
            for (String key : line.keySet()) {
                if (!FIELDS.contains(key)) {
                    throw new IllegalArgumentException("unknown field " + key + " in " + line);
                }
            }
            this.file = file;
            this.id = (String) line.get("id");
            this.type = (String) line.get("type");
            this.mode = (String) line.get("mode");
            this.options = (Map<String, Object>) line.get("options");
            this.error = (String) line.get("error");
            this.value = line.get("value");
            this.bytes = unhex((String) line.get("hex"));
        }

        @Override
        public String toString() {
            return file + ": " + id;
        }
    }

    private static final Path DIRECTORY = directory();
    private static final GoldenCodecs CODECS = new GoldenCodecs(loadSchemas());

    private static Path directory() {
        String property = System.getProperty("nexnet.golden");
        if (property != null) {
            return Paths.get(property);
        }
        for (Path dir = Paths.get("").toAbsolutePath(); dir != null; dir = dir.getParent()) {
            if (Files.exists(dir.resolve("tests/golden/README.md"))) {
                return dir.resolve("tests/golden");
            }
        }
        throw new IllegalStateException("No tests/golden: set -Dnexnet.golden.");
    }

    private static List<String> lines(String name) {
        try {
            List<String> result = new ArrayList<>();
            for (String line : Files.readAllLines(DIRECTORY.resolve(name), StandardCharsets.UTF_8)) {
                if (!line.trim().isEmpty()) {
                    result.add(line);
                }
            }
            return result;
        } catch (IOException e) {
            throw new IllegalStateException(e);
        }
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Map<String, Object>> loadSchemas() {
        Map<String, Map<String, Object>> schemas = new LinkedHashMap<>();
        for (String line : lines("schemas.jsonl")) {
            Map<String, Object> schema = (Map<String, Object>) Json.parse(line);
            schemas.put((String) schema.get("id"), schema);
        }
        return schemas;
    }

    @SuppressWarnings("unchecked")
    static List<Vector> load() {
        List<Vector> vectors = new ArrayList<>();
        for (String file : FILES) {
            for (String line : lines(file + ".jsonl")) {
                vectors.add(new Vector(file, (Map<String, Object>) Json.parse(line)));
            }
        }
        return vectors;
    }

    @TestFactory
    Stream<DynamicTest> replay() {
        return load().stream().map(v -> DynamicTest.dynamicTest(v.toString(), () -> replay(v)));
    }

    @Test
    void everyFileHasVectorsAndIdsAreUnique() {
        List<Vector> vectors = load();
        Set<String> ids = new HashSet<>();
        for (Vector v : vectors) {
            assertTrue(ids.add(v.id), "duplicate id " + v.id);
        }
        for (String file : FILES) {
            assertTrue(vectors.stream().anyMatch(v -> v.file.equals(file)), file + ".jsonl has vectors");
        }
        long rejects = vectors.stream().filter(v -> v.mode.equals("reject")).count();
        assertEquals(vectors.stream().filter(v -> v.file.equals("reject")).count(), rejects);
        assertTrue(vectors.size() > 500, "only " + vectors.size() + " vectors");
    }

    private static void replay(Vector v) {
        if (v.type.equals("preamble") || v.type.equals("frame")) {
            replayFrame(v);
            return;
        }
        GoldenCodecs.Codec codec = CODECS.of(TypeExpr.parse(v.type));
        MsgPackOptions options = options(v.options);
        switch (v.mode) {
            case "both": {
                assertEquals(hex(v.bytes), hex(encode(codec, v.value)), "encode");
                assertEquals(Json.canonical(v.value), Json.canonical(decode(codec, v.bytes, options)), "decode");
                break;
            }
            case "decode": {
                Object decoded = decode(codec, v.bytes, options);
                assertEquals(Json.canonical(v.value), Json.canonical(decoded), "decode");
                byte[] canonical = encode(codec, v.value);
                assertFalse(Arrays.equals(canonical, v.bytes), "a decode vector is not canonical");
                assertEquals(Json.canonical(v.value), Json.canonical(decode(codec, canonical, options)),
                    "the value of a decode vector is canonical");
                assertEquals(hex(canonical), hex(encode(codec, decoded)), "re-encode");
                break;
            }
            case "reject": {
                MsgPackException e = assertThrows(MsgPackException.class,
                    () -> decode(codec, v.bytes, options), "must be rejected");
                if (v.error != null) {
                    assertKind(v.error, e);
                }
                break;
            }
            default:
                fail("unknown mode " + v.mode);
        }
    }

    private static void assertKind(String expected, MsgPackException e) {
        String actual = e.kind().name().toLowerCase(Locale.ROOT);
        boolean lengthLike = (expected.equals("length") || expected.equals("truncated"))
            && (actual.equals("length") || actual.equals("truncated"));
        if (!expected.equals(actual) && !lengthLike) {
            fail("expected error kind " + expected + ", got " + actual + ": " + e.getMessage(), e);
        }
    }

    static MsgPackOptions options(Map<String, Object> json) {
        if (json == null) {
            return MsgPackOptions.UNTRUSTED;
        }
        MsgPackOptions options = "trusted".equals(json.get("security")) ? MsgPackOptions.TRUSTED
            : MsgPackOptions.UNTRUSTED;
        if (json.containsKey("maxDepth")) {
            options = options.withMaxDepth(((BigInteger) json.get("maxDepth")).intValue());
        }
        if (json.containsKey("strictUtf8")) {
            options = options.withStrictUtf8((Boolean) json.get("strictUtf8"));
        }
        if (json.containsKey("maxElements")) {
            options = options.withMaxElements(((BigInteger) json.get("maxElements")).intValue());
        }
        return options;
    }

    static byte[] encode(GoldenCodecs.Codec codec, Object json) {
        return Formatters.serialize(codec.formatter(), codec.fromJsonOrNull(json));
    }

    static Object decode(GoldenCodecs.Codec codec, byte[] bytes, MsgPackOptions options) {
        return codec.toJsonOrNull(Formatters.deserialize(codec.formatter(), bytes, options));
    }

    // ------------------------------------------------------------------ Frames (README "Frames")

    private static void replayFrame(Vector v) {
        assertEquals("both", v.mode);
        assertEquals(hex(v.bytes), hex(encodeFrame(v.type, v.value)), "encode");
        assertEquals(Json.canonical(v.value), Json.canonical(decodeFrame(v.type, v.bytes)), "decode");
    }

    private static byte[] encodeFrame(String type, Object json) {
        List<Object> value = GoldenCodecs.jlist(json);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        if (type.equals("preamble")) {
            out.write(new byte[] {0x4e, 0x6e, 0x50, 0x14, 0, 0, 0}, 0, 7);
            out.write((int) GoldenCodecs.jlong(value.get(0)));
            return out.toByteArray();
        }
        int frameType = (int) GoldenCodecs.jlong(value.get(0));
        out.write(frameType);
        if (frameType == 1 || (frameType >= 20 && frameType <= 38)) {
            return out.toByteArray();
        }
        byte[] body;
        byte[] post;
        switch (frameType) {
            case 50:
                post = new byte[] {(byte) GoldenCodecs.jlong(value.get(1)), (byte) GoldenCodecs.jlong(value.get(2))};
                body = GoldenCodecs.b64(value.get(3));
                break;
            case 51: {
                post = new byte[] {(byte) GoldenCodecs.jlong(value.get(1)), (byte) GoldenCodecs.jlong(value.get(2))};
                long credit = GoldenCodecs.jlong(value.get(3));
                body = new byte[] {(byte) credit, (byte) (credit >>> 8), (byte) (credit >>> 16), (byte) (credit >>> 24)};
                break;
            }
            case 114: {
                long id = GoldenCodecs.jlong(value.get(1));
                post = new byte[] {(byte) id, (byte) (id >>> 8), (byte) GoldenCodecs.jlong(value.get(2))};
                body = GoldenCodecs.b64(value.get(3));
                break;
            }
            default: {
                Map<String, Object> schema = CODECS.messageSchema(frameType);
                assertNotNull(schema, "no message for frame type " + frameType);
                post = new byte[0];
                body = encode(CODECS.of(TypeExpr.parse((String) schema.get("id"))), value.get(1));
                break;
            }
        }
        out.write(body.length & 0xFF);
        out.write(body.length >>> 8);
        out.write(post, 0, post.length);
        out.write(body, 0, body.length);
        return out.toByteArray();
    }

    private static Object decodeFrame(String type, byte[] bytes) {
        if (type.equals("preamble")) {
            assertEquals(8, bytes.length);
            assertArrayEquals(new byte[] {0x4e, 0x6e, 0x50, 0x14, 0, 0, 0}, Arrays.copyOf(bytes, 7));
            return Arrays.<Object>asList(BigInteger.valueOf(bytes[7] & 0xFF));
        }
        int frameType = bytes[0] & 0xFF;
        if (frameType == 1 || (frameType >= 20 && frameType <= 38)) {
            assertEquals(1, bytes.length);
            return Arrays.<Object>asList(BigInteger.valueOf(frameType));
        }
        int length = (bytes[1] & 0xFF) | ((bytes[2] & 0xFF) << 8);
        int postLength = frameType == 50 || frameType == 51 ? 2 : frameType == 114 ? 3 : 0;
        assertEquals(3 + postLength + length, bytes.length, "frame length");
        byte[] body = Arrays.copyOfRange(bytes, 3 + postLength, bytes.length);
        BigInteger t = BigInteger.valueOf(frameType);
        switch (frameType) {
            case 50:
                return Arrays.<Object>asList(t, BigInteger.valueOf(bytes[3] & 0xFF), BigInteger.valueOf(bytes[4] & 0xFF),
                    GoldenCodecs.b64(body));
            case 51: {
                assertEquals(4, body.length);
                long credit = (body[0] & 0xFFL) | ((body[1] & 0xFFL) << 8) | ((body[2] & 0xFFL) << 16)
                    | ((body[3] & 0xFFL) << 24);
                return Arrays.<Object>asList(t, BigInteger.valueOf(bytes[3] & 0xFF), BigInteger.valueOf(bytes[4] & 0xFF),
                    BigInteger.valueOf(credit));
            }
            case 114:
                return Arrays.<Object>asList(t, BigInteger.valueOf((bytes[3] & 0xFF) | ((bytes[4] & 0xFF) << 8)),
                    BigInteger.valueOf(bytes[5] & 0xFF), GoldenCodecs.b64(body));
            default: {
                Map<String, Object> schema = CODECS.messageSchema(frameType);
                assertNotNull(schema, "no message for frame type " + frameType);
                GoldenCodecs.Codec codec = CODECS.of(TypeExpr.parse((String) schema.get("id")));
                return Arrays.asList(t, decode(codec, body, MsgPackOptions.UNTRUSTED));
            }
        }
    }

    // ------------------------------------------------------------------ Hex

    static byte[] unhex(String hex) {
        byte[] bytes = new byte[hex.length() / 2];
        for (int i = 0; i < bytes.length; i++) {
            bytes[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        }
        return bytes;
    }

    static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) {
            sb.append(Character.forDigit((b >> 4) & 0xF, 16)).append(Character.forDigit(b & 0xF, 16));
        }
        return sb.toString();
    }
}

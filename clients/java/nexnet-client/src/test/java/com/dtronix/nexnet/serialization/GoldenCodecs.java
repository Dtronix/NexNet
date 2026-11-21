package com.dtronix.nexnet.serialization;

import com.dtronix.nexnet.NexusDateOnly;
import com.dtronix.nexnet.NexusDateTime;
import com.dtronix.nexnet.NexusDateTimeOffset;
import com.dtronix.nexnet.NexusDecimal;
import com.dtronix.nexnet.NexusTimeOnly;
import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.NexusVersion;
import com.dtronix.nexnet.Tuple1;
import com.dtronix.nexnet.Tuple2;
import com.dtronix.nexnet.Tuple3;
import com.dtronix.nexnet.Tuple4;
import com.dtronix.nexnet.Tuple5;
import com.dtronix.nexnet.Tuple6;
import com.dtronix.nexnet.Tuple7;
import com.dtronix.nexnet.UnsignedLongs;
import com.dtronix.nexnet.serialization.MsgPackException.Kind;
import java.math.BigInteger;
import java.util.AbstractMap;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collection;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.function.Function;
import java.util.function.Supplier;

/**
 * Maps golden type expressions to the runtime's formatters plus the typed-JSON value model (README "Typed JSON"), the
 * way generated code (K03) will use them. Built-ins and containers use the runtime's {@link Formatters}; declared types
 * of {@code schemas.jsonl} (objects, unions, enums, externals, message bodies) use schema-driven formatters written on
 * {@link MsgPackReader}/{@link MsgPackWriter}, which stand in for generated formatters.
 */
final class GoldenCodecs {
    private static final Set<String> EXT78 = new HashSet<>(Arrays.asList(
        "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "char", "half"));

    private static final long MAX_SAFE = (1L << 53) - 1;

    /** One golden type: a formatter and its value model. */
    abstract static class Codec {
        abstract Formatter<Object> formatter();

        /** Typed JSON (not null) to the Java value. */
        abstract Object fromJson(Object json);

        /** The Java value (not null) to typed JSON. */
        abstract Object toJson(Object value);

        /** The value of a member missing from an older peer's object. */
        Object defaultValue() {
            return null;
        }

        final Object fromJsonOrNull(Object json) {
            return json == null ? null : fromJson(json);
        }

        final Object toJsonOrNull(Object value) {
            return value == null ? null : toJson(value);
        }
    }

    private static final class Simple extends Codec {
        private final Formatter<Object> formatter;
        private final Function<Object, Object> from;
        private final Function<Object, Object> to;
        private final Supplier<Object> defaults;

        @SuppressWarnings("unchecked")
        Simple(Formatter<?> formatter, Function<Object, Object> from, Function<Object, Object> to,
            Supplier<Object> defaults) {
            this.formatter = (Formatter<Object>) formatter;
            this.from = from;
            this.to = to;
            this.defaults = defaults;
        }

        @Override
        Formatter<Object> formatter() {
            return formatter;
        }

        @Override
        Object fromJson(Object json) {
            return from.apply(json);
        }

        @Override
        Object toJson(Object value) {
            return to.apply(value);
        }

        @Override
        Object defaultValue() {
            return defaults.get();
        }
    }

    /** A codec referenced before it is built (recursive declared types). */
    private static final class Lazy extends Codec {
        Codec target;

        @Override
        Formatter<Object> formatter() {
            return new Formatter<Object>() {
                @Override
                public void write(MsgPackWriter writer, Object value) {
                    target.formatter().write(writer, value);
                }

                @Override
                public Object read(MsgPackReader reader) {
                    return target.formatter().read(reader);
                }
            };
        }

        @Override
        Object fromJson(Object json) {
            return target.fromJson(json);
        }

        @Override
        Object toJson(Object value) {
            return target.toJson(value);
        }

        @Override
        Object defaultValue() {
            return target.defaultValue();
        }
    }

    private final Map<String, Map<String, Object>> schemas;
    private final Map<String, Codec> cache = new HashMap<>();

    GoldenCodecs(Map<String, Map<String, Object>> schemas) {
        this.schemas = schemas;
    }

    Map<String, Object> schema(String id) {
        return schemas.get(id);
    }

    /** The message schema of a frame type, or null. */
    Map<String, Object> messageSchema(int messageType) {
        for (Map<String, Object> s : schemas.values()) {
            if ("message".equals(s.get("kind")) && s.get("messageType") != null
                && ((BigInteger) s.get("messageType")).intValue() == messageType) {
                return s;
            }
        }
        return null;
    }

    Codec of(TypeExpr type) {
        String key = type.toString();
        Codec cached = cache.get(key);
        if (cached != null) {
            return cached;
        }
        Lazy lazy = new Lazy();
        cache.put(key, lazy);
        Codec built = build(type);
        lazy.target = built;
        cache.put(key, built);
        return built;
    }

    // ------------------------------------------------------------------ JSON helpers

    static long jlong(Object json) {
        if (json instanceof BigInteger) {
            return ((BigInteger) json).longValueExact();
        }
        return Long.parseLong((String) json);
    }

    static BigInteger jbig(Object json) {
        return json instanceof BigInteger ? (BigInteger) json : new BigInteger((String) json);
    }

    static Object jint(long value) {
        if (value >= -MAX_SAFE && value <= MAX_SAFE) {
            return BigInteger.valueOf(value);
        }
        return String.valueOf(value);
    }

    static Object jint(BigInteger value) {
        if (value.abs().compareTo(BigInteger.valueOf(MAX_SAFE)) <= 0) {
            return value;
        }
        return value.toString();
    }

    static Object julong(long bits) {
        return jint(UnsignedLongs.toBigInteger(bits));
    }

    static long fromJulong(Object json) {
        return json instanceof BigInteger ? UnsignedLongs.fromBigInteger((BigInteger) json)
            : UnsignedLongs.parse((String) json);
    }

    static long bits(Object json, String prefix) {
        String s = (String) json;
        if (!s.startsWith(prefix + ":0x")) {
            throw new IllegalArgumentException("expected " + prefix + " bits: " + s);
        }
        return Long.parseUnsignedLong(s.substring(prefix.length() + 3), 16);
    }

    static String f32(float value) {
        return String.format("f32:0x%08X", Float.floatToRawIntBits(value));
    }

    static String f64(double value) {
        return String.format("f64:0x%016X", Double.doubleToRawLongBits(value));
    }

    static String f16(float value) {
        return String.format("f16:0x%04X", Float16.fromFloat(value) & 0xFFFF);
    }

    static float fromF16(Object json) {
        return Float16.toFloat((short) bits(json, "f16"));
    }

    static byte[] b64(Object json) {
        return Base64.getDecoder().decode((String) json);
    }

    static String b64(byte[] bytes) {
        return Base64.getEncoder().encodeToString(bytes);
    }

    @SuppressWarnings("unchecked")
    static List<Object> jlist(Object json) {
        return (List<Object>) json;
    }

    private static Codec simple(Formatter<?> f, Function<Object, Object> from, Function<Object, Object> to,
        Object defaultValue) {
        return new Simple(f, from, to, () -> defaultValue);
    }

    // ------------------------------------------------------------------ Mapping

    private Codec build(TypeExpr t) {
        switch (t.kind) {
            case NULLABLE: {
                Codec inner = of(t.arg(0));
                return new Simple(Formatters.nullable(inner.formatter()), inner::fromJson, inner::toJson, () -> null);
            }
            case ARRAY:
                return sequence("[]", t.arg(0));
            case TUPLE:
                return tuple(t.args);
            case PARAM:
                throw new IllegalArgumentException("unbound type parameter " + t);
            default:
                break;
        }
        if (t.args.isEmpty()) {
            Codec builtIn = builtIn(t.name);
            if (builtIn != null) {
                return builtIn;
            }
        }
        switch (t.name) {
            case "List":
            case "Memory":
            case "ReadOnlyMemory":
                return sequence(t.name, t.arg(0));
            case "ReadOnlySequence":
            case "ArraySegment":
                if (!"byte".equals(t.arg(0).toString())) {
                    throw new IllegalArgumentException("unsupported " + t);
                }
                return builtIn("byte[]");
            case "IList":
            case "IReadOnlyList":
            case "IEnumerable":
            case "ICollection":
            case "IReadOnlyCollection":
                return list(of(t.arg(0)));
            case "HashSet":
                return set(of(t.arg(0)));
            case "Queue":
            case "Stack":
                return deque(of(t.arg(0)));
            case "Dictionary":
            case "IDictionary":
            case "IReadOnlyDictionary":
                return map(of(t.arg(0)), of(t.arg(1)));
            case "KeyValuePair":
                return keyValuePair(of(t.arg(0)), of(t.arg(1)));
            case "Tuple":
            case "ValueTuple":
                return tuple(t.args);
            default:
                break;
        }
        Map<String, Object> schema = schemas.get(t.name);
        if (schema == null) {
            throw new IllegalArgumentException("no mapping for " + t);
        }
        switch ((String) schema.get("kind")) {
            case "enum":
                return of(TypeExpr.parse((String) schema.get("underlying")));
            case "external":
                return raw();
            case "object":
                return object(schema, t.args);
            case "union":
                return union(schema, t.args);
            case "message":
                return message(schema);
            default:
                throw new IllegalArgumentException("unknown schema kind " + schema.get("kind"));
        }
    }

    private Codec builtIn(String name) {
        switch (name) {
            case "bool":
                return simple(Formatters.BOOLEAN, j -> j, v -> v, false);
            case "sbyte":
                return simple(Formatters.SBYTE, j -> (byte) jlong(j), v -> jint((Byte) v), (byte) 0);
            case "byte":
                return simple(Formatters.BYTE, j -> (int) jlong(j), v -> jint((Integer) v), 0);
            case "short":
                return simple(Formatters.SHORT, j -> (short) jlong(j), v -> jint((Short) v), (short) 0);
            case "ushort":
                return simple(Formatters.USHORT, j -> (int) jlong(j), v -> jint((Integer) v), 0);
            case "int":
                return simple(Formatters.INT, j -> Math.toIntExact(jlong(j)), v -> jint((Integer) v), 0);
            case "uint":
                return simple(Formatters.UINT, GoldenCodecs::jlong, v -> jint((Long) v), 0L);
            case "long":
                return simple(Formatters.LONG, GoldenCodecs::jlong, v -> jint((Long) v), 0L);
            case "ulong":
                return simple(Formatters.ULONG, GoldenCodecs::fromJulong, v -> julong((Long) v), 0L);
            case "float":
                return simple(Formatters.FLOAT, j -> Float.intBitsToFloat((int) bits(j, "f32")), v -> f32((Float) v),
                    0f);
            case "double":
                return simple(Formatters.DOUBLE, j -> Double.longBitsToDouble(bits(j, "f64")), v -> f64((Double) v),
                    0d);
            case "half":
                return simple(Formatters.HALF, GoldenCodecs::fromF16, v -> f16((Float) v), 0f);
            case "char":
                return simple(Formatters.CHAR, j -> (char) jlong(j), v -> jint((Character) v), (char) 0);
            case "decimal":
                return simple(Formatters.NEXUS_DECIMAL, j -> {
                    List<Object> l = jlist(j);
                    return NexusDecimal.of((int) jlong(l.get(0)), (int) jlong(l.get(1)), (int) jlong(l.get(2)),
                        (int) jlong(l.get(3)));
                }, v -> {
                    NexusDecimal d = (NexusDecimal) v;
                    return Arrays.<Object>asList(jint(d.lo()), jint(d.mid()), jint(d.hi()), jint(d.flags()));
                }, NexusDecimal.of(0, 0, 0, 0));
            case "string":
            case "Uri":
                return simple(Formatters.STRING, j -> j, v -> v, null);
            case "Version":
                return simple(Formatters.VERSION, j -> NexusVersion.parse((String) j), Object::toString, null);
            case "byte[]":
                return simple(Formatters.BINARY, GoldenCodecs::b64, v -> b64((byte[]) v), null);
            case "raw":
                return raw();
            case "DateTime":
                return simple(Formatters.DATE_TIME, j -> NexusDateTime.fromBinary(jlong(j)),
                    v -> jint(((NexusDateTime) v).toBinary()), NexusDateTime.fromBinary(0));
            case "DateTimeOffset":
                return simple(Formatters.DATE_TIME_OFFSET, j -> {
                    List<Object> l = jlist(j);
                    return NexusDateTimeOffset.ofUtc(jlong(l.get(0)), (short) jlong(l.get(1)));
                }, v -> {
                    NexusDateTimeOffset d = (NexusDateTimeOffset) v;
                    return Arrays.<Object>asList(jint(d.utcTicks()), jint(d.offsetMinutes()));
                }, NexusDateTimeOffset.ofUtc(0, (short) 0));
            case "TimeSpan":
                return simple(Formatters.TIME_SPAN, j -> NexusTimeSpan.of(jlong(j)),
                    v -> jint(((NexusTimeSpan) v).ticks()), NexusTimeSpan.of(0));
            case "TimeOnly":
                return simple(Formatters.TIME_ONLY, j -> NexusTimeOnly.of(jlong(j)),
                    v -> jint(((NexusTimeOnly) v).ticks()), NexusTimeOnly.of(0));
            case "DateOnly":
                return simple(Formatters.DATE_ONLY, j -> NexusDateOnly.of((int) jlong(j)),
                    v -> jint(((NexusDateOnly) v).dayNumber()), NexusDateOnly.of(0));
            case "Guid":
                return simple(Formatters.GUID, j -> UUID.fromString((String) j), Object::toString, new UUID(0, 0));
            case "BigInteger":
                return simple(Formatters.BIG_INTEGER, j -> new BigInteger((String) j), Object::toString,
                    BigInteger.ZERO);
            default:
                return null;
        }
    }

    private static Codec raw() {
        Formatter<byte[]> f = new Formatter<byte[]>() {
            @Override
            public void write(MsgPackWriter writer, byte[] value) {
                if (value == null) {
                    writer.writeNil(); // nil is null everywhere in the value model
                } else {
                    writer.writeRaw(value);
                }
            }

            @Override
            public byte[] read(MsgPackReader reader) {
                return reader.tryReadNil() ? null : reader.readRawValue();
            }
        };
        return simple(f, GoldenCodecs::b64, v -> b64((byte[]) v), null);
    }

    private Codec sequence(String container, TypeExpr element) {
        String name = element.args.isEmpty() && element.kind == TypeExpr.Kind.NAMED ? element.name : "";
        boolean array = !"List".equals(container);
        if (EXT78.contains(name)) {
            return ext78(name);
        }
        if ("byte".equals(name) && array) {
            return builtIn("byte[]");
        }
        return list(of(element));
    }

    private static Codec ext78(String element) {
        switch (element) {
            case "sbyte":
                return simple(Formatters.SBYTE_ARRAY, j -> {
                    List<Object> l = jlist(j);
                    byte[] a = new byte[l.size()];
                    for (int i = 0; i < a.length; i++) {
                        a[i] = (byte) jlong(l.get(i));
                    }
                    return a;
                }, v -> {
                    List<Object> l = new ArrayList<>();
                    for (byte b : (byte[]) v) {
                        l.add(jint(b));
                    }
                    return l;
                }, null);
            case "short":
            case "half":
                boolean half = "half".equals(element);
                if (half) {
                    return simple(Formatters.HALF_ARRAY, j -> {
                        List<Object> l = jlist(j);
                        float[] a = new float[l.size()];
                        for (int i = 0; i < a.length; i++) {
                            a[i] = fromF16(l.get(i));
                        }
                        return a;
                    }, v -> {
                        List<Object> l = new ArrayList<>();
                        for (float f : (float[]) v) {
                            l.add(f16(f));
                        }
                        return l;
                    }, null);
                }
                return simple(Formatters.SHORT_ARRAY, j -> {
                    List<Object> l = jlist(j);
                    short[] a = new short[l.size()];
                    for (int i = 0; i < a.length; i++) {
                        a[i] = (short) jlong(l.get(i));
                    }
                    return a;
                }, v -> {
                    List<Object> l = new ArrayList<>();
                    for (short s : (short[]) v) {
                        l.add(jint(s));
                    }
                    return l;
                }, null);
            case "ushort":
            case "char":
                return simple("char".equals(element) ? Formatters.CHAR_ARRAY : Formatters.USHORT_ARRAY, j -> {
                    List<Object> l = jlist(j);
                    char[] a = new char[l.size()];
                    for (int i = 0; i < a.length; i++) {
                        a[i] = (char) jlong(l.get(i));
                    }
                    return a;
                }, v -> {
                    List<Object> l = new ArrayList<>();
                    for (char c : (char[]) v) {
                        l.add(jint(c));
                    }
                    return l;
                }, null);
            case "int":
            case "uint":
                boolean unsigned = "uint".equals(element);
                return simple(unsigned ? Formatters.UINT_ARRAY : Formatters.INT_ARRAY, j -> {
                    List<Object> l = jlist(j);
                    int[] a = new int[l.size()];
                    for (int i = 0; i < a.length; i++) {
                        a[i] = (int) jlong(l.get(i));
                    }
                    return a;
                }, v -> {
                    List<Object> l = new ArrayList<>();
                    for (int i : (int[]) v) {
                        l.add(jint(unsigned ? i & 0xFFFF_FFFFL : i));
                    }
                    return l;
                }, null);
            case "long":
            case "ulong":
                boolean unsignedLong = "ulong".equals(element);
                return simple(unsignedLong ? Formatters.ULONG_ARRAY : Formatters.LONG_ARRAY, j -> {
                    List<Object> l = jlist(j);
                    long[] a = new long[l.size()];
                    for (int i = 0; i < a.length; i++) {
                        a[i] = unsignedLong ? fromJulong(l.get(i)) : jlong(l.get(i));
                    }
                    return a;
                }, v -> {
                    List<Object> l = new ArrayList<>();
                    for (long x : (long[]) v) {
                        l.add(unsignedLong ? julong(x) : jint(x));
                    }
                    return l;
                }, null);
            case "float":
                return simple(Formatters.FLOAT_ARRAY, j -> {
                    List<Object> l = jlist(j);
                    float[] a = new float[l.size()];
                    for (int i = 0; i < a.length; i++) {
                        a[i] = Float.intBitsToFloat((int) bits(l.get(i), "f32"));
                    }
                    return a;
                }, v -> {
                    List<Object> l = new ArrayList<>();
                    for (float f : (float[]) v) {
                        l.add(f32(f));
                    }
                    return l;
                }, null);
            case "double":
                return simple(Formatters.DOUBLE_ARRAY, j -> {
                    List<Object> l = jlist(j);
                    double[] a = new double[l.size()];
                    for (int i = 0; i < a.length; i++) {
                        a[i] = Double.longBitsToDouble(bits(l.get(i), "f64"));
                    }
                    return a;
                }, v -> {
                    List<Object> l = new ArrayList<>();
                    for (double d : (double[]) v) {
                        l.add(f64(d));
                    }
                    return l;
                }, null);
            default:
                throw new IllegalArgumentException("not an ext 78 element: " + element);
        }
    }

    private static List<Object> elementsFromJson(Codec element, Object json) {
        List<Object> result = new ArrayList<>();
        for (Object item : jlist(json)) {
            result.add(element.fromJsonOrNull(item));
        }
        return result;
    }

    private static List<Object> elementsToJson(Codec element, Collection<?> values) {
        List<Object> result = new ArrayList<>();
        for (Object item : values) {
            result.add(element.toJsonOrNull(item));
        }
        return result;
    }

    private static Codec list(Codec element) {
        return simple(Formatters.list(element.formatter()), j -> elementsFromJson(element, j),
            v -> elementsToJson(element, (List<?>) v), null);
    }

    private static Codec set(Codec element) {
        return simple(Formatters.set(element.formatter()), j -> new LinkedHashSet<>(elementsFromJson(element, j)),
            v -> elementsToJson(element, (Set<?>) v), null);
    }

    private static Codec deque(Codec element) {
        return simple(Formatters.queue(element.formatter()), j -> new ArrayDeque<>(elementsFromJson(element, j)),
            v -> elementsToJson(element, (ArrayDeque<?>) v), null);
    }

    private static Codec map(Codec key, Codec value) {
        return simple(Formatters.map(key.formatter(), value.formatter()), j -> {
            Map<Object, Object> m = new LinkedHashMap<>();
            for (Object pair : jlist(j)) {
                List<Object> p = jlist(pair);
                m.put(key.fromJsonOrNull(p.get(0)), value.fromJsonOrNull(p.get(1)));
            }
            return m;
        }, v -> {
            List<Object> l = new ArrayList<>();
            for (Map.Entry<?, ?> e : ((Map<?, ?>) v).entrySet()) {
                l.add(Arrays.asList(key.toJsonOrNull(e.getKey()), value.toJsonOrNull(e.getValue())));
            }
            return l;
        }, null);
    }

    private static Codec keyValuePair(Codec key, Codec value) {
        return simple(Formatters.keyValuePair(key.formatter(), value.formatter()), j -> {
            List<Object> p = jlist(j);
            return new AbstractMap.SimpleImmutableEntry<>(key.fromJsonOrNull(p.get(0)), value.fromJsonOrNull(p.get(1)));
        }, v -> {
            Map.Entry<?, ?> e = (Map.Entry<?, ?>) v;
            return Arrays.asList(key.toJsonOrNull(e.getKey()), value.toJsonOrNull(e.getValue()));
        }, null);
    }

    private Codec tuple(List<TypeExpr> items) {
        int n = items.size();
        Codec[] c = new Codec[n];
        List<Formatter<Object>> f = new ArrayList<>();
        for (int i = 0; i < n; i++) {
            c[i] = of(items.get(i));
            f.add(c[i].formatter());
        }
        Formatter<?> formatter;
        switch (n) {
            case 1:
                formatter = Formatters.tuple(f.get(0));
                break;
            case 2:
                formatter = Formatters.tuple(f.get(0), f.get(1));
                break;
            case 3:
                formatter = Formatters.tuple(f.get(0), f.get(1), f.get(2));
                break;
            case 4:
                formatter = Formatters.tuple(f.get(0), f.get(1), f.get(2), f.get(3));
                break;
            case 5:
                formatter = Formatters.tuple(f.get(0), f.get(1), f.get(2), f.get(3), f.get(4));
                break;
            case 6:
                formatter = Formatters.tuple(f.get(0), f.get(1), f.get(2), f.get(3), f.get(4), f.get(5));
                break;
            case 7:
                formatter = Formatters.tuple(f.get(0), f.get(1), f.get(2), f.get(3), f.get(4), f.get(5), f.get(6));
                break;
            default:
                throw new IllegalArgumentException("tuple of " + n);
        }
        return simple(formatter, j -> {
            List<Object> l = jlist(j);
            Object[] v = new Object[n];
            for (int i = 0; i < n; i++) {
                v[i] = c[i].fromJsonOrNull(l.get(i));
            }
            switch (n) {
                case 1:
                    return Tuple1.of(v[0]);
                case 2:
                    return Tuple2.of(v[0], v[1]);
                case 3:
                    return Tuple3.of(v[0], v[1], v[2]);
                case 4:
                    return Tuple4.of(v[0], v[1], v[2], v[3]);
                case 5:
                    return Tuple5.of(v[0], v[1], v[2], v[3], v[4]);
                case 6:
                    return Tuple6.of(v[0], v[1], v[2], v[3], v[4], v[5]);
                default:
                    return Tuple7.of(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
            }
        }, v -> {
            Object[] items2 = tupleItems(v);
            List<Object> l = new ArrayList<>();
            for (int i = 0; i < n; i++) {
                l.add(c[i].toJsonOrNull(items2[i]));
            }
            return l;
        }, null);
    }

    private static Object[] tupleItems(Object t) {
        if (t instanceof Tuple1) {
            return new Object[] {((Tuple1<?>) t).item1()};
        }
        if (t instanceof Tuple2) {
            Tuple2<?, ?> x = (Tuple2<?, ?>) t;
            return new Object[] {x.item1(), x.item2()};
        }
        if (t instanceof Tuple3) {
            Tuple3<?, ?, ?> x = (Tuple3<?, ?, ?>) t;
            return new Object[] {x.item1(), x.item2(), x.item3()};
        }
        if (t instanceof Tuple4) {
            Tuple4<?, ?, ?, ?> x = (Tuple4<?, ?, ?, ?>) t;
            return new Object[] {x.item1(), x.item2(), x.item3(), x.item4()};
        }
        if (t instanceof Tuple5) {
            Tuple5<?, ?, ?, ?, ?> x = (Tuple5<?, ?, ?, ?, ?>) t;
            return new Object[] {x.item1(), x.item2(), x.item3(), x.item4(), x.item5()};
        }
        if (t instanceof Tuple6) {
            Tuple6<?, ?, ?, ?, ?, ?> x = (Tuple6<?, ?, ?, ?, ?, ?>) t;
            return new Object[] {x.item1(), x.item2(), x.item3(), x.item4(), x.item5(), x.item6()};
        }
        Tuple7<?, ?, ?, ?, ?, ?, ?> x = (Tuple7<?, ?, ?, ?, ?, ?, ?>) t;
        return new Object[] {x.item1(), x.item2(), x.item3(), x.item4(), x.item5(), x.item6(), x.item7()};
    }

    // ------------------------------------------------------------------ Declared types

    /** A {@code [NexusObject]}: an array by key; the Java value is an {@code Object[]} indexed by key. */
    private Codec object(Map<String, Object> schema, List<TypeExpr> bindings) {
        String id = (String) schema.get("id");
        int length = ((BigInteger) schema.get("length")).intValue();
        // A C# struct ("struct": true in schemas.jsonl): nil is not a value of it, missing members are at their default.
        boolean struct = Boolean.TRUE.equals(schema.get("struct"));
        Codec[] members = new Codec[length];
        for (Object m : jlist(schema.get("members"))) {
            List<Object> member = jlist(m);
            int key = ((BigInteger) member.get(0)).intValue();
            members[key] = of(TypeExpr.parse((String) member.get(2)).bind(bindings));
        }
        Supplier<Object> defaults = () -> {
            Object[] a = new Object[length];
            for (int k = 0; k < length; k++) {
                a[k] = members[k] == null ? null : members[k].defaultValue();
            }
            return a;
        };
        Formatter<Object> formatter = new Formatter<Object>() {
            @Override
            public void write(MsgPackWriter writer, Object value) {
                if (value == null) {
                    if (struct) {
                        throw new MsgPackException(Kind.TYPE, id + " is a struct.");
                    }
                    writer.writeNil();
                    return;
                }
                Object[] a = (Object[]) value;
                writer.writeArrayHeader(length);
                for (int k = 0; k < length; k++) {
                    if (members[k] == null) {
                        writer.writeNil();
                    } else {
                        members[k].formatter().write(writer, a[k]);
                    }
                }
            }

            @Override
            public Object read(MsgPackReader reader) {
                if (reader.isNil()) {
                    if (struct) {
                        throw new MsgPackException(Kind.TYPE, "nil is not a value of the struct " + id + ".");
                    }
                    reader.readNil();
                    return null;
                }
                int count = reader.readArrayHeader();
                reader.enter();
                Object[] a = (Object[]) defaults.get();
                for (int k = 0; k < count; k++) {
                    if (k < length && members[k] != null) {
                        a[k] = members[k].formatter().read(reader);
                    } else {
                        reader.skip();
                    }
                }
                reader.exit();
                return a;
            }
        };
        return new Simple(formatter, j -> {
            List<Object> l = jlist(j);
            Object[] a = new Object[length];
            for (int k = 0; k < length; k++) {
                a[k] = members[k] == null ? null : members[k].fromJsonOrNull(l.get(k));
            }
            return a;
        }, v -> {
            Object[] a = (Object[]) v;
            List<Object> l = new ArrayList<>();
            for (int k = 0; k < length; k++) {
                l.add(members[k] == null ? null : members[k].toJsonOrNull(a[k]));
            }
            return l;
        }, struct ? defaults : () -> null);
    }

    /** A union: {@code [tag, value]}; the Java value is {@code Object[] {tag, value}}. */
    private Codec union(Map<String, Object> schema, List<TypeExpr> bindings) {
        Map<Integer, Codec> cases = new HashMap<>();
        for (Object c : jlist(schema.get("cases"))) {
            List<Object> pair = jlist(c);
            TypeExpr caseType = TypeExpr.named((String) pair.get(1), new ArrayList<>(bindings));
            cases.put(((BigInteger) pair.get(0)).intValue(), of(caseType));
        }
        Formatter<Object> formatter = new Formatter<Object>() {
            @Override
            public void write(MsgPackWriter writer, Object value) {
                if (value == null) {
                    writer.writeNil();
                    return;
                }
                Object[] a = (Object[]) value;
                int tag = (Integer) a[0];
                writer.writeArrayHeader(2);
                writer.writeInt(tag);
                cases.get(tag).formatter().write(writer, a[1]);
            }

            @Override
            public Object read(MsgPackReader reader) {
                if (reader.tryReadNil()) {
                    return null;
                }
                reader.readArrayHeader(2);
                reader.enter();
                int tag = reader.readUInt16();
                Codec c = cases.get(tag);
                if (c == null) {
                    throw new MsgPackException(Kind.VALUE, "Unknown union tag " + tag + ".");
                }
                Object value = c.formatter().read(reader);
                reader.exit();
                return new Object[] {tag, value};
            }
        };
        return new Simple(formatter, j -> {
            List<Object> l = jlist(j);
            int tag = ((BigInteger) l.get(0)).intValue();
            return new Object[] {tag, cases.get(tag).fromJsonOrNull(l.get(1))};
        }, v -> {
            Object[] a = (Object[]) v;
            int tag = (Integer) a[0];
            return Arrays.asList(BigInteger.valueOf(tag), cases.get(tag).toJsonOrNull(a[1]));
        }, () -> null);
    }

    /** A protocol message body: an array of {@code minCount..maxCount} fields; the Java value is a list. */
    private Codec message(Map<String, Object> schema) {
        String id = (String) schema.get("id");
        int min = ((BigInteger) schema.get("minCount")).intValue();
        Object maxJson = schema.get("maxCount");
        int max = maxJson == null ? Integer.MAX_VALUE : ((BigInteger) maxJson).intValue();
        List<Codec> fields = new ArrayList<>();
        List<Boolean> rawFields = new ArrayList<>();
        List<Boolean> binFields = new ArrayList<>();
        for (Object f : jlist(schema.get("fields"))) {
            String type = (String) jlist(f).get(1);
            fields.add(of(TypeExpr.parse(type)));
            rawFields.add("raw".equals(type));
            binFields.add("byte[]".equals(type));
        }
        Formatter<Object> formatter = new Formatter<Object>() {
            @Override
            public void write(MsgPackWriter writer, Object value) {
                List<?> values = (List<?>) value;
                writer.writeArrayHeader(values.size());
                for (int i = 0; i < values.size(); i++) {
                    fields.get(i).formatter().write(writer, values.get(i));
                }
            }

            @Override
            public Object read(MsgPackReader reader) {
                // As the C# messages: the fields present are read first, then the count is checked.
                int count = reader.readArrayHeader();
                List<Object> values = new ArrayList<>();
                for (int i = 0; i < count; i++) {
                    if (i < fields.size()) {
                        Object value = fields.get(i).formatter().read(reader);
                        values.add(value == null && binFields.get(i) ? new byte[0] : value); // a nil token is none
                    } else {
                        reader.skip();
                    }
                }
                if (count < min || count > max) {
                    throw new MsgPackException(Kind.COUNT, id + " has " + count + " fields.");
                }
                for (int i = count; i < fields.size() && !rawFields.get(i); i++) {
                    values.add(fields.get(i).defaultValue()); // missing trailing fields read as 0
                }
                if ("RemoteErrorDetail".equals(id)) {
                    String type = (String) values.get(1);
                    String message = (String) values.get(2);
                    if ((type != null && type.length() > 256) || (message != null && message.length() > 2048)) {
                        throw new MsgPackException(Kind.VALUE, "RemoteErrorDetail text too long.");
                    }
                }
                return values;
            }
        };
        return new Simple(formatter, j -> {
            List<Object> l = jlist(j);
            List<Object> values = new ArrayList<>();
            for (int i = 0; i < l.size(); i++) {
                values.add(fields.get(i).fromJsonOrNull(l.get(i)));
            }
            return values;
        }, v -> {
            List<?> values = (List<?>) v;
            List<Object> l = new ArrayList<>();
            for (int i = 0; i < values.size(); i++) {
                l.add(fields.get(i).toJsonOrNull(values.get(i)));
            }
            return l;
        }, () -> null);
    }
}

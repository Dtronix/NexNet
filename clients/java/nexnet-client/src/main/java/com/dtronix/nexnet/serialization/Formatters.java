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
import com.dtronix.nexnet.serialization.MsgPackException.Kind;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.AbstractMap;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import org.jspecify.annotations.Nullable;

/**
 * The built-in formatters: one per C# built-in type of the NexNet wire format (protocol specification section 10),
 * with the Java types of the generator's type mapping, and factories for containers. Every formatter is stateless and
 * thread-safe. Generated code (K03) refers to these constants and factories.
 *
 * <p><b>Null.</b> Formatters of C# value types ({@code int}, {@code DateTime}, {@code decimal}, ...) throw
 * {@link MsgPackException} on a null value and read nil as an error; wrap them in {@link #nullable(Formatter)} for
 * C# {@code T?}. Formatters of reference types ({@code string}, arrays, collections) write null as nil and read nil
 * as null.
 *
 * <p><b>Unsigned types.</b> C# {@code byte} and {@code ushort} map to {@code int}, {@code uint} to {@code long}: the
 * value is range-checked on write. C# {@code ulong} maps to {@code long} holding the raw bits (see
 * {@link com.dtronix.nexnet.UnsignedLongs}).
 */
public final class Formatters {
    private Formatters() {
    }

    // ------------------------------------------------------------------ Helpers

    /** A formatter of a C# value type: null is refused both ways. */
    private abstract static class StructFormatter<T> implements Formatter<T> {
        private final String name;

        StructFormatter(String name) {
            this.name = name;
        }

        @Override
        public final void write(MsgPackWriter writer, @Nullable T value) {
            if (value == null) {
                throw new MsgPackException(Kind.TYPE, name + " is a value type and cannot be null.");
            }
            writeValue(writer, value);
        }

        @Override
        public final T read(MsgPackReader reader) {
            if (reader.isNil()) {
                throw new MsgPackException(Kind.TYPE, "nil is not a value of the value type " + name + ".");
            }
            return readValue(reader);
        }

        abstract void writeValue(MsgPackWriter writer, T value);

        abstract T readValue(MsgPackReader reader);

        @Override
        public String toString() {
            return name;
        }
    }

    /** A formatter of a reference type: null is nil. */
    private abstract static class ReferenceFormatter<T> implements Formatter<T> {
        @Override
        public final void write(MsgPackWriter writer, @Nullable T value) {
            if (value == null) {
                writer.writeNil();
            } else {
                writeValue(writer, value);
            }
        }

        @Override
        public final @Nullable T read(MsgPackReader reader) {
            if (reader.tryReadNil()) {
                return null;
            }
            return readValue(reader);
        }

        abstract void writeValue(MsgPackWriter writer, T value);

        abstract T readValue(MsgPackReader reader);
    }

    private static MsgPackException outOfRange(String type, Object value) {
        return new MsgPackException(Kind.RANGE, value + " is outside the C# " + type + " range.");
    }

    private static MsgPackException invalid(String type, Throwable cause) {
        return new MsgPackException(Kind.VALUE, "Invalid " + type + " value: " + cause.getMessage(), cause);
    }

    // ------------------------------------------------------------------ Scalars

    /** C# {@code bool}. */
    public static final Formatter<Boolean> BOOLEAN = new StructFormatter<Boolean>("bool") {
        @Override
        void writeValue(MsgPackWriter writer, Boolean value) {
            writer.writeBoolean(value);
        }

        @Override
        Boolean readValue(MsgPackReader reader) {
            return reader.readBoolean();
        }
    };

    /** C# {@code sbyte} as {@code byte}. */
    public static final Formatter<Byte> SBYTE = new StructFormatter<Byte>("sbyte") {
        @Override
        void writeValue(MsgPackWriter writer, Byte value) {
            writer.writeInt(value);
        }

        @Override
        Byte readValue(MsgPackReader reader) {
            return reader.readSByte();
        }
    };

    /** C# {@code byte} as {@code int}, 0 to 255 (range-checked on write). */
    public static final Formatter<Integer> BYTE = new StructFormatter<Integer>("byte") {
        @Override
        void writeValue(MsgPackWriter writer, Integer value) {
            int v = value;
            if (v < 0 || v > 0xFF) {
                throw outOfRange("byte", value);
            }
            writer.writeInt(v);
        }

        @Override
        Integer readValue(MsgPackReader reader) {
            return reader.readUInt8();
        }
    };

    /** C# {@code short}. */
    public static final Formatter<Short> SHORT = new StructFormatter<Short>("short") {
        @Override
        void writeValue(MsgPackWriter writer, Short value) {
            writer.writeInt(value);
        }

        @Override
        Short readValue(MsgPackReader reader) {
            return reader.readShort();
        }
    };

    /** C# {@code ushort} as {@code int}, 0 to 65535 (range-checked on write). */
    public static final Formatter<Integer> USHORT = new StructFormatter<Integer>("ushort") {
        @Override
        void writeValue(MsgPackWriter writer, Integer value) {
            int v = value;
            if (v < 0 || v > 0xFFFF) {
                throw outOfRange("ushort", value);
            }
            writer.writeInt(v);
        }

        @Override
        Integer readValue(MsgPackReader reader) {
            return reader.readUInt16();
        }
    };

    /** C# {@code int}. */
    public static final Formatter<Integer> INT = new StructFormatter<Integer>("int") {
        @Override
        void writeValue(MsgPackWriter writer, Integer value) {
            writer.writeInt(value);
        }

        @Override
        Integer readValue(MsgPackReader reader) {
            return reader.readInt();
        }
    };

    /** C# {@code uint} as {@code long}, 0 to 4294967295 (range-checked on write). */
    public static final Formatter<Long> UINT = new StructFormatter<Long>("uint") {
        @Override
        void writeValue(MsgPackWriter writer, Long value) {
            long v = value;
            if (v < 0 || v > 0xFFFF_FFFFL) {
                throw outOfRange("uint", value);
            }
            writer.writeInt(v);
        }

        @Override
        Long readValue(MsgPackReader reader) {
            return reader.readUInt32();
        }
    };

    /** C# {@code long}. */
    public static final Formatter<Long> LONG = new StructFormatter<Long>("long") {
        @Override
        void writeValue(MsgPackWriter writer, Long value) {
            writer.writeInt(value);
        }

        @Override
        Long readValue(MsgPackReader reader) {
            return reader.readLong();
        }
    };

    /** C# {@code ulong} as {@code long} holding the raw bits. */
    public static final Formatter<Long> ULONG = new StructFormatter<Long>("ulong") {
        @Override
        void writeValue(MsgPackWriter writer, Long value) {
            writer.writeUInt64(value);
        }

        @Override
        Long readValue(MsgPackReader reader) {
            return reader.readUInt64();
        }
    };

    /** C# {@code float}: always float 32. */
    public static final Formatter<Float> FLOAT = new StructFormatter<Float>("float") {
        @Override
        void writeValue(MsgPackWriter writer, Float value) {
            writer.writeFloat(value);
        }

        @Override
        Float readValue(MsgPackReader reader) {
            return reader.readFloat();
        }
    };

    /** C# {@code double}: always float 64. */
    public static final Formatter<Double> DOUBLE = new StructFormatter<Double>("double") {
        @Override
        void writeValue(MsgPackWriter writer, Double value) {
            writer.writeDouble(value);
        }

        @Override
        Double readValue(MsgPackReader reader) {
            return reader.readDouble();
        }
    };

    /**
     * C# {@code Half} as {@code float}: written as a float 32 after rounding to binary16, read as the nearest binary16
     * value (see {@link Float16}).
     */
    public static final Formatter<Float> HALF = new StructFormatter<Float>("Half") {
        @Override
        void writeValue(MsgPackWriter writer, Float value) {
            writer.writeFloat(Float16.round(value));
        }

        @Override
        Float readValue(MsgPackReader reader) {
            return reader.readHalf();
        }
    };

    /** C# {@code char}: a UTF-16 code unit written as an unsigned integer. */
    public static final Formatter<Character> CHAR = new StructFormatter<Character>("char") {
        @Override
        void writeValue(MsgPackWriter writer, Character value) {
            writer.writeInt(value.charValue());
        }

        @Override
        Character readValue(MsgPackReader reader) {
            return reader.readChar();
        }
    };

    /** C# {@code string}: UTF-8 str, null is nil. */
    public static final Formatter<String> STRING = new Formatter<String>() {
        @Override
        public void write(MsgPackWriter writer, @Nullable String value) {
            writer.writeString(value);
        }

        @Override
        public @Nullable String read(MsgPackReader reader) {
            return reader.readString();
        }
    };

    /** C# {@code Uri} as its original string. */
    public static final Formatter<String> URI = STRING;

    /** C# {@code Version}: its text, {@code major.minor[.build[.revision]]}. */
    public static final Formatter<NexusVersion> VERSION = new ReferenceFormatter<NexusVersion>() {
        @Override
        void writeValue(MsgPackWriter writer, NexusVersion value) {
            writer.writeString(value.toString());
        }

        @Override
        NexusVersion readValue(MsgPackReader reader) {
            String text = reader.readString();
            try {
                return NexusVersion.parse(text == null ? "" : text);
            } catch (IllegalArgumentException e) {
                throw invalid("Version", e);
            }
        }
    };

    /** C# {@code byte[]}, {@code Memory<byte>} and the other byte containers: bin (also reads str bytes). */
    public static final Formatter<byte[]> BINARY = new ReferenceFormatter<byte[]>() {
        @Override
        void writeValue(MsgPackWriter writer, byte[] value) {
            writer.writeBinary(value, 0, value.length);
        }

        @Override
        byte[] readValue(MsgPackReader reader) {
            byte[] value = reader.readBinary();
            if (value == null) {
                throw new IllegalStateException("nil was handled");
            }
            return value;
        }
    };

    // ------------------------------------------------------------------ Dates and times

    /**
     * C# {@code DateTime}: its {@code ToBinary} int64 (bits 0-61 the ticks, bits 62-63 the kind). A Local value
     * carries UTC ticks (see {@link NexusDateTime}); kind bits 11 read as Local.
     */
    public static final Formatter<NexusDateTime> DATE_TIME = new StructFormatter<NexusDateTime>("DateTime") {
        @Override
        void writeValue(MsgPackWriter writer, NexusDateTime value) {
            if (!value.isValid()) {
                throw new MsgPackException(Kind.VALUE, "Not a .NET DateTime: " + value);
            }
            writer.writeInt(value.toBinary());
        }

        @Override
        NexusDateTime readValue(MsgPackReader reader) {
            long binary = reader.readLong();
            try {
                return NexusDateTime.fromBinary(binary);
            } catch (IllegalArgumentException e) {
                throw invalid("DateTime", e);
            }
        }
    };

    /** C# {@code DateTimeOffset}: {@code [utcTicks, offsetMinutes]}. */
    public static final Formatter<NexusDateTimeOffset> DATE_TIME_OFFSET =
        new StructFormatter<NexusDateTimeOffset>("DateTimeOffset") {
            @Override
            void writeValue(MsgPackWriter writer, NexusDateTimeOffset value) {
                if (!value.isValid()) {
                    throw new MsgPackException(Kind.VALUE, "Not a .NET DateTimeOffset: " + value);
                }
                writer.writeArrayHeader(2);
                writer.writeInt(value.utcTicks());
                writer.writeInt(value.offsetMinutes());
            }

            @Override
            NexusDateTimeOffset readValue(MsgPackReader reader) {
                reader.readArrayHeader(2);
                long utcTicks = reader.readLong();
                short offset = reader.readShort();
                try {
                    return NexusDateTimeOffset.ofUtc(utcTicks, offset);
                } catch (IllegalArgumentException e) {
                    throw invalid("DateTimeOffset", e);
                }
            }
        };

    /** C# {@code TimeSpan}: ticks. */
    public static final Formatter<NexusTimeSpan> TIME_SPAN = new StructFormatter<NexusTimeSpan>("TimeSpan") {
        @Override
        void writeValue(MsgPackWriter writer, NexusTimeSpan value) {
            writer.writeInt(value.ticks());
        }

        @Override
        NexusTimeSpan readValue(MsgPackReader reader) {
            return NexusTimeSpan.of(reader.readLong());
        }
    };

    /** C# {@code DateOnly}: the day number. */
    public static final Formatter<NexusDateOnly> DATE_ONLY = new StructFormatter<NexusDateOnly>("DateOnly") {
        @Override
        void writeValue(MsgPackWriter writer, NexusDateOnly value) {
            if (!value.isValid()) {
                throw new MsgPackException(Kind.VALUE, "Not a .NET DateOnly: " + value);
            }
            writer.writeInt(value.dayNumber());
        }

        @Override
        NexusDateOnly readValue(MsgPackReader reader) {
            NexusDateOnly value = NexusDateOnly.of(reader.readInt());
            if (!value.isValid()) {
                throw new MsgPackException(Kind.VALUE, "Not a .NET DateOnly: " + value);
            }
            return value;
        }
    };

    /** C# {@code TimeOnly}: ticks since midnight. */
    public static final Formatter<NexusTimeOnly> TIME_ONLY = new StructFormatter<NexusTimeOnly>("TimeOnly") {
        @Override
        void writeValue(MsgPackWriter writer, NexusTimeOnly value) {
            if (!value.isValid()) {
                throw new MsgPackException(Kind.VALUE, "Not a .NET TimeOnly: " + value);
            }
            writer.writeInt(value.ticks());
        }

        @Override
        NexusTimeOnly readValue(MsgPackReader reader) {
            NexusTimeOnly value = NexusTimeOnly.of(reader.readLong());
            if (!value.isValid()) {
                throw new MsgPackException(Kind.VALUE, "Not a .NET TimeOnly: " + value);
            }
            return value;
        }
    };

    // ------------------------------------------------------------------ Other built-ins

    private static int fixedBinaryHeader(MsgPackReader reader, String type) {
        int length = reader.readBinaryHeader();
        if (length < 0) {
            throw new MsgPackException(Kind.TYPE, "nil is not a value of the value type " + type + ".");
        }
        if (length != 16) {
            throw new MsgPackException(Kind.SIZE, type + " must be 16 bytes, found " + length + ".");
        }
        return reader.take(16);
    }

    /** C# {@code Guid} as {@link UUID}: bin 16 in RFC 4122 (text) byte order. */
    public static final Formatter<UUID> GUID = new StructFormatter<UUID>("Guid") {
        @Override
        void writeValue(MsgPackWriter writer, UUID value) {
            writer.writeBinaryHeader(16);
            writer.ensure(16);
            byte[] b = writer.buffer();
            int at = writer.size();
            putLongBigEndian(b, at, value.getMostSignificantBits());
            putLongBigEndian(b, at + 8, value.getLeastSignificantBits());
            writer.advance(16);
        }

        @Override
        UUID readValue(MsgPackReader reader) {
            int at = fixedBinaryHeader(reader, "Guid");
            byte[] b = reader.array();
            return new UUID(getLongBigEndian(b, at), getLongBigEndian(b, at + 8));
        }
    };

    /**
     * C# {@code decimal} in its four-integer form: bin 16, {@code lo, mid, hi, flags} as little-endian int32. Exact,
     * including the sign of zero.
     */
    public static final Formatter<NexusDecimal> NEXUS_DECIMAL = new StructFormatter<NexusDecimal>("decimal") {
        @Override
        void writeValue(MsgPackWriter writer, NexusDecimal value) {
            if (!NexusDecimal.isValidFlags(value.flags())) {
                throw new MsgPackException(Kind.VALUE, "Invalid decimal flags 0x" + Integer.toHexString(value.flags())
                    + ".");
            }
            writer.writeBinaryHeader(16);
            writer.ensure(16);
            byte[] b = writer.buffer();
            int at = writer.size();
            putIntLittleEndian(b, at, value.lo());
            putIntLittleEndian(b, at + 4, value.mid());
            putIntLittleEndian(b, at + 8, value.hi());
            putIntLittleEndian(b, at + 12, value.flags());
            writer.advance(16);
        }

        @Override
        NexusDecimal readValue(MsgPackReader reader) {
            int at = fixedBinaryHeader(reader, "decimal");
            byte[] b = reader.array();
            int flags = getIntLittleEndian(b, at + 12);
            if (!NexusDecimal.isValidFlags(flags)) {
                throw new MsgPackException(Kind.VALUE, "Invalid decimal flags 0x" + Integer.toHexString(flags) + ".");
            }
            return NexusDecimal.of(getIntLittleEndian(b, at), getIntLittleEndian(b, at + 4),
                getIntLittleEndian(b, at + 8), flags);
        }
    };

    /**
     * C# {@code decimal} as {@link BigDecimal} (the generator's mapping), through {@link NexusDecimal}. A value with
     * more than 96 bits or a scale above 28 cannot be written; a negative zero reads as zero.
     */
    public static final Formatter<BigDecimal> DECIMAL = new StructFormatter<BigDecimal>("decimal") {
        @Override
        void writeValue(MsgPackWriter writer, BigDecimal value) {
            NexusDecimal d;
            try {
                d = NexusDecimal.fromBigDecimal(value);
            } catch (ArithmeticException e) {
                throw new MsgPackException(Kind.RANGE, "The value does not fit a C# decimal: " + value, e);
            }
            NEXUS_DECIMAL.write(writer, d);
        }

        @Override
        BigDecimal readValue(MsgPackReader reader) {
            NexusDecimal d = NEXUS_DECIMAL.read(reader);
            return d.toBigDecimal();
        }
    };

    /** C# {@code BigInteger}: bin, little-endian two's complement, minimal length. */
    public static final Formatter<BigInteger> BIG_INTEGER = new StructFormatter<BigInteger>("BigInteger") {
        @Override
        void writeValue(MsgPackWriter writer, BigInteger value) {
            byte[] bigEndian = value.toByteArray();
            int n = bigEndian.length;
            writer.writeBinaryHeader(n);
            writer.ensure(n);
            byte[] b = writer.buffer();
            int at = writer.size();
            for (int i = 0; i < n; i++) {
                b[at + i] = bigEndian[n - 1 - i];
            }
            writer.advance(n);
        }

        @Override
        BigInteger readValue(MsgPackReader reader) {
            int length = reader.readBinaryHeader();
            if (length == 0) {
                return BigInteger.ZERO;
            }
            int at = reader.take(length);
            byte[] b = reader.array();
            byte[] bigEndian = new byte[length];
            for (int i = 0; i < length; i++) {
                bigEndian[i] = b[at + length - 1 - i];
            }
            return new BigInteger(bigEndian);
        }
    };

    // ------------------------------------------------------------------ Ext 78 primitive arrays

    /** C# {@code sbyte[]} (and {@code List<sbyte>}, {@code Memory<sbyte>}...) as {@code byte[]}: ext 78. */
    public static final Formatter<byte[]> SBYTE_ARRAY = new ReferenceFormatter<byte[]>() {
        @Override
        void writeValue(MsgPackWriter writer, byte[] value) {
            PrimitiveArrays.writeBytes(writer, PrimitiveArrays.SBYTE, value);
        }

        @Override
        byte[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readBytes(reader, PrimitiveArrays.SBYTE);
        }
    };

    /** C# {@code short[]}: ext 78. */
    public static final Formatter<short[]> SHORT_ARRAY = new ReferenceFormatter<short[]>() {
        @Override
        void writeValue(MsgPackWriter writer, short[] value) {
            PrimitiveArrays.writeShorts(writer, PrimitiveArrays.INT16, value);
        }

        @Override
        short[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readShorts(reader, PrimitiveArrays.INT16);
        }
    };

    /** C# {@code ushort[]} as {@code char[]} (unsigned 16-bit): ext 78. */
    public static final Formatter<char[]> USHORT_ARRAY = new ReferenceFormatter<char[]>() {
        @Override
        void writeValue(MsgPackWriter writer, char[] value) {
            PrimitiveArrays.writeChars(writer, PrimitiveArrays.UINT16, value);
        }

        @Override
        char[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readChars(reader, PrimitiveArrays.UINT16);
        }
    };

    /** C# {@code int[]}: ext 78. */
    public static final Formatter<int[]> INT_ARRAY = new ReferenceFormatter<int[]>() {
        @Override
        void writeValue(MsgPackWriter writer, int[] value) {
            PrimitiveArrays.writeInts(writer, PrimitiveArrays.INT32, value);
        }

        @Override
        int[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readInts(reader, PrimitiveArrays.INT32);
        }
    };

    /** C# {@code uint[]} as {@code int[]} holding the raw bits: ext 78. */
    public static final Formatter<int[]> UINT_ARRAY = new ReferenceFormatter<int[]>() {
        @Override
        void writeValue(MsgPackWriter writer, int[] value) {
            PrimitiveArrays.writeInts(writer, PrimitiveArrays.UINT32, value);
        }

        @Override
        int[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readInts(reader, PrimitiveArrays.UINT32);
        }
    };

    /** C# {@code long[]}: ext 78. */
    public static final Formatter<long[]> LONG_ARRAY = new ReferenceFormatter<long[]>() {
        @Override
        void writeValue(MsgPackWriter writer, long[] value) {
            PrimitiveArrays.writeLongs(writer, PrimitiveArrays.INT64, value);
        }

        @Override
        long[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readLongs(reader, PrimitiveArrays.INT64);
        }
    };

    /** C# {@code ulong[]} as {@code long[]} holding the raw bits: ext 78. */
    public static final Formatter<long[]> ULONG_ARRAY = new ReferenceFormatter<long[]>() {
        @Override
        void writeValue(MsgPackWriter writer, long[] value) {
            PrimitiveArrays.writeLongs(writer, PrimitiveArrays.UINT64, value);
        }

        @Override
        long[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readLongs(reader, PrimitiveArrays.UINT64);
        }
    };

    /** C# {@code float[]}: ext 78, exact bits. */
    public static final Formatter<float[]> FLOAT_ARRAY = new ReferenceFormatter<float[]>() {
        @Override
        void writeValue(MsgPackWriter writer, float[] value) {
            PrimitiveArrays.writeFloats(writer, PrimitiveArrays.SINGLE, value);
        }

        @Override
        float[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readFloats(reader, PrimitiveArrays.SINGLE);
        }
    };

    /** C# {@code double[]}: ext 78, exact bits. */
    public static final Formatter<double[]> DOUBLE_ARRAY = new ReferenceFormatter<double[]>() {
        @Override
        void writeValue(MsgPackWriter writer, double[] value) {
            PrimitiveArrays.writeDoubles(writer, PrimitiveArrays.DOUBLE, value);
        }

        @Override
        double[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readDoubles(reader, PrimitiveArrays.DOUBLE);
        }
    };

    /** C# {@code char[]}: ext 78. */
    public static final Formatter<char[]> CHAR_ARRAY = new ReferenceFormatter<char[]>() {
        @Override
        void writeValue(MsgPackWriter writer, char[] value) {
            PrimitiveArrays.writeChars(writer, PrimitiveArrays.CHAR, value);
        }

        @Override
        char[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readChars(reader, PrimitiveArrays.CHAR);
        }
    };

    /** C# {@code Half[]} as raw binary16 bits in a {@code short[]}: ext 78, exact. */
    public static final Formatter<short[]> HALF_BITS_ARRAY = new ReferenceFormatter<short[]>() {
        @Override
        void writeValue(MsgPackWriter writer, short[] value) {
            PrimitiveArrays.writeShorts(writer, PrimitiveArrays.HALF, value);
        }

        @Override
        short[] readValue(MsgPackReader reader) {
            return PrimitiveArrays.readShorts(reader, PrimitiveArrays.HALF);
        }
    };

    /**
     * C# {@code Half[]} as {@code float[]} (the generator's mapping): ext 78 of binary16 values, each float rounded to
     * binary16 on write (see {@link Float16}).
     */
    public static final Formatter<float[]> HALF_ARRAY = new ReferenceFormatter<float[]>() {
        @Override
        void writeValue(MsgPackWriter writer, float[] value) {
            short[] bits = new short[value.length];
            for (int i = 0; i < bits.length; i++) {
                bits[i] = Float16.fromFloat(value[i]);
            }
            PrimitiveArrays.writeShorts(writer, PrimitiveArrays.HALF, bits);
        }

        @Override
        float[] readValue(MsgPackReader reader) {
            short[] bits = PrimitiveArrays.readShorts(reader, PrimitiveArrays.HALF);
            float[] result = new float[bits.length];
            for (int i = 0; i < bits.length; i++) {
                result[i] = Float16.toFloat(bits[i]);
            }
            return result;
        }
    };

    // ------------------------------------------------------------------ Containers

    /**
     * C# {@code T?}: nil for null, otherwise the inner formatter.
     *
     * @param inner the formatter of the value type
     * @param <T> the type
     * @return the formatter
     */
    public static <T> Formatter<T> nullable(Formatter<T> inner) {
        requireNonNull(inner, "inner");
        return new Formatter<T>() {
            @Override
            public void write(MsgPackWriter writer, @Nullable T value) {
                if (value == null) {
                    writer.writeNil();
                } else {
                    inner.write(writer, value);
                }
            }

            @Override
            public @Nullable T read(MsgPackReader reader) {
                return reader.tryReadNil() ? null : inner.read(reader);
            }
        };
    }

    private static void writeElements(MsgPackWriter writer, Collection<?> values, Formatter<?> element) {
        @SuppressWarnings("unchecked")
        Formatter<Object> f = (Formatter<Object>) element;
        writer.writeArrayHeader(values.size());
        for (Object item : values) {
            f.write(writer, item);
        }
    }

    /**
     * An element-wise array (C# {@code List<T>}, {@code T[]}, {@code IList<T>}, {@code IEnumerable<T>}... of a
     * non-primitive) as {@link List}; reads an {@link ArrayList}.
     *
     * @param element the element formatter
     * @param <T> the element type
     * @return the formatter
     */
    public static <T> Formatter<List<T>> list(Formatter<T> element) {
        requireNonNull(element, "element");
        return new ReferenceFormatter<List<T>>() {
            @Override
            void writeValue(MsgPackWriter writer, List<T> value) {
                writeElements(writer, value, element);
            }

            @Override
            List<T> readValue(MsgPackReader reader) {
                int count = reader.readArrayHeader();
                reader.enter();
                List<T> result = new ArrayList<>(count);
                for (int i = 0; i < count; i++) {
                    result.add(element.read(reader));
                }
                reader.exit();
                return result;
            }
        };
    }

    /**
     * C# {@code HashSet<T>} as {@link Set}: an array; reads a {@link LinkedHashSet} in wire order.
     *
     * @param element the element formatter
     * @param <T> the element type
     * @return the formatter
     */
    public static <T> Formatter<Set<T>> set(Formatter<T> element) {
        requireNonNull(element, "element");
        return new ReferenceFormatter<Set<T>>() {
            @Override
            void writeValue(MsgPackWriter writer, Set<T> value) {
                writeElements(writer, value, element);
            }

            @Override
            Set<T> readValue(MsgPackReader reader) {
                int count = reader.readArrayHeader();
                reader.enter();
                Set<T> result = new LinkedHashSet<>(capacity(count));
                for (int i = 0; i < count; i++) {
                    result.add(element.read(reader));
                }
                reader.exit();
                return result;
            }
        };
    }

    private static <T> Formatter<ArrayDeque<T>> deque(Formatter<T> element) {
        requireNonNull(element, "element");
        return new ReferenceFormatter<ArrayDeque<T>>() {
            @Override
            void writeValue(MsgPackWriter writer, ArrayDeque<T> value) {
                writeElements(writer, value, element);
            }

            @Override
            ArrayDeque<T> readValue(MsgPackReader reader) {
                int count = reader.readArrayHeader();
                reader.enter();
                ArrayDeque<T> result = new ArrayDeque<>(Math.max(count, 1));
                for (int i = 0; i < count; i++) {
                    T item = element.read(reader);
                    if (item == null) {
                        throw new MsgPackException(Kind.VALUE, "An ArrayDeque cannot hold null.");
                    }
                    result.addLast(item);
                }
                reader.exit();
                return result;
            }
        };
    }

    /**
     * C# {@code Queue<T>} as {@link ArrayDeque}: an array, front first (the deque's head is the front).
     *
     * @param element the element formatter
     * @param <T> the element type
     * @return the formatter
     */
    public static <T> Formatter<ArrayDeque<T>> queue(Formatter<T> element) {
        return deque(element);
    }

    /**
     * C# {@code Stack<T>} as {@link ArrayDeque}: an array, top first (the deque's head is the top, as with
     * {@link ArrayDeque#push}).
     *
     * @param element the element formatter
     * @param <T> the element type
     * @return the formatter
     */
    public static <T> Formatter<ArrayDeque<T>> stack(Formatter<T> element) {
        return deque(element);
    }

    /**
     * C# {@code Dictionary<K,V>} (and {@code IDictionary}, {@code IReadOnlyDictionary}) as {@link Map}: a map;
     * reads a {@link LinkedHashMap} in wire order.
     *
     * @param key the key formatter
     * @param value the value formatter
     * @param <K> the key type
     * @param <V> the value type
     * @return the formatter
     */
    public static <K, V> Formatter<Map<K, V>> map(Formatter<K> key, Formatter<V> value) {
        requireNonNull(key, "key");
        requireNonNull(value, "value");
        return new ReferenceFormatter<Map<K, V>>() {
            @Override
            void writeValue(MsgPackWriter writer, Map<K, V> map) {
                writer.writeMapHeader(map.size());
                for (Map.Entry<K, V> entry : map.entrySet()) {
                    key.write(writer, entry.getKey());
                    value.write(writer, entry.getValue());
                }
            }

            @Override
            Map<K, V> readValue(MsgPackReader reader) {
                int count = reader.readMapHeader();
                reader.enter();
                Map<K, V> result = new LinkedHashMap<>(capacity(count));
                for (int i = 0; i < count; i++) {
                    K k = key.read(reader);
                    result.put(k, value.read(reader));
                }
                reader.exit();
                return result;
            }
        };
    }

    /**
     * C# {@code KeyValuePair<K,V>} as {@link Map.Entry}: an array of {@code [key, value]}.
     *
     * @param key the key formatter
     * @param value the value formatter
     * @param <K> the key type
     * @param <V> the value type
     * @return the formatter
     */
    public static <K, V> Formatter<Map.Entry<K, V>> keyValuePair(Formatter<K> key, Formatter<V> value) {
        requireNonNull(key, "key");
        requireNonNull(value, "value");
        return new StructFormatter<Map.Entry<K, V>>("KeyValuePair") {
            @Override
            void writeValue(MsgPackWriter writer, Map.Entry<K, V> entry) {
                writer.writeArrayHeader(2);
                key.write(writer, entry.getKey());
                value.write(writer, entry.getValue());
            }

            @Override
            Map.Entry<K, V> readValue(MsgPackReader reader) {
                reader.readArrayHeader(2);
                reader.enter();
                K k = key.read(reader);
                V v = value.read(reader);
                reader.exit();
                return new AbstractMap.SimpleImmutableEntry<>(k, v);
            }
        };
    }

    /**
     * A tuple of 1 (C# {@code ValueTuple<T1>} or {@code Tuple<T1>}): an array of exactly 1 element.
     *
     * @param f1 the element formatter
     * @param <T1> the element type
     * @return the formatter
     */
    public static <T1> Formatter<Tuple1<T1>> tuple(Formatter<T1> f1) {
        return new TupleFormatter<Tuple1<T1>>(1) {
            @Override
            void writeItems(MsgPackWriter w, Tuple1<T1> t) {
                f1.write(w, t.item1());
            }

            @Override
            Tuple1<T1> readItems(MsgPackReader r) {
                return Tuple1.of(f1.read(r));
            }
        };
    }

    /**
     * A tuple of 2: an array of exactly 2 elements.
     *
     * @param f1 the first element formatter
     * @param f2 the second element formatter
     * @param <T1> the first element type
     * @param <T2> the second element type
     * @return the formatter
     */
    public static <T1, T2> Formatter<Tuple2<T1, T2>> tuple(Formatter<T1> f1, Formatter<T2> f2) {
        return new TupleFormatter<Tuple2<T1, T2>>(2) {
            @Override
            void writeItems(MsgPackWriter w, Tuple2<T1, T2> t) {
                f1.write(w, t.item1());
                f2.write(w, t.item2());
            }

            @Override
            Tuple2<T1, T2> readItems(MsgPackReader r) {
                T1 a = f1.read(r);
                return Tuple2.of(a, f2.read(r));
            }
        };
    }

    /**
     * A tuple of 3: an array of exactly 3 elements.
     *
     * @param f1 the formatter of element 1
     * @param f2 the formatter of element 2
     * @param f3 the formatter of element 3
     * @param <T1> the type of element 1
     * @param <T2> the type of element 2
     * @param <T3> the type of element 3
     * @return the formatter
     */
    public static <T1, T2, T3> Formatter<Tuple3<T1, T2, T3>> tuple(Formatter<T1> f1, Formatter<T2> f2,
        Formatter<T3> f3) {
        return new TupleFormatter<Tuple3<T1, T2, T3>>(3) {
            @Override
            void writeItems(MsgPackWriter w, Tuple3<T1, T2, T3> t) {
                f1.write(w, t.item1());
                f2.write(w, t.item2());
                f3.write(w, t.item3());
            }

            @Override
            Tuple3<T1, T2, T3> readItems(MsgPackReader r) {
                T1 a = f1.read(r);
                T2 b = f2.read(r);
                return Tuple3.of(a, b, f3.read(r));
            }
        };
    }

    /**
     * A tuple of 4: an array of exactly 4 elements.
     *
     * @param f1 the formatter of element 1
     * @param f2 the formatter of element 2
     * @param f3 the formatter of element 3
     * @param f4 the formatter of element 4
     * @param <T1> the type of element 1
     * @param <T2> the type of element 2
     * @param <T3> the type of element 3
     * @param <T4> the type of element 4
     * @return the formatter
     */
    public static <T1, T2, T3, T4> Formatter<Tuple4<T1, T2, T3, T4>> tuple(Formatter<T1> f1, Formatter<T2> f2,
        Formatter<T3> f3, Formatter<T4> f4) {
        return new TupleFormatter<Tuple4<T1, T2, T3, T4>>(4) {
            @Override
            void writeItems(MsgPackWriter w, Tuple4<T1, T2, T3, T4> t) {
                f1.write(w, t.item1());
                f2.write(w, t.item2());
                f3.write(w, t.item3());
                f4.write(w, t.item4());
            }

            @Override
            Tuple4<T1, T2, T3, T4> readItems(MsgPackReader r) {
                T1 a = f1.read(r);
                T2 b = f2.read(r);
                T3 c = f3.read(r);
                return Tuple4.of(a, b, c, f4.read(r));
            }
        };
    }

    /**
     * A tuple of 5: an array of exactly 5 elements.
     *
     * @param f1 the formatter of element 1
     * @param f2 the formatter of element 2
     * @param f3 the formatter of element 3
     * @param f4 the formatter of element 4
     * @param f5 the formatter of element 5
     * @param <T1> the type of element 1
     * @param <T2> the type of element 2
     * @param <T3> the type of element 3
     * @param <T4> the type of element 4
     * @param <T5> the type of element 5
     * @return the formatter
     */
    public static <T1, T2, T3, T4, T5> Formatter<Tuple5<T1, T2, T3, T4, T5>> tuple(Formatter<T1> f1,
        Formatter<T2> f2, Formatter<T3> f3, Formatter<T4> f4, Formatter<T5> f5) {
        return new TupleFormatter<Tuple5<T1, T2, T3, T4, T5>>(5) {
            @Override
            void writeItems(MsgPackWriter w, Tuple5<T1, T2, T3, T4, T5> t) {
                f1.write(w, t.item1());
                f2.write(w, t.item2());
                f3.write(w, t.item3());
                f4.write(w, t.item4());
                f5.write(w, t.item5());
            }

            @Override
            Tuple5<T1, T2, T3, T4, T5> readItems(MsgPackReader r) {
                T1 a = f1.read(r);
                T2 b = f2.read(r);
                T3 c = f3.read(r);
                T4 d = f4.read(r);
                return Tuple5.of(a, b, c, d, f5.read(r));
            }
        };
    }

    /**
     * A tuple of 6: an array of exactly 6 elements.
     *
     * @param f1 the formatter of element 1
     * @param f2 the formatter of element 2
     * @param f3 the formatter of element 3
     * @param f4 the formatter of element 4
     * @param f5 the formatter of element 5
     * @param f6 the formatter of element 6
     * @param <T1> the type of element 1
     * @param <T2> the type of element 2
     * @param <T3> the type of element 3
     * @param <T4> the type of element 4
     * @param <T5> the type of element 5
     * @param <T6> the type of element 6
     * @return the formatter
     */
    public static <T1, T2, T3, T4, T5, T6> Formatter<Tuple6<T1, T2, T3, T4, T5, T6>> tuple(Formatter<T1> f1,
        Formatter<T2> f2, Formatter<T3> f3, Formatter<T4> f4, Formatter<T5> f5, Formatter<T6> f6) {
        return new TupleFormatter<Tuple6<T1, T2, T3, T4, T5, T6>>(6) {
            @Override
            void writeItems(MsgPackWriter w, Tuple6<T1, T2, T3, T4, T5, T6> t) {
                f1.write(w, t.item1());
                f2.write(w, t.item2());
                f3.write(w, t.item3());
                f4.write(w, t.item4());
                f5.write(w, t.item5());
                f6.write(w, t.item6());
            }

            @Override
            Tuple6<T1, T2, T3, T4, T5, T6> readItems(MsgPackReader r) {
                T1 a = f1.read(r);
                T2 b = f2.read(r);
                T3 c = f3.read(r);
                T4 d = f4.read(r);
                T5 e = f5.read(r);
                return Tuple6.of(a, b, c, d, e, f6.read(r));
            }
        };
    }

    /**
     * A tuple of 7: an array of exactly 7 elements.
     *
     * @param f1 the formatter of element 1
     * @param f2 the formatter of element 2
     * @param f3 the formatter of element 3
     * @param f4 the formatter of element 4
     * @param f5 the formatter of element 5
     * @param f6 the formatter of element 6
     * @param f7 the formatter of element 7
     * @param <T1> the type of element 1
     * @param <T2> the type of element 2
     * @param <T3> the type of element 3
     * @param <T4> the type of element 4
     * @param <T5> the type of element 5
     * @param <T6> the type of element 6
     * @param <T7> the type of element 7
     * @return the formatter
     */
    public static <T1, T2, T3, T4, T5, T6, T7> Formatter<Tuple7<T1, T2, T3, T4, T5, T6, T7>> tuple(
        Formatter<T1> f1, Formatter<T2> f2, Formatter<T3> f3, Formatter<T4> f4, Formatter<T5> f5, Formatter<T6> f6,
        Formatter<T7> f7) {
        return new TupleFormatter<Tuple7<T1, T2, T3, T4, T5, T6, T7>>(7) {
            @Override
            void writeItems(MsgPackWriter w, Tuple7<T1, T2, T3, T4, T5, T6, T7> t) {
                f1.write(w, t.item1());
                f2.write(w, t.item2());
                f3.write(w, t.item3());
                f4.write(w, t.item4());
                f5.write(w, t.item5());
                f6.write(w, t.item6());
                f7.write(w, t.item7());
            }

            @Override
            Tuple7<T1, T2, T3, T4, T5, T6, T7> readItems(MsgPackReader r) {
                T1 a = f1.read(r);
                T2 b = f2.read(r);
                T3 c = f3.read(r);
                T4 d = f4.read(r);
                T5 e = f5.read(r);
                T6 f = f6.read(r);
                return Tuple7.of(a, b, c, d, e, f, f7.read(r));
            }
        };
    }

    /**
     * Tuples are written as a fixed-size array. A C# {@code ValueTuple} is a value type and a {@code Tuple} a
     * reference type; both map to {@code TupleN}, and nil reads as null (wrap in nothing for either).
     */
    private abstract static class TupleFormatter<T> extends ReferenceFormatter<T> {
        private final int arity;

        TupleFormatter(int arity) {
            this.arity = arity;
        }

        @Override
        final void writeValue(MsgPackWriter writer, T value) {
            writer.writeArrayHeader(arity);
            writeItems(writer, value);
        }

        @Override
        final T readValue(MsgPackReader reader) {
            reader.readArrayHeader(arity);
            reader.enter();
            T result = readItems(reader);
            reader.exit();
            return result;
        }

        abstract void writeItems(MsgPackWriter writer, T value);

        abstract T readItems(MsgPackReader reader);
    }

    // ------------------------------------------------------------------ Whole values

    /**
     * Serializes one value.
     *
     * @param formatter the formatter
     * @param value the value
     * @param <T> the type
     * @return the bytes
     */
    public static <T> byte[] serialize(Formatter<T> formatter, @Nullable T value) {
        try (MsgPackWriter writer = new MsgPackWriter()) {
            formatter.write(writer, value);
            return writer.toByteArray();
        }
    }

    /**
     * Deserializes exactly one value from all of {@code bytes} with the {@link MsgPackOptions#UNTRUSTED} options.
     *
     * @param formatter the formatter
     * @param bytes the bytes
     * @param <T> the type
     * @return the value
     * @throws MsgPackException on malformed input or bytes after the value
     */
    public static <T> @Nullable T deserialize(Formatter<T> formatter, byte[] bytes) {
        return deserialize(formatter, bytes, MsgPackOptions.UNTRUSTED);
    }

    /**
     * Deserializes exactly one value from all of {@code bytes}.
     *
     * @param formatter the formatter
     * @param bytes the bytes
     * @param options the hardening options
     * @param <T> the type
     * @return the value
     * @throws MsgPackException on malformed input or bytes after the value
     */
    public static <T> @Nullable T deserialize(Formatter<T> formatter, byte[] bytes, MsgPackOptions options) {
        MsgPackReader reader = new MsgPackReader(bytes, 0, bytes.length, options);
        T value = formatter.read(reader);
        reader.ensureEnd();
        return value;
    }

    // ------------------------------------------------------------------ Byte helpers

    private static int capacity(int count) {
        return count < 3 ? count + 1 : (int) Math.min(Integer.MAX_VALUE, count / 3L * 4 + 1);
    }

    private static void requireNonNull(Object value, String name) {
        if (value == null) {
            throw new NullPointerException(name);
        }
    }

    private static void putLongBigEndian(byte[] b, int at, long v) {
        for (int i = 7; i >= 0; i--) {
            b[at + i] = (byte) v;
            v >>>= 8;
        }
    }

    private static long getLongBigEndian(byte[] b, int at) {
        long v = 0;
        for (int i = 0; i < 8; i++) {
            v = (v << 8) | (b[at + i] & 0xFF);
        }
        return v;
    }

    private static void putIntLittleEndian(byte[] b, int at, int v) {
        b[at] = (byte) v;
        b[at + 1] = (byte) (v >>> 8);
        b[at + 2] = (byte) (v >>> 16);
        b[at + 3] = (byte) (v >>> 24);
    }

    private static int getIntLittleEndian(byte[] b, int at) {
        return (b[at] & 0xFF) | ((b[at + 1] & 0xFF) << 8) | ((b[at + 2] & 0xFF) << 16) | ((b[at + 3] & 0xFF) << 24);
    }
}

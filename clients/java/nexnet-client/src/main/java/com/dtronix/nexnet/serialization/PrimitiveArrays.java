package com.dtronix.nexnet.serialization;

import com.dtronix.nexnet.serialization.MsgPackException.Kind;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * The NexNet ext 78 primitive arrays (protocol specification section 10.4): one ext value whose payload is an element
 * kind byte followed by the elements in little-endian order.
 */
final class PrimitiveArrays {
    /** The ext type code. */
    static final int EXT_TYPE = 78;

    static final int SBYTE = 1;
    static final int INT16 = 2;
    static final int UINT16 = 3;
    static final int INT32 = 4;
    static final int UINT32 = 5;
    static final int INT64 = 6;
    static final int UINT64 = 7;
    static final int SINGLE = 8;
    static final int DOUBLE = 9;
    static final int CHAR = 10;
    static final int HALF = 11;

    private PrimitiveArrays() {
    }

    /** Writes the header and kind byte; returns a little-endian buffer over the {@code bytes} data bytes to fill. */
    private static ByteBuffer begin(MsgPackWriter writer, int kind, long bytes) {
        if (bytes + 1 > Integer.MAX_VALUE - 16) {
            throw new MsgPackException(Kind.SIZE, "The array is larger than 2 GiB.");
        }
        int length = (int) bytes;
        writer.writeExtHeader(EXT_TYPE, length + 1);
        writer.ensure(length + 1);
        byte[] buffer = writer.buffer();
        int at = writer.size();
        buffer[at] = (byte) kind;
        writer.advance(length + 1);
        return ByteBuffer.wrap(buffer, at + 1, length).slice().order(ByteOrder.LITTLE_ENDIAN);
    }

    static void writeBytes(MsgPackWriter writer, int kind, byte[] values) {
        begin(writer, kind, values.length).put(values);
    }

    static void writeShorts(MsgPackWriter writer, int kind, short[] values) {
        begin(writer, kind, 2L * values.length).asShortBuffer().put(values);
    }

    static void writeChars(MsgPackWriter writer, int kind, char[] values) {
        begin(writer, kind, 2L * values.length).asCharBuffer().put(values);
    }

    static void writeInts(MsgPackWriter writer, int kind, int[] values) {
        begin(writer, kind, 4L * values.length).asIntBuffer().put(values);
    }

    static void writeLongs(MsgPackWriter writer, int kind, long[] values) {
        begin(writer, kind, 8L * values.length).asLongBuffer().put(values);
    }

    static void writeFloats(MsgPackWriter writer, int kind, float[] values) {
        // Through the raw bits: a FloatBuffer may canonicalize NaN payloads.
        java.nio.IntBuffer out = begin(writer, kind, 4L * values.length).asIntBuffer();
        for (float v : values) {
            out.put(Float.floatToRawIntBits(v));
        }
    }

    static void writeDoubles(MsgPackWriter writer, int kind, double[] values) {
        java.nio.LongBuffer out = begin(writer, kind, 8L * values.length).asLongBuffer();
        for (double v : values) {
            out.put(Double.doubleToRawLongBits(v));
        }
    }

    /** Reads the header and kind byte; returns a little-endian buffer over the data of whole elements. */
    static ByteBuffer read(MsgPackReader reader, int kind, int elementSize) {
        int length = reader.readExtHeader();
        if (reader.lastExtType() != EXT_TYPE) {
            throw new MsgPackException(Kind.TYPE, "Expected ext " + EXT_TYPE + ", found ext " + reader.lastExtType()
                + ".");
        }
        if (length < 1) {
            throw new MsgPackException(Kind.SIZE, "The primitive array has no element kind byte.");
        }
        int at = reader.take(length);
        byte[] data = reader.array();
        int found = data[at];
        if (found != kind) {
            throw new MsgPackException(Kind.TYPE, "The primitive array has element kind " + found + ", expected "
                + kind + ".");
        }
        int dataLength = length - 1;
        if (dataLength % elementSize != 0) {
            throw new MsgPackException(Kind.SIZE, "The primitive array data is not a whole number of elements.");
        }
        return ByteBuffer.wrap(data, at + 1, dataLength).slice().order(ByteOrder.LITTLE_ENDIAN);
    }

    static byte[] readBytes(MsgPackReader reader, int kind) {
        ByteBuffer in = read(reader, kind, 1);
        byte[] result = new byte[in.remaining()];
        in.get(result);
        return result;
    }

    static short[] readShorts(MsgPackReader reader, int kind) {
        java.nio.ShortBuffer in = read(reader, kind, 2).asShortBuffer();
        short[] result = new short[in.remaining()];
        in.get(result);
        return result;
    }

    static char[] readChars(MsgPackReader reader, int kind) {
        java.nio.CharBuffer in = read(reader, kind, 2).asCharBuffer();
        char[] result = new char[in.remaining()];
        in.get(result);
        return result;
    }

    static int[] readInts(MsgPackReader reader, int kind) {
        java.nio.IntBuffer in = read(reader, kind, 4).asIntBuffer();
        int[] result = new int[in.remaining()];
        in.get(result);
        return result;
    }

    static long[] readLongs(MsgPackReader reader, int kind) {
        java.nio.LongBuffer in = read(reader, kind, 8).asLongBuffer();
        long[] result = new long[in.remaining()];
        in.get(result);
        return result;
    }

    static float[] readFloats(MsgPackReader reader, int kind) {
        java.nio.IntBuffer in = read(reader, kind, 4).asIntBuffer();
        float[] result = new float[in.remaining()];
        for (int i = 0; i < result.length; i++) {
            result[i] = Float.intBitsToFloat(in.get());
        }
        return result;
    }

    static double[] readDoubles(MsgPackReader reader, int kind) {
        java.nio.LongBuffer in = read(reader, kind, 8).asLongBuffer();
        double[] result = new double[in.remaining()];
        for (int i = 0; i < result.length; i++) {
            result[i] = Double.longBitsToDouble(in.get());
        }
        return result;
    }
}

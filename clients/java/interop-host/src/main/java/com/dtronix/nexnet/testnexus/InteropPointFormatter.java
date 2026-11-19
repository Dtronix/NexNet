package com.dtronix.nexnet.testnexus;

import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import java.util.Arrays;
import java.util.List;

/**
 * The Java formatter of the C# {@code InteropPoint} ({@code NexNet.TestNexus.Interop.InteropPointFormatter}):
 * {@code [X, Y]}, two canonical integers. The zoo maps it with
 * {@code [assembly: NexusTargetFormatter<InteropPoint>(NexusTargets.Java, "com.dtronix.nexnet.testnexus.InteropPointFormatter")]}.
 * The Java value is the list {@code [x, y]}.
 */
public final class InteropPointFormatter implements Formatter<List<Integer>> {
    /** Creates the formatter. */
    public InteropPointFormatter() {
    }

    @Override
    public void write(MsgPackWriter writer, List<Integer> value) {
        // InteropPoint is a C# struct: null (the typed-JSON default) is the default point.
        writer.writeArrayHeader(2);
        writer.writeInt(value == null ? 0 : value.get(0));
        writer.writeInt(value == null ? 0 : value.get(1));
    }

    @Override
    public List<Integer> read(MsgPackReader reader) {
        reader.readArrayHeader(2);
        int x = reader.readInt();
        return Arrays.asList(x, reader.readInt());
    }
}

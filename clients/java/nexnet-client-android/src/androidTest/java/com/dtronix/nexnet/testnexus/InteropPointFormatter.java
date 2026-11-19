package com.dtronix.nexnet.testnexus;

import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import java.util.Arrays;
import java.util.List;

/**
 * The user formatter the interop zoo declares with NexusTargetFormatter (the C# InteropPoint as [X, Y]). The generated
 * interop client names this class, so the device tests that compile it need it too.
 */
public final class InteropPointFormatter implements Formatter<List<Integer>> {
    /** Creates the formatter. */
    public InteropPointFormatter() {
    }

    @Override
    public void write(MsgPackWriter writer, List<Integer> value) {
        writer.writeArrayHeader(2);
        writer.writeInt(value.get(0));
        writer.writeInt(value.get(1));
    }

    @Override
    public List<Integer> read(MsgPackReader reader) {
        reader.readArrayHeader(2);
        int x = reader.readInt();
        return Arrays.asList(x, reader.readInt());
    }
}

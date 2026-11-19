package com.dtronix.nexnet.consumer;

import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import com.dtronix.nexnet.testnexus.InteropNested;
import com.dtronix.nexnet.testnexus.formatters.Formatters;
import java.util.Collections;

/** Round-trips a generated type through its generated formatter, on the consumer's runtime classpath. */
public final class ConsumerMain {
    private ConsumerMain() {
    }

    /**
     * Runs the check.
     *
     * @param args unused
     */
    public static void main(String[] args) {
        InteropNested value = new InteropNested();
        value.setItems(Collections.emptyList());
        value.setChild(new InteropNested());

        MsgPackWriter writer = new MsgPackWriter();
        Formatters.InteropNested.write(writer, value);
        InteropNested read = Formatters.InteropNested.read(new MsgPackReader(writer.toByteArray()));
        if (!value.equals(read)) {
            throw new AssertionError("Round trip changed the value: " + value + " became " + read);
        }
        System.out.println("nexnet-client consumer check passed: " + read);
    }
}

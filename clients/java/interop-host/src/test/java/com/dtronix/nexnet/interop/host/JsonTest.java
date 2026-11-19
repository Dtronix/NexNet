package com.dtronix.nexnet.interop.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.math.BigInteger;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;

class JsonTest {
    private static final String ESCAPE = "\\" + "u";

    @Test
    void roundTripsControlMessages() {
        String text = "{\"t\":\"req\",\"id\":17,\"test\":null,\"args\":{\"a\":[1,-2,\"x\",true,false,null],\"f\":1.5}}";
        Object parsed = Json.parse(text);
        assertEquals(text, Json.write(parsed));
        @SuppressWarnings("unchecked")
        Map<String, Object> map = (Map<String, Object>) parsed;
        assertEquals(17L, map.get("id"));
        assertNull(map.get("test"));
    }

    @Test
    void keepsLargeIntegersExact() {
        assertEquals(new BigInteger("18446744073709551615"), Json.parse("18446744073709551615"));
        assertEquals(Long.MIN_VALUE, Json.parse("-9223372036854775808"));
    }

    @Test
    void escapesControlCharactersAndLoneSurrogates() {
        String emoji = new String(Character.toChars(0x1F600));
        String value = "q\"b\\n\n" + (char) 1 + emoji + (char) 0xD800;
        String json = Json.write(value);
        assertEquals("\"q\\\"b\\\\n\\n" + ESCAPE + "0001" + emoji + ESCAPE + "d800\"", json);
        assertEquals(value, Json.parse(json));
    }

    @Test
    void writesLists() {
        List<Object> list = Arrays.asList(1L, "a", null);
        assertEquals("[1,\"a\",null]", Json.write(list));
    }

    @Test
    void refusesMalformedJson() {
        assertThrows(IllegalArgumentException.class, () -> Json.parse("{\"a\":}"));
        assertThrows(IllegalArgumentException.class, () -> Json.parse("[1,2"));
        assertThrows(IllegalArgumentException.class, () -> Json.parse("1 2"));
    }
}

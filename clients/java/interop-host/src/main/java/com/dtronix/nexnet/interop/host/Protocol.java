package com.dtronix.nexnet.interop.host;

import java.util.LinkedHashMap;
import java.util.Map;

/** Builds the JSON objects of results and events. */
final class Protocol {
    private Protocol() {
    }

    static Map<String, Object> empty() {
        return new LinkedHashMap<>();
    }

    static Map<String, Object> map(Object... pairs) {
        Map<String, Object> map = new LinkedHashMap<>();
        for (int i = 0; i + 1 < pairs.length; i += 2) {
            map.put((String) pairs[i], pairs[i + 1]);
        }
        return map;
    }
}

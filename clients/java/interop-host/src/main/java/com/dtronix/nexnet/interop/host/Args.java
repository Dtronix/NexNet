package com.dtronix.nexnet.interop.host;

import java.math.BigInteger;
import java.util.Collections;
import java.util.List;
import java.util.Map;

/** Reads the members of a request's {@code args} object; a missing or mistyped member fails with BadRequest. */
final class Args {
    private final Map<String, Object> map;

    Args(Map<String, Object> map) {
        this.map = map == null ? Collections.emptyMap() : map;
    }

    Map<String, Object> raw() {
        return map;
    }

    boolean has(String name) {
        return map.get(name) != null;
    }

    Object value(String name) {
        return map.get(name);
    }

    String str(String name) {
        Object value = map.get(name);
        if (!(value instanceof String)) {
            throw HostError.badRequest("The argument '" + name + "' must be a string.");
        }
        return (String) value;
    }

    String optStr(String name) {
        Object value = map.get(name);
        if (value == null) {
            return null;
        }
        if (!(value instanceof String)) {
            throw HostError.badRequest("The argument '" + name + "' must be a string.");
        }
        return (String) value;
    }

    long integer(String name) {
        Long value = optInteger(name);
        if (value == null) {
            throw HostError.badRequest("The argument '" + name + "' is required.");
        }
        return value;
    }

    Long optInteger(String name) {
        return toLong(name, map.get(name));
    }

    static Long toLong(String name, Object value) {
        if (value == null) {
            return null;
        }
        if (value instanceof Long || value instanceof Integer) {
            return ((Number) value).longValue();
        }
        if (value instanceof BigInteger) {
            throw HostError.badRequest("The argument '" + name + "' is out of range.");
        }
        if (value instanceof Double) {
            double d = (Double) value;
            if (d == Math.rint(d) && Math.abs(d) < 9.0e15) {
                return (long) d;
            }
        }
        throw HostError.badRequest("The argument '" + name + "' must be an integer.");
    }

    int int32(String name) {
        long value = integer(name);
        if (value < Integer.MIN_VALUE || value > Integer.MAX_VALUE) {
            throw HostError.badRequest("The argument '" + name + "' is out of range.");
        }
        return (int) value;
    }

    boolean bool(String name) {
        Object value = map.get(name);
        if (value == null) {
            return false;
        }
        if (!(value instanceof Boolean)) {
            throw HostError.badRequest("The argument '" + name + "' must be a boolean.");
        }
        return (Boolean) value;
    }

    Args obj(String name) {
        Args value = optObj(name);
        if (value == null) {
            throw HostError.badRequest("The argument '" + name + "' is required.");
        }
        return value;
    }

    Args optObj(String name) {
        Object value = map.get(name);
        if (value == null) {
            return null;
        }
        if (!(value instanceof Map)) {
            throw HostError.badRequest("The argument '" + name + "' must be an object.");
        }
        @SuppressWarnings("unchecked")
        Map<String, Object> object = (Map<String, Object>) value;
        return new Args(object);
    }

    List<Object> list(String name) {
        Object value = map.get(name);
        if (value == null) {
            return Collections.emptyList();
        }
        if (!(value instanceof List)) {
            throw HostError.badRequest("The argument '" + name + "' must be an array.");
        }
        @SuppressWarnings("unchecked")
        List<Object> list = (List<Object>) value;
        return list;
    }
}

package com.dtronix.nexnet.serialization;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * A minimal JSON reader and canonical writer for the golden vector files (the core runtime has no dependencies, so
 * the tests have none either). Values: {@code null}, {@link Boolean}, {@link BigInteger} (integral numbers),
 * {@link BigDecimal} (other numbers), {@link String}, {@link List} and {@link Map} (insertion-ordered).
 */
final class Json {
    private final String text;
    private int pos;

    private Json(String text) {
        this.text = text;
    }

    static Object parse(String text) {
        Json json = new Json(text);
        json.ws();
        Object value = json.value();
        json.ws();
        if (json.pos != text.length()) {
            throw json.error("trailing characters");
        }
        return value;
    }

    private IllegalArgumentException error(String message) {
        return new IllegalArgumentException("JSON " + message + " at " + pos + ": " + text);
    }

    private void ws() {
        while (pos < text.length() && " \t\r\n".indexOf(text.charAt(pos)) >= 0) {
            pos++;
        }
    }

    private void expect(char c) {
        if (pos >= text.length() || text.charAt(pos) != c) {
            throw error("expected '" + c + "'");
        }
        pos++;
    }

    private Object value() {
        if (pos >= text.length()) {
            throw error("unexpected end");
        }
        char c = text.charAt(pos);
        switch (c) {
            case '{': {
                pos++;
                Map<String, Object> map = new LinkedHashMap<>();
                ws();
                if (text.charAt(pos) == '}') {
                    pos++;
                    return map;
                }
                while (true) {
                    ws();
                    String key = string();
                    ws();
                    expect(':');
                    ws();
                    if (map.containsKey(key)) {
                        throw error("duplicate key " + key);
                    }
                    map.put(key, value());
                    ws();
                    if (text.charAt(pos) == ',') {
                        pos++;
                        continue;
                    }
                    expect('}');
                    return map;
                }
            }
            case '[': {
                pos++;
                List<Object> list = new ArrayList<>();
                ws();
                if (text.charAt(pos) == ']') {
                    pos++;
                    return list;
                }
                while (true) {
                    ws();
                    list.add(value());
                    ws();
                    if (text.charAt(pos) == ',') {
                        pos++;
                        continue;
                    }
                    expect(']');
                    return list;
                }
            }
            case '"':
                return string();
            case 't':
                literal("true");
                return Boolean.TRUE;
            case 'f':
                literal("false");
                return Boolean.FALSE;
            case 'n':
                literal("null");
                return null;
            default:
                return number();
        }
    }

    private void literal(String word) {
        if (!text.startsWith(word, pos)) {
            throw error("expected " + word);
        }
        pos += word.length();
    }

    private Object number() {
        int start = pos;
        while (pos < text.length() && "+-0123456789.eE".indexOf(text.charAt(pos)) >= 0) {
            pos++;
        }
        if (start == pos) {
            throw error("unexpected character");
        }
        BigDecimal d = new BigDecimal(text.substring(start, pos));
        try {
            return d.toBigIntegerExact();
        } catch (ArithmeticException e) {
            return d;
        }
    }

    private String string() {
        expect('"');
        StringBuilder sb = new StringBuilder();
        while (true) {
            if (pos >= text.length()) {
                throw error("unterminated string");
            }
            char c = text.charAt(pos++);
            if (c == '"') {
                return sb.toString();
            }
            if (c != '\\') {
                sb.append(c);
                continue;
            }
            char e = text.charAt(pos++);
            switch (e) {
                case '"':
                case '\\':
                case '/':
                    sb.append(e);
                    break;
                case 'b':
                    sb.append('\b');
                    break;
                case 'f':
                    sb.append('\f');
                    break;
                case 'n':
                    sb.append('\n');
                    break;
                case 'r':
                    sb.append('\r');
                    break;
                case 't':
                    sb.append('\t');
                    break;
                case 'u':
                    sb.append((char) Integer.parseInt(text.substring(pos, pos + 4), 16));
                    pos += 4;
                    break;
                default:
                    throw error("bad escape");
            }
        }
    }

    /** A canonical text form for comparisons: integral numbers without exponent, strings escaped as code units. */
    static String canonical(Object value) {
        StringBuilder sb = new StringBuilder();
        write(sb, value);
        return sb.toString();
    }

    private static void write(StringBuilder sb, Object value) {
        if (value == null) {
            sb.append("null");
        } else if (value instanceof Boolean) {
            sb.append(value);
        } else if (value instanceof BigInteger || value instanceof Long || value instanceof Integer) {
            sb.append(value);
        } else if (value instanceof BigDecimal) {
            BigDecimal d = (BigDecimal) value;
            try {
                sb.append(d.toBigIntegerExact());
            } catch (ArithmeticException e) {
                sb.append(d.stripTrailingZeros().toPlainString());
            }
        } else if (value instanceof String) {
            String s = (String) value;
            sb.append('"');
            for (int i = 0; i < s.length(); i++) {
                char c = s.charAt(i);
                if (c == '"' || c == '\\') {
                    sb.append('\\').append(c);
                } else if (c < 0x20 || c > 0x7E) {
                    sb.append(String.format("\\u%04x", (int) c));
                } else {
                    sb.append(c);
                }
            }
            sb.append('"');
        } else if (value instanceof List) {
            sb.append('[');
            boolean first = true;
            for (Object item : (List<?>) value) {
                if (!first) {
                    sb.append(',');
                }
                first = false;
                write(sb, item);
            }
            sb.append(']');
        } else if (value instanceof Map) {
            sb.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> e : ((Map<?, ?>) value).entrySet()) {
                if (!first) {
                    sb.append(',');
                }
                first = false;
                write(sb, e.getKey().toString());
                sb.append(':');
                write(sb, e.getValue());
            }
            sb.append('}');
        } else {
            throw new IllegalArgumentException("not a JSON value: " + value.getClass());
        }
    }
}

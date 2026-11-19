package com.dtronix.nexnet.interop.host;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * The JSON of the control protocol. Objects are {@code Map<String, Object>} (insertion ordered, null values allowed),
 * arrays {@code List<Object>}, integers {@link Long} (or {@link BigInteger} beyond 64 bits), other numbers
 * {@link Double}; typed JSON (the value model of the generated scaffolding) is a subset.
 */
final class Json {
    private Json() {
    }

    /**
     * Parses one JSON value.
     *
     * @param text the text
     * @return the value
     * @throws IllegalArgumentException for malformed JSON
     */
    static Object parse(String text) {
        Parser parser = new Parser(text);
        parser.whitespace();
        Object value = parser.value();
        parser.whitespace();
        if (parser.pos != text.length()) {
            throw new IllegalArgumentException("Trailing characters after the JSON value at " + parser.pos + ".");
        }
        return value;
    }

    /**
     * Writes a value.
     *
     * @param value a map, list, string, number, boolean or null
     * @return the JSON text
     */
    static String write(Object value) {
        StringBuilder sb = new StringBuilder();
        write(sb, value);
        return sb.toString();
    }

    private static void write(StringBuilder sb, Object value) {
        if (value == null) {
            sb.append("null");
        } else if (value instanceof String) {
            string(sb, (String) value);
        } else if (value instanceof Boolean) {
            sb.append(((Boolean) value).booleanValue() ? "true" : "false");
        } else if (value instanceof Long || value instanceof Integer || value instanceof Short || value instanceof Byte
            || value instanceof BigInteger) {
            sb.append(value.toString());
        } else if (value instanceof BigDecimal) {
            sb.append(((BigDecimal) value).toPlainString());
        } else if (value instanceof Number) {
            double d = ((Number) value).doubleValue();
            if (Double.isNaN(d) || Double.isInfinite(d)) {
                throw new IllegalArgumentException("JSON has no " + d + ".");
            }
            if (d == Math.rint(d) && Math.abs(d) < 1e15) {
                sb.append((long) d);
            } else {
                sb.append(Double.toString(d));
            }
        } else if (value instanceof Map) {
            sb.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> entry : ((Map<?, ?>) value).entrySet()) {
                if (!first) {
                    sb.append(',');
                }
                first = false;
                string(sb, String.valueOf(entry.getKey()));
                sb.append(':');
                write(sb, entry.getValue());
            }
            sb.append('}');
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
        } else {
            throw new IllegalArgumentException("Not a JSON value: " + value.getClass().getName() + ".");
        }
    }

    private static void string(StringBuilder sb, String s) {
        sb.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"':
                    sb.append("\\\"");
                    break;
                case '\\':
                    sb.append("\\\\");
                    break;
                case '\n':
                    sb.append("\\n");
                    break;
                case '\r':
                    sb.append("\\r");
                    break;
                case '\t':
                    sb.append("\\t");
                    break;
                default:
                    boolean lone = Character.isHighSurrogate(c)
                        ? i + 1 >= s.length() || !Character.isLowSurrogate(s.charAt(i + 1))
                        : Character.isLowSurrogate(c) && (i == 0 || !Character.isHighSurrogate(s.charAt(i - 1)));
                    if (c < 0x20 || lone) {
                        sb.append(String.format("\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
            }
        }
        sb.append('"');
    }

    private static final class Parser {
        private final String s;
        private int pos;

        Parser(String s) {
            this.s = s;
        }

        void whitespace() {
            while (pos < s.length()) {
                char c = s.charAt(pos);
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') {
                    return;
                }
                pos++;
            }
        }

        private IllegalArgumentException error(String what) {
            return new IllegalArgumentException("Malformed JSON at " + pos + ": " + what + ".");
        }

        private char peek() {
            if (pos >= s.length()) {
                throw error("unexpected end");
            }
            return s.charAt(pos);
        }

        Object value() {
            char c = peek();
            switch (c) {
                case '{':
                    return object();
                case '[':
                    return array();
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
            if (!s.startsWith(word, pos)) {
                throw error("expected " + word);
            }
            pos += word.length();
        }

        private Map<String, Object> object() {
            Map<String, Object> map = new LinkedHashMap<>();
            pos++;
            whitespace();
            if (peek() == '}') {
                pos++;
                return map;
            }
            while (true) {
                whitespace();
                if (peek() != '"') {
                    throw error("expected a member name");
                }
                String name = string();
                whitespace();
                if (peek() != ':') {
                    throw error("expected ':'");
                }
                pos++;
                whitespace();
                map.put(name, value());
                whitespace();
                char c = peek();
                pos++;
                if (c == '}') {
                    return map;
                }
                if (c != ',') {
                    throw error("expected ',' or '}'");
                }
            }
        }

        private List<Object> array() {
            List<Object> list = new ArrayList<>();
            pos++;
            whitespace();
            if (peek() == ']') {
                pos++;
                return list;
            }
            while (true) {
                whitespace();
                list.add(value());
                whitespace();
                char c = peek();
                pos++;
                if (c == ']') {
                    return list;
                }
                if (c != ',') {
                    throw error("expected ',' or ']'");
                }
            }
        }

        private String string() {
            pos++;
            StringBuilder sb = new StringBuilder();
            while (true) {
                char c = peek();
                pos++;
                if (c == '"') {
                    return sb.toString();
                }
                if (c != '\\') {
                    sb.append(c);
                    continue;
                }
                char e = peek();
                pos++;
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
                        if (pos + 4 > s.length()) {
                            throw error("truncated escape");
                        }
                        sb.append((char) Integer.parseInt(s.substring(pos, pos + 4), 16));
                        pos += 4;
                        break;
                    default:
                        throw error("bad escape");
                }
            }
        }

        private Object number() {
            int start = pos;
            boolean integral = true;
            while (pos < s.length()) {
                char c = s.charAt(pos);
                if (c == '.' || c == 'e' || c == 'E') {
                    integral = false;
                } else if (!(c == '-' || c == '+' || (c >= '0' && c <= '9'))) {
                    break;
                }
                pos++;
            }
            String text = s.substring(start, pos);
            if (text.isEmpty()) {
                throw error("unexpected character");
            }
            try {
                if (integral) {
                    BigInteger big = new BigInteger(text);
                    return big.bitLength() < 64 ? (Object) big.longValue() : big;
                }
                return Double.parseDouble(text);
            } catch (NumberFormatException e) {
                throw error("bad number '" + text + "'");
            }
        }
    }
}

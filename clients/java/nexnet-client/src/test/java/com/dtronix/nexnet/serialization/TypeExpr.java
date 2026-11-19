package com.dtronix.nexnet.serialization;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** A parsed golden type expression ({@code tests/golden/README.md}, "Types"). */
final class TypeExpr {
    enum Kind {
        /** A name with optional type arguments: {@code int}, {@code List<string>}, {@code InteropBox<int>}. */
        NAMED,
        /** {@code T[]}. */
        ARRAY,
        /** {@code T?}. */
        NULLABLE,
        /** {@code (A,B,...)}. */
        TUPLE,
        /** {@code $0}, {@code $1}... */
        PARAM
    }

    final Kind kind;
    final String name;
    final List<TypeExpr> args;
    final int param;

    private TypeExpr(Kind kind, String name, List<TypeExpr> args, int param) {
        this.kind = kind;
        this.name = name;
        this.args = args;
        this.param = param;
    }

    static TypeExpr named(String name, List<TypeExpr> args) {
        return new TypeExpr(Kind.NAMED, name, Collections.unmodifiableList(args), -1);
    }

    TypeExpr arg(int i) {
        return args.get(i);
    }

    /** Replaces {@code $n} with {@code bindings[n]}. */
    TypeExpr bind(List<TypeExpr> bindings) {
        if (kind == Kind.PARAM) {
            return bindings.get(param);
        }
        if (args.isEmpty()) {
            return this;
        }
        List<TypeExpr> bound = new ArrayList<>();
        for (TypeExpr a : args) {
            bound.add(a.bind(bindings));
        }
        return new TypeExpr(kind, name, Collections.unmodifiableList(bound), param);
    }

    @Override
    public String toString() {
        switch (kind) {
            case ARRAY:
                return args.get(0) + "[]";
            case NULLABLE:
                return args.get(0) + "?";
            case TUPLE:
                return "(" + join(args) + ")";
            case PARAM:
                return "$" + param;
            default:
                return args.isEmpty() ? name : name + "<" + join(args) + ">";
        }
    }

    private static String join(List<TypeExpr> list) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < list.size(); i++) {
            if (i > 0) {
                sb.append(',');
            }
            sb.append(list.get(i));
        }
        return sb.toString();
    }

    static TypeExpr parse(String text) {
        int[] pos = {0};
        TypeExpr result = parse(text, pos);
        if (pos[0] != text.length()) {
            throw new IllegalArgumentException("Bad type expression " + text + " at " + pos[0]);
        }
        return result;
    }

    private static TypeExpr parse(String text, int[] pos) {
        TypeExpr result;
        char c = text.charAt(pos[0]);
        if (c == '(') {
            pos[0]++;
            result = new TypeExpr(Kind.TUPLE, "", Collections.unmodifiableList(list(text, pos, ')')), -1);
        } else if (c == '$') {
            int start = ++pos[0];
            while (pos[0] < text.length() && Character.isDigit(text.charAt(pos[0]))) {
                pos[0]++;
            }
            result = new TypeExpr(Kind.PARAM, "", Collections.<TypeExpr>emptyList(),
                Integer.parseInt(text.substring(start, pos[0])));
        } else {
            int start = pos[0];
            while (pos[0] < text.length() && (Character.isLetterOrDigit(text.charAt(pos[0]))
                || text.charAt(pos[0]) == '_' || text.charAt(pos[0]) == '.')) {
                pos[0]++;
            }
            if (start == pos[0]) {
                throw new IllegalArgumentException("Bad type expression " + text + " at " + start);
            }
            String name = text.substring(start, pos[0]);
            List<TypeExpr> args = new ArrayList<>();
            if (pos[0] < text.length() && text.charAt(pos[0]) == '<') {
                pos[0]++;
                args = list(text, pos, '>');
            }
            result = named(name, args);
        }
        while (pos[0] < text.length()) {
            if (text.startsWith("[]", pos[0])) {
                pos[0] += 2;
                result = new TypeExpr(Kind.ARRAY, "", Collections.singletonList(result), -1);
            } else if (text.charAt(pos[0]) == '?') {
                pos[0]++;
                result = new TypeExpr(Kind.NULLABLE, "", Collections.singletonList(result), -1);
            } else {
                break;
            }
        }
        return result;
    }

    private static List<TypeExpr> list(String text, int[] pos, char close) {
        List<TypeExpr> items = new ArrayList<>();
        while (true) {
            items.add(parse(text, pos));
            char c = text.charAt(pos[0]++);
            if (c == close) {
                return items;
            }
            if (c != ',') {
                throw new IllegalArgumentException("Bad type expression " + text + " at " + (pos[0] - 1));
            }
        }
    }
}

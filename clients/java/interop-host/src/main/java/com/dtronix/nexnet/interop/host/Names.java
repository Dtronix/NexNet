package com.dtronix.nexnet.interop.host;

import java.util.Locale;

/** Converts between Java enum constants ({@code SOCKET_ERROR}) and the C# names of the protocol ({@code SocketError}). */
final class Names {
    private Names() {
    }

    /**
     * The C# name of a Java enum constant.
     *
     * @param value the constant
     * @return the PascalCase name
     */
    static String pascal(Enum<?> value) {
        StringBuilder sb = new StringBuilder();
        for (String part : value.name().split("_")) {
            if (part.isEmpty()) {
                continue;
            }
            sb.append(part.charAt(0)).append(part.substring(1).toLowerCase(Locale.ROOT));
        }
        return sb.toString();
    }

    /**
     * The Java enum constant of a C# name.
     *
     * @param type the enum type
     * @param name the PascalCase name
     * @param <E> the enum type
     * @return the constant
     * @throws HostError (BadRequest) for an unknown name
     */
    static <E extends Enum<E>> E parse(Class<E> type, String name) {
        for (E constant : type.getEnumConstants()) {
            if (pascal(constant).equals(name)) {
                return constant;
            }
        }
        throw HostError.badRequest("Unknown " + type.getSimpleName() + " '" + name + "'.");
    }
}

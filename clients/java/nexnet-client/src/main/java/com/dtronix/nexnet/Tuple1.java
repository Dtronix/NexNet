package com.dtronix.nexnet;

import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * An immutable tuple of 1 element: the Java form of a C# {@code ValueTuple} or {@code Tuple} of the same arity.
 * Equality is element-wise.
 *
 * @param <T1> the type of the first element
 */
public final class Tuple1<T1> {
    private final @Nullable T1 item1;

    /**
     * Creates the tuple.
     *
     * @param item1 the first element
     */
    public Tuple1(@Nullable T1 item1) {
        this.item1 = item1;
    }

    /**
     * Creates the tuple.
     *
     * @param item1 the first element
     * @param <T1> the type of the first element
     * @return the tuple
     */
    public static <T1> Tuple1<T1> of(@Nullable T1 item1) {
        return new Tuple1<>(item1);
    }

    /**
     * The first element.
     *
     * @return the element
     */
    public @Nullable T1 item1() {
        return item1;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof Tuple1)) {
            return false;
        }
        Tuple1<?> other = (Tuple1<?>) obj;
        return Objects.equals(item1, other.item1);
    }

    @Override
    public int hashCode() {
        return Objects.hash(item1);
    }

    @Override
    public String toString() {
        return "(" + item1 + ")";
    }
}

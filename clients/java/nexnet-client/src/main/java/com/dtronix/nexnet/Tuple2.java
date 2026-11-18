package com.dtronix.nexnet;

import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * An immutable tuple of 2 elements: the Java form of a C# {@code ValueTuple} or {@code Tuple} of the same arity.
 * Equality is element-wise.
 *
 * @param <T1> the type of the first element
 * @param <T2> the type of the second element
 */
public final class Tuple2<T1, T2> {
    private final @Nullable T1 item1;
    private final @Nullable T2 item2;

    /**
     * Creates the tuple.
     *
     * @param item1 the first element
     * @param item2 the second element
     */
    public Tuple2(@Nullable T1 item1, @Nullable T2 item2) {
        this.item1 = item1;
        this.item2 = item2;
    }

    /**
     * Creates the tuple.
     *
     * @param item1 the first element
     * @param item2 the second element
     * @param <T1> the type of the first element
     * @param <T2> the type of the second element
     * @return the tuple
     */
    public static <T1, T2> Tuple2<T1, T2> of(@Nullable T1 item1, @Nullable T2 item2) {
        return new Tuple2<>(item1, item2);
    }

    /**
     * The first element.
     *
     * @return the element
     */
    public @Nullable T1 item1() {
        return item1;
    }

    /**
     * The second element.
     *
     * @return the element
     */
    public @Nullable T2 item2() {
        return item2;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof Tuple2)) {
            return false;
        }
        Tuple2<?, ?> other = (Tuple2<?, ?>) obj;
        return Objects.equals(item1, other.item1)
            && Objects.equals(item2, other.item2);
    }

    @Override
    public int hashCode() {
        return Objects.hash(item1, item2);
    }

    @Override
    public String toString() {
        return "(" + item1 + ", " + item2 + ")";
    }
}

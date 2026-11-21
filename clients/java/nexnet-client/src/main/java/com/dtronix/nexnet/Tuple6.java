package com.dtronix.nexnet;

import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * An immutable tuple of 6 elements: the Java form of a C# {@code ValueTuple} or {@code Tuple} of the same arity.
 * Equality is element-wise.
 *
 * @param <T1> the type of the first element
 * @param <T2> the type of the second element
 * @param <T3> the type of the third element
 * @param <T4> the type of the fourth element
 * @param <T5> the type of the fifth element
 * @param <T6> the type of the sixth element
 */
public final class Tuple6<T1, T2, T3, T4, T5, T6> {
    private final @Nullable T1 item1;
    private final @Nullable T2 item2;
    private final @Nullable T3 item3;
    private final @Nullable T4 item4;
    private final @Nullable T5 item5;
    private final @Nullable T6 item6;

    /**
     * Creates the tuple.
     *
     * @param item1 the first element
     * @param item2 the second element
     * @param item3 the third element
     * @param item4 the fourth element
     * @param item5 the fifth element
     * @param item6 the sixth element
     */
    public Tuple6(@Nullable T1 item1, @Nullable T2 item2, @Nullable T3 item3, @Nullable T4 item4, @Nullable T5 item5, @Nullable T6 item6) {
        this.item1 = item1;
        this.item2 = item2;
        this.item3 = item3;
        this.item4 = item4;
        this.item5 = item5;
        this.item6 = item6;
    }

    /**
     * Creates the tuple.
     *
     * @param item1 the first element
     * @param item2 the second element
     * @param item3 the third element
     * @param item4 the fourth element
     * @param item5 the fifth element
     * @param item6 the sixth element
     * @param <T1> the type of the first element
     * @param <T2> the type of the second element
     * @param <T3> the type of the third element
     * @param <T4> the type of the fourth element
     * @param <T5> the type of the fifth element
     * @param <T6> the type of the sixth element
     * @return the tuple
     */
    public static <T1, T2, T3, T4, T5, T6> Tuple6<T1, T2, T3, T4, T5, T6> of(@Nullable T1 item1, @Nullable T2 item2, @Nullable T3 item3, @Nullable T4 item4, @Nullable T5 item5, @Nullable T6 item6) {
        return new Tuple6<>(item1, item2, item3, item4, item5, item6);
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

    /**
     * The third element.
     *
     * @return the element
     */
    public @Nullable T3 item3() {
        return item3;
    }

    /**
     * The fourth element.
     *
     * @return the element
     */
    public @Nullable T4 item4() {
        return item4;
    }

    /**
     * The fifth element.
     *
     * @return the element
     */
    public @Nullable T5 item5() {
        return item5;
    }

    /**
     * The sixth element.
     *
     * @return the element
     */
    public @Nullable T6 item6() {
        return item6;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof Tuple6)) {
            return false;
        }
        Tuple6<?, ?, ?, ?, ?, ?> other = (Tuple6<?, ?, ?, ?, ?, ?>) obj;
        return Objects.equals(item1, other.item1)
            && Objects.equals(item2, other.item2)
            && Objects.equals(item3, other.item3)
            && Objects.equals(item4, other.item4)
            && Objects.equals(item5, other.item5)
            && Objects.equals(item6, other.item6);
    }

    @Override
    public int hashCode() {
        return Objects.hash(item1, item2, item3, item4, item5, item6);
    }

    @Override
    public String toString() {
        return "(" + item1 + ", " + item2 + ", " + item3 + ", " + item4 + ", " + item5 + ", " + item6 + ")";
    }
}

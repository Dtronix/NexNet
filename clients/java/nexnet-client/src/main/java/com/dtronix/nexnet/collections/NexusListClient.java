package com.dtronix.nexnet.collections;

import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.invocation.InvocationRuntime;
import com.dtronix.nexnet.invocation.PayloadCodec;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.pipes.NexusChannelReader;
import com.dtronix.nexnet.pipes.NexusChannelWriter;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.PipeManager;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.session.SerialExecutor;
import java.util.AbstractList;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.Comparator;
import java.util.Iterator;
import java.util.List;
import java.util.ListIterator;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Consumer;
import java.util.function.Predicate;
import java.util.function.Supplier;
import java.util.function.UnaryOperator;
import org.jspecify.annotations.Nullable;

/**
 * The client replica of a server {@code INexusList<T>} (protocol specification section 12). One instance lasts as long
 * as the client; each {@link #enable()} opens a new collection pipe on the current session and receives a full reset.
 *
 * <p>The server is authoritative. Mutations are sent as requests (BiDirectional mode only), at most one in flight,
 * and the replica changes only when the server streams the change back; the request then completes with true (its
 * Ack), or with false on a Noop, a change that fails validation, or a closed pipe. Changes that arrive before or during
 * the reset are buffered and replayed after it, dropping stale ones ({@code version > 0 && version <= current}).
 *
 * <p>Items are processed on a serial lane, never on the session's reader thread. Change listeners, and every future
 * this class returns, complete in order on the callback executor. The {@link List} view reads an immutable snapshot
 * and is safe to read from any thread; its mutators throw {@link UnsupportedOperationException}.
 *
 * <p>Collections are not resumed after a reconnect: the session's end disables the list, and the application enables
 * it again.
 *
 * @param <T> the item type
 */
public final class NexusListClient<T> extends AbstractList<T> implements NexusList<T> {
    private static final int READ_BATCH = 64;
    /** The C# client caps the initial capacity of the reset buffer; the count comes from the wire. */
    private static final int MAX_INITIAL_CAPACITY = 1024;

    /** The contents and version, replaced as a whole. */
    private static final class Snapshot<T> {
        final List<T> items;
        final int version;

        Snapshot(List<T> items, int version) {
            this.items = items;
            this.version = version;
        }
    }

    /** Builds the bytes of a request from a snapshot, or null to complete with false and send nothing. */
    private interface Request<T> {
        byte @Nullable [] build(Snapshot<T> snapshot, PayloadCodec codec);
    }

    /** One enable: its pipe, futures and reset state. Lane-only fields are touched on {@link #lane} only. */
    private final class Run {
        final NexusDuplexChannel<byte[]> channel;
        final PayloadCodec codec;
        final CompletableFuture<Boolean> enabled = new CompletableFuture<>();
        final CompletableFuture<Void> ready = new CompletableFuture<>();
        final CompletableFuture<Void> disabled = new CompletableFuture<>();
        final AtomicReference<@Nullable CompletableFuture<Boolean>> pending = new AtomicReference<>();
        volatile @Nullable NexusChannelWriter<byte[]> writer;
        /** Set once on the lane when the run ends; no item of the run is processed after it. */
        volatile boolean closed;
        // Lane-only.
        boolean stopped;
        boolean synced;
        @Nullable List<@Nullable T> resetting;
        int resetTotal;
        int resetVersion;
        @Nullable List<CollectionWire.Message<T>> buffer;

        Run(NexusDuplexChannel<byte[]> channel, PayloadCodec codec) {
            this.channel = channel;
            this.codec = codec;
        }
    }

    private final int collectionId;
    private final NexusCollectionMode mode;
    private final Formatter<T> itemFormatter;
    private final Supplier<@Nullable InvocationRuntime> runtimes;
    private final SerialExecutor callbacks;
    private final SerialExecutor lane = new SerialExecutor(NexusExecutors.shared());
    private final @Nullable NexusLogger logger;
    private final CopyOnWriteArrayList<Consumer<NexusListChange<T>>> listeners = new CopyOnWriteArrayList<>();

    private final Object lock = new Object();
    private volatile Snapshot<T> snapshot = new Snapshot<>(Collections.<T>emptyList(), 0);
    private volatile NexusCollectionState state = NexusCollectionState.DISCONNECTED;
    private @Nullable Run run;
    private CompletableFuture<Void> ready = CompletableFuture.completedFuture(null);
    private CompletableFuture<?> operations = CompletableFuture.completedFuture(null);

    /**
     * Creates the list of one collection of a client's server nexus.
     *
     * @param collectionId the collection id
     * @param mode the collection's mode, from the server nexus's {@code [NexusCollection]} attribute
     * @param itemFormatter the item formatter
     * @param runtimes the invocation runtime of the current session, or null while not connected; read at each
     *     {@link #enable()}
     * @param callbackExecutor the executor that runs change listeners and completes futures
     * @param logger the logger, or null
     */
    public NexusListClient(int collectionId, NexusCollectionMode mode, Formatter<T> itemFormatter,
                           Supplier<@Nullable InvocationRuntime> runtimes, Executor callbackExecutor,
                           @Nullable NexusLogger logger) {
        if (collectionId < 0 || collectionId > 0xFFFF) {
            throw new IllegalArgumentException("collectionId out of range: " + collectionId);
        }
        if (mode == null || itemFormatter == null || runtimes == null || callbackExecutor == null) {
            throw new NullPointerException("An argument is null.");
        }
        this.collectionId = collectionId;
        this.mode = mode;
        this.itemFormatter = itemFormatter;
        this.runtimes = runtimes;
        this.callbacks = new SerialExecutor(callbackExecutor);
        this.logger = logger;
    }

    /**
     * The collection id.
     *
     * @return the id
     */
    public int collectionId() {
        return collectionId;
    }

    /**
     * The mode.
     *
     * @return the mode
     */
    public NexusCollectionMode mode() {
        return mode;
    }

    /**
     * The version of the replica: the server's version of the last applied change, or 0 while disabled.
     *
     * @return the version
     */
    public int version() {
        return snapshot.version;
    }

    // ------------------------------------------------------------------ lifecycle

    @Override
    public CompletableFuture<Boolean> enable() {
        Run created;
        PipeManager pipes;
        synchronized (lock) {
            Run current = run;
            if (current != null) {
                // Opening or open: the same enable.
                return view(current.enabled);
            }
            InvocationRuntime runtime = runtimes.get();
            pipes = runtime == null ? null : runtime.pipes();
            if (runtime == null || pipes == null) {
                return failed(new IllegalStateException("The client is not connected."));
            }
            NexusDuplexChannel<byte[]> channel;
            try {
                channel = pipes.createChannel(CollectionWire.RAW);
            } catch (RuntimeException e) {
                return failed(e);
            }
            created = new Run(channel, runtime.codec());
            run = created;
            ready = created.ready;
        }
        log(NexusLogLevel.DEBUG, "Connecting collection " + collectionId + ".", null);
        try {
            pipes.invokeChannel(collectionId, created.channel, CollectionWire.PIPE_ARGUMENT);
        } catch (RuntimeException e) {
            lane.execute(() -> openFailed(created, e));
            return view(created.enabled);
        }
        // Ready, or Refused (NexusBusyException), or the session ended first.
        created.channel.pipe().ready().whenComplete((v, error) -> lane.execute(() -> {
            if (error != null) {
                openFailed(created, unwrap(error));
            } else {
                opened(created);
            }
        }));
        return view(created.enabled);
    }

    @Override
    public CompletableFuture<Void> disable() {
        Run current;
        synchronized (lock) {
            current = run;
        }
        if (current == null) {
            return CompletableFuture.completedFuture(null);
        }
        // Completing the pipe runs the normal disconnect path when the server has closed it too.
        current.channel.close();
        return view(current.disabled);
    }

    @Override
    public CompletableFuture<Void> ready() {
        synchronized (lock) {
            return view(ready);
        }
    }

    @Override
    public CompletableFuture<Void> disabled() {
        synchronized (lock) {
            return run == null ? CompletableFuture.completedFuture(null) : view(run.disabled);
        }
    }

    @Override
    public NexusCollectionState state() {
        return state;
    }

    @Override
    public Registration addChangeListener(Consumer<NexusListChange<T>> listener) {
        if (listener == null) {
            throw new NullPointerException("listener");
        }
        // Each registration is its own entry, so adding a listener twice delivers twice and closes independently.
        Consumer<NexusListChange<T>> entry = listener::accept;
        listeners.add(entry);
        return () -> listeners.remove(entry);
    }

    /** Lane: the enable failed before the pipe opened. No events: the list never left Disabled. */
    private void openFailed(Run failed, Throwable error) {
        if (failed.stopped) {
            return;
        }
        failed.stopped = true;
        failed.closed = true;
        synchronized (lock) {
            if (run == failed) {
                run = null;
            }
        }
        failed.channel.close();
        log(NexusLogLevel.DEBUG, "Collection " + collectionId + " could not be enabled.", error);
        IllegalStateException notReady =
            new IllegalStateException("The collection could not be enabled: " + error.getMessage(), error);
        callbacks.execute(() -> {
            failed.ready.completeExceptionally(notReady);
            failed.enabled.completeExceptionally(error);
            failed.disabled.complete(null);
        });
    }

    /** Lane: the pipe is open; start reading items. */
    private void opened(Run opened) {
        if (opened.stopped) {
            return;
        }
        if (mode.isWritable()) {
            opened.writer = opened.channel.writer().getNow(null);
        }
        NexusChannelReader<byte[]> reader = opened.channel.reader().getNow(null);
        // Registered once the run's state is in place, so a pipe already closed still runs the full disconnect.
        opened.channel.pipe().completed().whenComplete((v, e) -> lane.execute(() -> disconnected(opened)));
        if (reader == null) {
            closeRun(opened);
            return;
        }
        readNext(opened, reader);
    }

    private void readNext(Run reading, NexusChannelReader<byte[]> reader) {
        reader.readBatch(READ_BATCH).whenComplete((items, error) -> lane.execute(() -> {
            if (reading.stopped) {
                // Items still buffered in a closed pipe never reach the state.
                return;
            }
            if (error != null) {
                log(NexusLogLevel.INFORMATION, "Error while reading collection " + collectionId + "; closing its pipe.",
                    unwrap(error));
                closeRun(reading);
                return;
            }
            if (items.isEmpty()) {
                // The server completed its output: make sure the pipe completes on this side too.
                closeRun(reading);
                return;
            }
            for (byte[] item : items) {
                if (!process(reading, item)) {
                    log(NexusLogLevel.INFORMATION, "Protocol error on collection " + collectionId + "; closing its pipe.",
                        null);
                    closeRun(reading);
                    return;
                }
                if (reading.stopped) {
                    return;
                }
            }
            readNext(reading, reader);
        }));
    }

    /** Lane: stops processing the run's items and closes its pipe; the pipe's completion runs the disconnect. */
    private void closeRun(Run closing) {
        closing.stopped = true;
        closing.channel.close();
    }

    /** Lane: the run's pipe completed (either side closed it, or the session ended). */
    private void disconnected(Run ended) {
        if (ended.closed) {
            return;
        }
        ended.stopped = true;
        ended.closed = true;
        synchronized (lock) {
            if (run == ended) {
                run = null;
            }
        }
        ended.channel.close();
        state = NexusCollectionState.DISCONNECTED;
        snapshot = new Snapshot<>(Collections.<T>emptyList(), 0);
        ended.resetting = null;
        ended.buffer = null;
        completePending(ended, false);
        emit(new NexusListChange<>(NexusListChange.Action.RESET, -1, -1, null, null));
        IllegalStateException notReady = new IllegalStateException("The collection disconnected before its initial "
            + "synchronization completed. Enable it again to resynchronize.");
        callbacks.execute(() -> {
            ended.ready.completeExceptionally(notReady);
            ended.disabled.complete(null);
            ended.enabled.complete(false);
        });
    }

    // ------------------------------------------------------------------ items (lane)

    /** Processes one item; false is a protocol error that closes the pipe. */
    private boolean process(Run current, byte[] item) {
        CollectionWire.Message<T> message;
        try {
            message = CollectionWire.decode(item, current.codec, itemFormatter);
        } catch (RuntimeException e) {
            log(NexusLogLevel.WARNING, "A collection item does not decode.", e);
            return false;
        }
        try {
            return apply(current, message);
        } catch (RuntimeException e) {
            log(NexusLogLevel.WARNING, "Exception while processing " + message + ".", e);
            return false;
        }
    }

    private boolean apply(Run current, CollectionWire.Message<T> message) {
        switch (message.tag) {
            case CollectionWire.RESET_START: {
                if (current.resetting != null) {
                    return protocolError("ResetStart while already resetting.");
                }
                int total = message.index;
                if (total < 0) {
                    return protocolError("ResetStart with an invalid total value count " + total + ".");
                }
                current.resetting = new ArrayList<>(Math.min(total, MAX_INITIAL_CAPACITY));
                current.resetTotal = total;
                current.resetVersion = message.version;
                // Changes buffered before the reset started are kept and replayed after it.
                if (current.buffer == null) {
                    current.buffer = new ArrayList<>();
                }
                return true;
            }
            case CollectionWire.RESET_VALUES: {
                List<@Nullable T> resetting = current.resetting;
                if (resetting == null) {
                    return protocolError("ResetValues while not resetting.");
                }
                List<@Nullable T> values = message.values;
                if (values != null) {
                    if (values.size() > current.resetTotal - resetting.size()) {
                        return protocolError("More reset values than the " + current.resetTotal + " announced.");
                    }
                    resetting.addAll(values);
                }
                return true;
            }
            case CollectionWire.RESET_COMPLETE:
                return completeReset(current);
            default:
                break;
        }
        if (current.resetting != null || !current.synced) {
            // Resetting, or the reset has not started yet: buffer, and replay after the reset (stale rule).
            List<CollectionWire.Message<T>> buffer = current.buffer;
            if (buffer == null) {
                buffer = new ArrayList<>();
                current.buffer = buffer;
            }
            buffer.add(message);
            return true;
        }
        return applyChange(current, message);
    }

    private boolean completeReset(Run current) {
        List<@Nullable T> resetting = current.resetting;
        if (resetting == null) {
            return protocolError("ResetComplete while not resetting.");
        }
        if (resetting.size() != current.resetTotal) {
            return protocolError("Reset completed with " + resetting.size() + " values, but " + current.resetTotal
                + " were announced.");
        }
        List<CollectionWire.Message<T>> buffered = current.buffer;
        @SuppressWarnings("unchecked")
        List<T> items = (List<T>) (List<?>) Collections.unmodifiableList(resetting);
        snapshot = new Snapshot<>(items, current.resetVersion);
        current.resetting = null;
        current.buffer = null;
        current.synced = true;
        if (buffered != null) {
            for (CollectionWire.Message<T> message : buffered) {
                if (!applyChange(current, message)) {
                    return false;
                }
            }
        }
        emit(new NexusListChange<>(NexusListChange.Action.RESET, -1, -1, null, null));
        state = NexusCollectionState.CONNECTED;
        emit(new NexusListChange<>(NexusListChange.Action.READY, -1, -1, null, null));
        callbacks.execute(() -> {
            current.ready.complete(null);
            current.enabled.complete(true);
        });
        return true;
    }

    /** Applies a change in Ready (specification section 12.4). */
    private boolean applyChange(Run current, CollectionWire.Message<T> message) {
        if (message.tag == CollectionWire.NOOP) {
            // The server made the pending request obsolete. A Noop with no request pending is ignored.
            completePending(current, false);
            return true;
        }
        Snapshot<T> s = snapshot;
        if (message.version > 0 && message.version <= s.version) {
            // Stale: already in the reset snapshot. Nothing is raised.
            return true;
        }
        List<T> items = s.items;
        int n = items.size();
        List<T> next;
        NexusListChange<T> change;
        switch (message.tag) {
            case CollectionWire.CLEAR:
                next = Collections.emptyList();
                change = new NexusListChange<>(NexusListChange.Action.RESET, -1, -1, null, null);
                break;
            case CollectionWire.INSERT:
                if (message.index < 0 || message.index > n) {
                    return invalid(current, message, n);
                }
                next = new ArrayList<>(n + 1);
                next.addAll(items);
                next.add(message.index, message.value);
                change = new NexusListChange<>(NexusListChange.Action.ADD, message.index, -1, message.value, null);
                break;
            case CollectionWire.REPLACE: {
                if (message.index < 0 || message.index >= n) {
                    return invalid(current, message, n);
                }
                next = new ArrayList<>(items);
                T old = next.set(message.index, message.value);
                change = new NexusListChange<>(NexusListChange.Action.REPLACE, message.index, -1, message.value, old);
                break;
            }
            case CollectionWire.MOVE: {
                int from = message.index;
                int to = message.toIndex;
                if (from < 0 || from >= n || to < 0 || to >= n || from == to) {
                    return invalid(current, message, n);
                }
                next = new ArrayList<>(items);
                T moved = next.remove(from);
                next.add(to, moved);
                change = new NexusListChange<>(NexusListChange.Action.MOVE, to, from, moved, null);
                break;
            }
            case CollectionWire.REMOVE: {
                if (message.index < 0 || message.index >= n) {
                    return invalid(current, message, n);
                }
                next = new ArrayList<>(items);
                T old = next.remove(message.index);
                change = new NexusListChange<>(NexusListChange.Action.REMOVE, -1, message.index, null, old);
                break;
            }
            default:
                return protocolError("Unexpected collection item " + message + ".");
        }
        snapshot = new Snapshot<>(Collections.unmodifiableList(next), message.version);
        emit(change);
        if (message.isAck()) {
            completePending(current, true);
        }
        return true;
    }

    private boolean invalid(Run current, CollectionWire.Message<T> message, int count) {
        if (message.isAck()) {
            completePending(current, false);
        }
        return protocolError(message + " is not valid for a list of " + count + " items.");
    }

    private boolean protocolError(String reason) {
        log(NexusLogLevel.WARNING, reason, null);
        return false;
    }

    private void completePending(Run current, boolean value) {
        CompletableFuture<Boolean> pending = current.pending.getAndSet(null);
        if (pending != null) {
            callbacks.execute(() -> pending.complete(value));
        }
    }

    private void emit(NexusListChange<T> change) {
        callbacks.execute(() -> {
            for (Consumer<NexusListChange<T>> listener : listeners) {
                try {
                    listener.accept(change);
                } catch (RuntimeException e) {
                    log(NexusLogLevel.ERROR, "A collection change listener failed.", e);
                }
            }
        });
    }

    // ------------------------------------------------------------------ mutations

    @Override
    public CompletableFuture<Boolean> addAsync(@Nullable T item) {
        // Index and version from the same snapshot.
        return mutate((s, codec) -> CollectionWire.valueRequest(CollectionWire.INSERT, s.version, s.items.size(),
            item, codec, itemFormatter));
    }

    @Override
    public CompletableFuture<Boolean> insertAsync(int index, @Nullable T item) {
        if (index < 0) {
            return failed(new IndexOutOfBoundsException("index: " + index));
        }
        return mutate((s, codec) -> CollectionWire.valueRequest(CollectionWire.INSERT, s.version, index, item, codec,
            itemFormatter));
    }

    @Override
    public CompletableFuture<Boolean> removeAsync(@Nullable T item) {
        return mutate((s, codec) -> {
            int index = s.items.indexOf(item);
            return index < 0 ? null : CollectionWire.removeRequest(s.version, index);
        });
    }

    @Override
    public CompletableFuture<Boolean> removeAtAsync(int index) {
        if (index < 0) {
            return failed(new IndexOutOfBoundsException("index: " + index));
        }
        return mutate((s, codec) -> CollectionWire.removeRequest(s.version, index));
    }

    @Override
    public CompletableFuture<Boolean> replaceAsync(int index, @Nullable T item) {
        if (index < 0) {
            return failed(new IndexOutOfBoundsException("index: " + index));
        }
        return mutate((s, codec) -> CollectionWire.valueRequest(CollectionWire.REPLACE, s.version, index, item, codec,
            itemFormatter));
    }

    @Override
    public CompletableFuture<Boolean> moveAsync(int fromIndex, int toIndex) {
        if (fromIndex < 0 || toIndex < 0) {
            return failed(new IndexOutOfBoundsException("fromIndex: " + fromIndex + ", toIndex: " + toIndex));
        }
        return mutate((s, codec) -> CollectionWire.moveRequest(s.version, fromIndex, toIndex));
    }

    @Override
    public CompletableFuture<Boolean> clearAsync() {
        return mutate((s, codec) -> CollectionWire.clearRequest(s.version));
    }

    /**
     * Queues a request behind the one in flight. It is built from the snapshot current when its turn comes, once the
     * list is synchronized, and completes with the server's answer.
     */
    private CompletableFuture<Boolean> mutate(Request<T> request) {
        if (!mode.isWritable()) {
            return failed(new IllegalStateException("The collection is read-only (mode " + mode + ")."));
        }
        CompletableFuture<Boolean> result = new CompletableFuture<>();
        synchronized (lock) {
            Run current = run;
            if (current == null) {
                return failed(new IllegalStateException("The collection is not enabled."));
            }
            CompletableFuture<?> previous = operations;
            operations = result;
            previous.handle((v, e) -> current.ready).thenCompose(r -> r).whenComplete((v, notReady) -> {
                if (notReady != null) {
                    // The run ended before its reset completed.
                    callbacks.execute(() -> result.complete(false));
                } else {
                    send(current, request, result);
                }
            });
        }
        return view(result);
    }

    private void send(Run current, Request<T> request, CompletableFuture<Boolean> result) {
        NexusChannelWriter<byte[]> writer = current.writer;
        if (current.closed || writer == null) {
            callbacks.execute(() -> result.complete(false));
            return;
        }
        byte[] bytes;
        try {
            bytes = request.build(snapshot, current.codec);
        } catch (RuntimeException e) {
            callbacks.execute(() -> result.completeExceptionally(e));
            return;
        }
        if (bytes == null) {
            callbacks.execute(() -> result.complete(false));
            return;
        }
        current.pending.set(result);
        if (current.closed) {
            // The run ended while this request was being built: it completes with false, once.
            if (current.pending.compareAndSet(result, null)) {
                callbacks.execute(() -> result.complete(false));
            }
            return;
        }
        writer.write(bytes).whenComplete((v, error) -> {
            if (error == null) {
                return;
            }
            Throwable cause = unwrap(error);
            if (current.pending.compareAndSet(result, null)) {
                // Too large is the caller's error; anything else is the pipe closing under the request.
                if (cause instanceof NexusMessageTooLargeException) {
                    callbacks.execute(() -> result.completeExceptionally(cause));
                } else {
                    callbacks.execute(() -> result.complete(false));
                }
            }
        });
    }

    // ------------------------------------------------------------------ read-only List view

    private List<T> items() {
        return snapshot.items;
    }

    @Override
    public T get(int index) {
        return items().get(index);
    }

    @Override
    public int size() {
        return items().size();
    }

    @Override
    public boolean isEmpty() {
        return items().isEmpty();
    }

    @Override
    public boolean contains(@Nullable Object o) {
        return items().contains(o);
    }

    @Override
    public int indexOf(@Nullable Object o) {
        return items().indexOf(o);
    }

    @Override
    public int lastIndexOf(@Nullable Object o) {
        return items().lastIndexOf(o);
    }

    @Override
    public Iterator<T> iterator() {
        return items().iterator();
    }

    @Override
    public ListIterator<T> listIterator() {
        return items().listIterator();
    }

    @Override
    public ListIterator<T> listIterator(int index) {
        return items().listIterator(index);
    }

    @Override
    public List<T> subList(int fromIndex, int toIndex) {
        return items().subList(fromIndex, toIndex);
    }

    @Override
    public Object[] toArray() {
        return items().toArray();
    }

    @Override
    public <A> A[] toArray(A[] a) {
        return items().toArray(a);
    }

    @Override
    public boolean equals(@Nullable Object o) {
        return o == this || items().equals(o);
    }

    @Override
    public int hashCode() {
        return items().hashCode();
    }

    @Override
    public String toString() {
        return items().toString();
    }

    private static UnsupportedOperationException readOnly() {
        return new UnsupportedOperationException(
            "A NexusList is read-only; use the asynchronous methods (addAsync, removeAsync, ...) to change it.");
    }

    @Override
    public boolean add(T t) {
        throw readOnly();
    }

    @Override
    public void add(int index, T element) {
        throw readOnly();
    }

    @Override
    public T set(int index, T element) {
        throw readOnly();
    }

    @Override
    public T remove(int index) {
        throw readOnly();
    }

    @Override
    public boolean remove(@Nullable Object o) {
        throw readOnly();
    }

    @Override
    public boolean addAll(Collection<? extends T> c) {
        throw readOnly();
    }

    @Override
    public boolean addAll(int index, Collection<? extends T> c) {
        throw readOnly();
    }

    @Override
    public boolean removeAll(Collection<?> c) {
        throw readOnly();
    }

    @Override
    public boolean retainAll(Collection<?> c) {
        throw readOnly();
    }

    @Override
    public boolean removeIf(Predicate<? super T> filter) {
        throw readOnly();
    }

    @Override
    public void replaceAll(UnaryOperator<T> operator) {
        throw readOnly();
    }

    @Override
    public void sort(@Nullable Comparator<? super T> c) {
        throw readOnly();
    }

    @Override
    public void clear() {
        throw readOnly();
    }

    @Override
    protected void removeRange(int fromIndex, int toIndex) {
        throw readOnly();
    }

    // ------------------------------------------------------------------ helpers

    /** A future the caller can't complete; it follows {@code source}. */
    private static <R> CompletableFuture<R> view(CompletableFuture<R> source) {
        return source.thenApply(r -> r);
    }

    private <R> CompletableFuture<R> failed(Throwable error) {
        CompletableFuture<R> future = new CompletableFuture<>();
        callbacks.execute(() -> future.completeExceptionally(error));
        return future;
    }

    private static Throwable unwrap(Throwable error) {
        Throwable current = error;
        while ((current instanceof CompletionException || current instanceof ExecutionException)
            && current.getCause() != null) {
            current = current.getCause();
        }
        return current;
    }

    private void log(NexusLogLevel level, String message, @Nullable Throwable exception) {
        NexusLogger current = logger;
        if (current != null && current.isEnabled(level)) {
            try {
                current.log(level, "Collection" + collectionId, exception, message);
            } catch (RuntimeException ignored) {
                // A failing logger must not break the collection.
            }
        }
    }

}

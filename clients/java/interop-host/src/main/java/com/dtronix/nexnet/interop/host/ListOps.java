package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.collections.NexusListChange;
import com.dtronix.nexnet.collections.NexusListClient;
import com.dtronix.nexnet.testnexus.scaffolding.ScaffoldingExport;
import com.dtronix.nexnet.testnexus.scaffolding.TypedCodec;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Function;

/**
 * The {@code list.*} ops (control protocol §6.10) of one handle: a client of {@code client.create}, or a collection a
 * pool's collection connector returned ({@code pool.collection}, §6.11). Owns the handle's watches and throttles.
 */
final class ListOps {
    /** The message of the exception a {@code list.watch} listener with {@code throws} raises. */
    static final String THROWING_WATCH_MESSAGE = "A scripted list change handler threw (list.watch throws).";

    private final InteropHost host;
    private final Scope scope;
    private final String handle;
    private final ScaffoldingExport<?> export;
    private final Function<String, NexusList<Object>> resolver;
    private final Map<String, Registration> watches = new ConcurrentHashMap<>();
    /** {@code list.throttle} slow consumers, by collection name (§6.10). */
    private final Map<String, ListThrottle> throttles = new ConcurrentHashMap<>();

    /**
     * @param host the host
     * @param scope the handle's scope
     * @param handle the handle {@code list.changed} events carry
     * @param export the nexus export
     * @param resolver the handle's collection of a name; throws {@link HostError} for a collection it does not have
     */
    ListOps(InteropHost host, Scope scope, String handle, ScaffoldingExport<?> export,
            Function<String, NexusList<Object>> resolver) {
        this.host = host;
        this.scope = scope;
        this.handle = handle;
        this.export = export;
        this.resolver = resolver;
    }

    /** Detaches every watch and stops every throttle (the handle is released). */
    void close() {
        for (Registration watch : watches.values()) {
            watch.close();
        }
        watches.clear();
        for (ListThrottle throttle : throttles.values()) {
            throttle.stop();
        }
        throttles.clear();
    }

    /** The watches not ended by {@code list.unwatch} (§5.1 subscriptions). */
    int watchCount() {
        return watches.size();
    }

    private NexusList<Object> list(Args args) {
        return resolver.apply(args.obj("collection").str("name"));
    }

    private TypedCodec<Object> itemCodec(Args args) {
        String name = args.obj("collection").str("name");
        ScaffoldingExport.Collection collection = export.collection(name);
        if (collection == null) {
            throw HostError.badRequest("Unknown collection '" + name + "' of " + export + ".");
        }
        return PipeEntry.codec(collection.item());
    }

    CompletableFuture<Map<String, Object>> listEnable(Args args, CancellationToken token) {
        NexusList<Object> list = list(args);
        CompletableFuture<Boolean> enabled = list.enable().thenCompose(ok -> ok
            ? list.ready().thenApply(v -> true)
            : CompletableFuture.completedFuture(false));
        return Async.withToken(enabled, token).thenApply(ok -> Protocol.map("ok", ok));
    }

    CompletableFuture<Map<String, Object>> listDisable(Args args) {
        return list(args).disable().thenApply(v -> Protocol.empty());
    }

    CompletableFuture<Map<String, Object>> listOp(Args args) {
        NexusList<Object> list = list(args);
        TypedCodec<Object> codec = itemCodec(args);
        CompletableFuture<Boolean> result;
        switch (args.str("op")) {
            case "add":
                result = list.addAsync(codec.fromJson(args.value("item")));
                break;
            case "insert":
                result = list.insertAsync(args.int32("index"), codec.fromJson(args.value("item")));
                break;
            case "remove":
                result = list.removeAsync(codec.fromJson(args.value("item")));
                break;
            case "removeAt":
                result = list.removeAtAsync(args.int32("index"));
                break;
            case "replace":
                result = list.replaceAsync(args.int32("index"), codec.fromJson(args.value("item")));
                break;
            case "move":
                result = list.moveAsync(args.int32("from"), args.int32("to"));
                break;
            case "clear":
                result = list.clearAsync();
                break;
            default:
                throw HostError.badRequest("Unknown list op '" + args.str("op") + "'.");
        }
        if (args.bool("count")) {
            // Read the moment the op completes: every change the server sent before its answer is applied by then (§6.10).
            return result.thenApply(ok -> Protocol.map("ok", ok, "count", list.size()));
        }
        return result.thenApply(ok -> Protocol.map("ok", ok));
    }

    Map<String, Object> listSnapshot(Args args) {
        NexusList<Object> list = list(args);
        TypedCodec<Object> codec = itemCodec(args);
        List<Object> items = new ArrayList<>();
        for (Object item : list.toArray()) {
            items.add(codec.toJson(item));
        }
        return Protocol.map("items", items);
    }

    /** {@code list.state} (§6.10): the mode, state and disabled signal of the collection. */
    Map<String, Object> listState(Args args) {
        NexusList<Object> list = list(args);
        if (!(list instanceof NexusListClient)) {
            throw new HostError(HostError.INVALID_OPERATION, "The collection is not a runtime list; its mode is unknown.");
        }
        // The Java list has no read-only flag (C# IsReadOnly): its mode tells whether the client can change it.
        return Protocol.map(
            "mode", Names.pascal(((NexusListClient<?>) list).mode()),
            "state", Names.pascal(list.state()),
            "disabled", list.disabled().isDone());
    }

    /** {@code list.throttle} (§6.10): installs, replaces or removes the list's slow consumer. */
    Map<String, Object> listThrottle(Args args) {
        NexusListClient<?> list = runtimeList(args);
        String name = args.obj("collection").str("name");
        int every = optInt(args, "every", 1, 1);
        int delayMs = optInt(args, "delayMs", 0, 0);
        int firstDelayMs = optInt(args, "firstDelayMs", 0, 0);
        boolean pause = args.bool("pause");
        // A new throttle replaces the last one, and releases the message it holds.
        ListThrottle previous = throttles.remove(name);
        if (previous != null) {
            previous.stop();
        }
        if (!ListThrottle.isActive(delayMs, firstDelayMs, pause)) {
            list.setProcessTestHook(null);
            return Protocol.empty();
        }
        ListThrottle throttle = new ListThrottle(host.timer, every, delayMs, firstDelayMs, pause);
        throttles.put(name, throttle);
        list.setProcessTestHook(throttle);
        return Protocol.empty();
    }

    /** {@code list.throttleState} (§6.10): what the list's slow consumer has seen, and whether it holds a message. */
    Map<String, Object> listThrottleState(Args args) {
        runtimeList(args);
        ListThrottle throttle = throttles.get(args.obj("collection").str("name"));
        return Protocol.map(
            "seen", throttle == null ? 0L : (long) throttle.seen(),
            "holding", throttle != null && throttle.holding());
    }

    private NexusListClient<?> runtimeList(Args args) {
        NexusList<Object> list = list(args);
        if (!(list instanceof NexusListClient)) {
            throw new HostError(HostError.INVALID_OPERATION, "The collection is not a runtime list; it has no process test hook.");
        }
        return (NexusListClient<?>) list;
    }

    private static int optInt(Args args, String name, int min, int fallback) {
        Long value = args.optInteger(name);
        if (value == null) {
            return fallback;
        }
        if (value < min || value > Integer.MAX_VALUE) {
            throw HostError.badRequest("The argument '" + name + "' is out of range.");
        }
        return value.intValue();
    }

    Map<String, Object> listWatch(Args args) {
        String watch = args.str("watch");
        NexusList<Object> list = list(args);
        TypedCodec<Object> codec = itemCodec(args);
        if (watches.containsKey(watch)) {
            throw HostError.badRequest("The watch '" + watch + "' is in use.");
        }
        boolean throwing = args.bool("throws");
        Registration registration = list.addChangeListener(change -> {
            host.event(scope.id, handle, "list.changed", changeJson(watch, change, codec));
            if (throwing) {
                // list.watch throws: the runtime must survive a listener that throws (control protocol §6.10).
                throw new IllegalStateException(THROWING_WATCH_MESSAGE);
            }
        });
        watches.put(watch, registration);
        return Protocol.empty();
    }

    Map<String, Object> listUnwatch(Args args) {
        String watch = args.str("watch");
        Registration registration = watches.remove(watch);
        if (registration == null) {
            throw HostError.unknownHandle(watch);
        }
        registration.close();
        return Protocol.empty();
    }

    private static Map<String, Object> changeJson(String watch, NexusListChange<Object> change, TypedCodec<Object> codec) {
        Map<String, Object> data = Protocol.map("watch", watch, "action", Names.pascal(change.action()));
        switch (change.action()) {
            case ADD:
            case REPLACE:
            case MOVE:
                if (change.index() >= 0) {
                    data.put("index", (long) change.index());
                }
                if (change.action() == NexusListChange.Action.MOVE && change.oldIndex() >= 0) {
                    data.put("oldIndex", (long) change.oldIndex());
                }
                data.put("items", Collections.singletonList(codec.toJson(change.item())));
                if (change.action() == NexusListChange.Action.REPLACE) {
                    data.put("oldItems", Collections.singletonList(codec.toJson(change.oldItem())));
                }
                break;
            case REMOVE:
                if (change.oldIndex() >= 0) {
                    data.put("oldIndex", (long) change.oldIndex());
                }
                data.put("oldItems", Collections.singletonList(codec.toJson(change.oldItem())));
                break;
            default:
                break;
        }
        return data;
    }
}

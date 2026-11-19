package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationTokenSource;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;

/** A test scope (control protocol §5): the handles and tokens it owns and its requests in flight. */
final class Scope {
    final String id;
    private final Map<String, Object> handles = new ConcurrentHashMap<>();
    private final Map<String, CancellationTokenSource> tokens = new ConcurrentHashMap<>();
    private final Set<CompletableFuture<?>> inflight = ConcurrentHashMap.newKeySet();

    Scope(String id) {
        this.id = id;
    }

    void put(String handle, Object value) {
        handles.put(handle, value);
    }

    void remove(String handle) {
        handles.remove(handle);
    }

    /**
     * A handle of this scope.
     *
     * @param handle the handle
     * @param type its expected type
     * @param <T> the type
     * @return the value
     * @throws HostError UnknownHandle for an unknown, released or foreign handle, or one of another type
     */
    <T> T get(String handle, Class<T> type) {
        Object value = handles.get(handle);
        if (!type.isInstance(value)) {
            throw HostError.unknownHandle(handle);
        }
        return type.cast(value);
    }

    Object find(String handle) {
        Object value = handles.get(handle);
        if (value == null) {
            throw HostError.unknownHandle(handle);
        }
        return value;
    }

    <T> List<T> all(Class<T> type) {
        List<T> result = new ArrayList<>();
        for (Object value : handles.values()) {
            if (type.isInstance(value)) {
                result.add(type.cast(value));
            }
        }
        return result;
    }

    void clearHandles() {
        handles.clear();
    }

    CancellationTokenSource token(String name) {
        CancellationTokenSource source = tokens.get(name);
        if (source == null) {
            throw HostError.unknownHandle(name);
        }
        return source;
    }

    Map<String, CancellationTokenSource> tokens() {
        return tokens;
    }

    void track(CompletableFuture<?> request) {
        inflight.add(request);
        request.whenComplete((v, e) -> inflight.remove(request));
    }

    List<CompletableFuture<?>> inflight() {
        return new ArrayList<>(inflight);
    }
}

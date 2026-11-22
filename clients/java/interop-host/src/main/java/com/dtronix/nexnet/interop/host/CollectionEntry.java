package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.collections.NexusList;

/**
 * A collection a pool's collection connector returned ({@code pool.collection}, control protocol §6.11): a handle for
 * the {@code list.*} ops of that one collection.
 */
final class CollectionEntry {
    final String handle;
    final PoolEntry pool;
    final String collection;
    final ListOps lists;

    CollectionEntry(InteropHost host, Scope scope, String handle, PoolEntry pool, String collection, NexusList<Object> list) {
        this.handle = handle;
        this.pool = pool;
        this.collection = collection;
        this.lists = new ListOps(host, scope, handle, pool.export, name -> {
            if (!collection.equals(name)) {
                throw HostError.badRequest(handle + " is the collection '" + collection + "', not '" + name + "'.");
            }
            return list;
        });
    }
}

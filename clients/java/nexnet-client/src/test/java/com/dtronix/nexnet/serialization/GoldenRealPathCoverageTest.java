package com.dtronix.nexnet.serialization;

import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import org.junit.jupiter.api.Test;

/**
 * Every message, frame and collection vector, and every reject vector of a message or collection item, has a type
 * that one of the real-path replays takes ({@link GoldenFiles}): {@code session.GoldenSessionVectorsTest},
 * {@code invocation.GoldenInvocationVectorsTest}, {@code pipes.GoldenPipeVectorsTest},
 * {@code collections.GoldenCollectionVectorsTest}. A new message type fails here until a replay owns it.
 */
class GoldenRealPathCoverageTest {
    @Test
    void everyMessageFrameAndCollectionVectorHasARealPath() {
        Set<String> owned = new HashSet<>();
        owned.addAll(GoldenFiles.SESSION_TYPES);
        owned.addAll(GoldenFiles.INVOCATION_TYPES);
        owned.addAll(GoldenFiles.PIPE_TYPES);
        owned.addAll(GoldenFiles.COLLECTION_TYPES);
        Set<String> messages = new HashSet<>();
        for (Map<String, Object> schema : GoldenVectorTests.loadSchemas().values()) {
            if ("message".equals(schema.get("kind"))) {
                messages.add((String) schema.get("id"));
            }
        }
        messages.add("CollectionListMessage");
        int count = 0;
        for (GoldenFiles.Vector v : GoldenFiles.all()) {
            boolean realPath = GoldenFiles.REAL_PATH_FILES.contains(v.file)
                || (v.file.equals("reject") && messages.contains(v.type));
            if (realPath) {
                count++;
                assertTrue(owned.contains(v.type), v + ": no real-path replay takes type " + v.type);
            }
        }
        assertTrue(count > 70, "only " + count + " vectors");
    }
}

package com.dtronix.nexnet.collections;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The C#-produced collection items in {@code collection-items.txt} (recorded by {@code src/test/recorder/collections}).
 */
final class CollectionFixtures {
    private static final Map<String, byte[]> ITEMS = load();

    private CollectionFixtures() {
    }

    /** The item bytes for a name. */
    static byte[] get(String name) {
        byte[] item = ITEMS.get(name);
        if (item == null) {
            throw new IllegalArgumentException("No collection fixture " + name);
        }
        return item.clone();
    }

    static Map<String, byte[]> all() {
        return Collections.unmodifiableMap(ITEMS);
    }

    static byte[] hex(String hex) {
        byte[] bytes = new byte[hex.length() / 2];
        for (int i = 0; i < bytes.length; i++) {
            bytes[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        }
        return bytes;
    }

    private static Map<String, byte[]> load() {
        Map<String, byte[]> items = new LinkedHashMap<>();
        try (InputStream in = CollectionFixtures.class.getResourceAsStream("collection-items.txt");
             BufferedReader reader = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) {
                    continue;
                }
                String[] parts = line.split(" ");
                items.put(parts[0], hex(parts[1]));
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return items;
    }
}

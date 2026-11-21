package com.dtronix.nexnet.session;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

/** The C#-produced frames in {@code golden-frames.txt}. */
public final class GoldenFrames {
    private static final Map<String, byte[]> FRAMES = load();

    private GoldenFrames() {
    }

    /** The frame bytes for a name. */
    public static byte[] get(String name) {
        byte[] frame = FRAMES.get(name);
        if (frame == null) {
            throw new IllegalArgumentException("No golden frame " + name);
        }
        return frame.clone();
    }

    /** Parses lowercase hex. */
    public static byte[] hex(String hex) {
        byte[] bytes = new byte[hex.length() / 2];
        for (int i = 0; i < bytes.length; i++) {
            bytes[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        }
        return bytes;
    }

    private static Map<String, byte[]> load() {
        Map<String, byte[]> frames = new HashMap<>();
        try (InputStream in = GoldenFrames.class.getResourceAsStream("golden-frames.txt");
             BufferedReader reader = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) {
                    continue;
                }
                String[] parts = line.split(" ");
                frames.put(parts[0], hex(parts[1]));
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return frames;
    }
}

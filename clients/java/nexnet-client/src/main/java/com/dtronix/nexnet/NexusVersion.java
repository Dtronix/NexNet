package com.dtronix.nexnet;

/**
 * A .NET {@code System.Version}: major, minor, and optional build and revision components (-1 when absent).
 */
public final class NexusVersion implements Comparable<NexusVersion> {
    private final int major;
    private final int minor;
    private final int build;
    private final int revision;

    private NexusVersion(int major, int minor, int build, int revision) {
        this.major = major;
        this.minor = minor;
        this.build = build;
        this.revision = revision;
    }

    /**
     * Creates a version.
     *
     * @param major the major component (0 or more)
     * @param minor the minor component (0 or more)
     * @param build the build component, or -1 when absent
     * @param revision the revision component, or -1 when absent (must be -1 when build is -1)
     * @return the version
     * @throws IllegalArgumentException when a component is out of range
     */
    public static NexusVersion of(int major, int minor, int build, int revision) {
        if (major < 0 || minor < 0 || build < -1 || revision < -1 || (build == -1 && revision != -1)) {
            throw new IllegalArgumentException("Invalid version components: " + major + "." + minor + "." + build + "." + revision);
        }
        return new NexusVersion(major, minor, build, revision);
    }

    /**
     * Creates a two-component version.
     *
     * @param major the major component
     * @param minor the minor component
     * @return the version
     */
    public static NexusVersion of(int major, int minor) {
        return of(major, minor, -1, -1);
    }

    /**
     * Parses {@code major.minor[.build[.revision]]}.
     *
     * @param text the text
     * @return the version
     * @throws IllegalArgumentException when the text is not a version
     */
    public static NexusVersion parse(String text) {
        String[] parts = text.split("\\.", -1);
        if (parts.length < 2 || parts.length > 4) {
            throw new IllegalArgumentException("Not a version: " + text);
        }
        int[] values = {-1, -1, -1, -1};
        try {
            for (int i = 0; i < parts.length; i++) {
                values[i] = Integer.parseInt(parts[i]);
                if (values[i] < 0) {
                    throw new IllegalArgumentException("Not a version: " + text);
                }
            }
        } catch (NumberFormatException e) {
            throw new IllegalArgumentException("Not a version: " + text, e);
        }
        return of(values[0], values[1], values[2], values[3]);
    }

    /**
     * The major component.
     *
     * @return the value
     */
    public int major() {
        return major;
    }

    /**
     * The minor component.
     *
     * @return the value
     */
    public int minor() {
        return minor;
    }

    /**
     * The build component.
     *
     * @return the value, or -1 when absent
     */
    public int build() {
        return build;
    }

    /**
     * The revision component.
     *
     * @return the value, or -1 when absent
     */
    public int revision() {
        return revision;
    }

    @Override
    public int compareTo(NexusVersion other) {
        int c = Integer.compare(major, other.major);
        if (c == 0) {
            c = Integer.compare(minor, other.minor);
        }
        if (c == 0) {
            c = Integer.compare(build, other.build);
        }
        if (c == 0) {
            c = Integer.compare(revision, other.revision);
        }
        return c;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusVersion)) {
            return false;
        }
        NexusVersion other = (NexusVersion) obj;
        return major == other.major && minor == other.minor && build == other.build && revision == other.revision;
    }

    @Override
    public int hashCode() {
        return ((major * 31 + minor) * 31 + build) * 31 + revision;
    }

    /** Formats the version as .NET does: two to four components. */
    @Override
    public String toString() {
        StringBuilder sb = new StringBuilder().append(major).append('.').append(minor);
        if (build >= 0) {
            sb.append('.').append(build);
            if (revision >= 0) {
                sb.append('.').append(revision);
            }
        }
        return sb.toString();
    }
}

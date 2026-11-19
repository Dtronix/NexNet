package com.dtronix.nexnet.transport;

import java.net.InetAddress;
import java.net.UnknownHostException;
import java.security.cert.CertificateParsingException;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Collection;
import java.util.List;
import java.util.Locale;
import org.jspecify.annotations.Nullable;

/**
 * Matches a host name or IP literal against a server certificate's subjectAltName entries (RFC 6125). Runs after
 * the TLS handshake in addition to the endpoint identification algorithm, because Android's platform verifier is
 * not reliably applied to sockets created directly from an {@code SSLSocketFactory}. There is no fallback to the
 * subject common name.
 */
final class HostnameMatcher {
    private static final int SAN_DNS = 2;
    private static final int SAN_IP = 7;

    private HostnameMatcher() {
    }

    static boolean isIpLiteral(String host) {
        String h = stripBrackets(host);
        if (h.indexOf(':') >= 0) {
            return true;
        }
        return h.matches("\\d{1,3}(\\.\\d{1,3}){3}");
    }

    static String stripBrackets(String host) {
        if (host.length() > 1 && host.charAt(0) == '[' && host.charAt(host.length() - 1) == ']') {
            return host.substring(1, host.length() - 1);
        }
        return host;
    }

    static boolean matches(String host, X509Certificate certificate) {
        Collection<List<?>> names;
        try {
            names = certificate.getSubjectAlternativeNames();
        } catch (CertificateParsingException e) {
            return false;
        }
        if (names == null) {
            return false;
        }
        String h = stripBrackets(host);
        if (isIpLiteral(h)) {
            byte[] address = literalAddress(h);
            if (address == null) {
                return false;
            }
            for (List<?> entry : names) {
                if (entry.size() >= 2 && Integer.valueOf(SAN_IP).equals(entry.get(0))
                    && entry.get(1) instanceof String) {
                    byte[] other = literalAddress((String) entry.get(1));
                    if (other != null && Arrays.equals(address, other)) {
                        return true;
                    }
                }
            }
            return false;
        }
        String name = normalize(h);
        for (List<?> entry : names) {
            if (entry.size() >= 2 && Integer.valueOf(SAN_DNS).equals(entry.get(0))
                && entry.get(1) instanceof String && matchesDns(name, normalize((String) entry.get(1)))) {
                return true;
            }
        }
        return false;
    }

    private static String normalize(String name) {
        String n = name.toLowerCase(Locale.ROOT);
        return n.endsWith(".") ? n.substring(0, n.length() - 1) : n;
    }

    /** A wildcard covers exactly one whole left-most label, and never a public suffix of fewer than two labels. */
    static boolean matchesDns(String host, String pattern) {
        if (host.isEmpty() || pattern.isEmpty()) {
            return false;
        }
        if (!pattern.startsWith("*.")) {
            return pattern.indexOf('*') < 0 && host.equals(pattern);
        }
        String suffix = pattern.substring(1);
        if (suffix.indexOf('*') >= 0 || suffix.indexOf('.', 1) < 0) {
            return false;
        }
        if (!host.endsWith(suffix)) {
            return false;
        }
        String label = host.substring(0, host.length() - suffix.length());
        return !label.isEmpty() && label.indexOf('.') < 0;
    }

    private static byte @Nullable [] literalAddress(String literal) {
        String l = stripBrackets(literal);
        // Only literals: getByName on anything else would resolve through DNS.
        if (!(l.indexOf(':') >= 0 || l.matches("\\d{1,3}(\\.\\d{1,3}){3}"))) {
            return null;
        }
        try {
            return InetAddress.getByName(l).getAddress();
        } catch (UnknownHostException | SecurityException e) {
            return null;
        }
    }
}

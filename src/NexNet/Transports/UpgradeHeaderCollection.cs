using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;

namespace NexNet.Transports;

/// <summary>
/// Custom headers of the WebSocket or HttpSocket upgrade request (<see cref="HttpClientConfig.UpgradeHeaders"/>), name
/// to value, in insertion order. Names compare ignoring case: setting a name again replaces its value (and its
/// spelling). Every name and value is checked when it is set, the same rules as the Java (<c>ClientConfig.Builder.header</c>)
/// and JavaScript (<c>webSocket.headers</c>/<c>httpSocket.headers</c>, checked at <c>createClient</c>) clients, except
/// that only C# also refuses the content headers.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A name must be an HTTP token (RFC 9110 §5.6.2): letters, digits and <c>!#$%&amp;'*+-.^_`|~</c>.</item>
/// <item>A value must be Latin-1 with no control character other than tab (no CR or LF).</item>
/// <item>The headers the transport sets cannot be set: <c>Host</c>, <c>Connection</c>, <c>Upgrade</c>,
/// <c>Sec-WebSocket-*</c>, <c>Content-Length</c> and <c>Transfer-Encoding</c>. Nor can the other content headers
/// (<c>Content-Type</c>, <c>Expires</c>, ...): the upgrade request is a GET with no body.</item>
/// <item><c>Authorization</c> is allowed. <see cref="HttpClientConfig.AuthenticationHeader"/>, when set, replaces it.</item>
/// </list>
/// A refused name or value throws <see cref="ArgumentException"/>; a null one <see cref="ArgumentNullException"/>.
/// </remarks>
public sealed class UpgradeHeaderCollection : IDictionary<string, string>
{
    private readonly List<KeyValuePair<string, string>> _headers = new();

    /// <inheritdoc />
    public int Count => _headers.Count;

    /// <inheritdoc />
    bool ICollection<KeyValuePair<string, string>>.IsReadOnly => false;

    /// <inheritdoc />
    public ICollection<string> Keys => _headers.ConvertAll(static h => h.Key);

    /// <inheritdoc />
    public ICollection<string> Values => _headers.ConvertAll(static h => h.Value);

    /// <summary>
    /// Gets the value of a header, or sets it: a name already present (ignoring case) is replaced, name and value.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <exception cref="ArgumentException">The name or value is refused (see the class remarks).</exception>
    /// <exception cref="KeyNotFoundException">Get: no header has that name.</exception>
    public string this[string name]
    {
        get => TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"No upgrade header '{name}'.");
        set
        {
            Validate(name, value);
            var index = IndexOf(name);
            if (index >= 0)
                _headers.RemoveAt(index);
            _headers.Add(new KeyValuePair<string, string>(name, value));
        }
    }

    /// <summary>Adds a header.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    /// <exception cref="ArgumentException">The name or value is refused (see the class remarks), or a header with
    /// that name (ignoring case) is already present.</exception>
    public void Add(string name, string value)
    {
        Validate(name, value);
        if (IndexOf(name) >= 0)
            throw new ArgumentException($"The upgrade header '{name}' is already set.", nameof(name));
        _headers.Add(new KeyValuePair<string, string>(name, value));
    }

    /// <inheritdoc />
    public bool ContainsKey(string name) => IndexOf(name) >= 0;

    /// <inheritdoc />
    public bool Remove(string name)
    {
        var index = IndexOf(name);
        if (index < 0)
            return false;
        _headers.RemoveAt(index);
        return true;
    }

    /// <inheritdoc />
    public bool TryGetValue(string name, [MaybeNullWhen(false)] out string value)
    {
        var index = IndexOf(name);
        if (index < 0)
        {
            value = null;
            return false;
        }

        value = _headers[index].Value;
        return true;
    }

    /// <inheritdoc />
    public void Clear() => _headers.Clear();

    /// <inheritdoc />
    void ICollection<KeyValuePair<string, string>>.Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

    /// <inheritdoc />
    bool ICollection<KeyValuePair<string, string>>.Contains(KeyValuePair<string, string> item)
        => TryGetValue(item.Key, out var value) && string.Equals(value, item.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    void ICollection<KeyValuePair<string, string>>.CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
        => _headers.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    bool ICollection<KeyValuePair<string, string>>.Remove(KeyValuePair<string, string> item)
        => ((ICollection<KeyValuePair<string, string>>)this).Contains(item) && Remove(item.Key);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _headers.Count; i++)
        {
            if (string.Equals(_headers[i].Key, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>Throws when a header cannot be sent on an upgrade request.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    internal static void Validate(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (name.Length == 0 || !IsToken(name))
            throw new ArgumentException($"Invalid upgrade header name: '{name}'.", nameof(name));

        if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The {name} header is set by the transport.", nameof(name));

        // HttpRequestHeaders refuses content headers (Content-Type, Expires, ...); the upgrade GET has no content.
        using (var probe = new HttpRequestMessage())
        {
            if (!probe.Headers.TryAddWithoutValidation(name, string.Empty))
                throw new ArgumentException($"The {name} header is a content header; the upgrade request has no content.", nameof(name));
        }

        foreach (var c in value)
        {
            if ((c < 0x20 && c != '\t') || c == 0x7F || c > 0xFF)
                throw new ArgumentException($"The value of the {name} header has a control or non-Latin-1 character.", nameof(value));
        }
    }

    private static bool IsToken(string name)
    {
        foreach (var c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                continue;
            if ("!#$%&'*+-.^_`|~".IndexOf(c) < 0)
                return false;
        }

        return true;
    }
}

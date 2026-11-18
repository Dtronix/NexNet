namespace NexNet.Generator.Targets;

/// <summary>Maps .NET names to one target language. Collisions after mapping are NEXNET041.</summary>
internal interface INameMapper
{
    /// <summary>A nexus method (proxy and client nexus): camelCase, leading '_' stripped, keywords escaped.</summary>
    string Method(string name);

    /// <summary>A method parameter: camelCase, leading '_' stripped, keywords escaped.</summary>
    string Parameter(string name);

    /// <summary>An object member or a synchronized collection accessor: camelCase, leading '_' stripped, keywords escaped.</summary>
    string Property(string name);

    /// <summary>A type name: unchanged except for keyword escaping.</summary>
    string Type(string name);

    /// <summary>An enum member: unchanged except for keyword escaping.</summary>
    string EnumMember(string name);
}

/// <summary>
/// The name mapping shared by the targets: they differ only in their reserved words. An escaped name gets a trailing
/// '_' (<c>class</c> → <c>class_</c>).
/// </summary>
internal sealed class NameMapper : INameMapper
{
    private readonly HashSet<string> _keywords;
    private readonly HashSet<string> _reservedMembers;

    /// <param name="keywords">Words that cannot be identifiers in the target.</param>
    /// <param name="reservedMembers">
    /// Additional names a method or member must not take because every generated class already has them
    /// (e.g. Java <c>hashCode</c>, JavaScript <c>constructor</c>).
    /// </param>
    public NameMapper(IEnumerable<string> keywords, IEnumerable<string> reservedMembers)
    {
        _keywords = new HashSet<string>(keywords, StringComparer.Ordinal);
        _reservedMembers = new HashSet<string>(reservedMembers, StringComparer.Ordinal);
    }

    public bool IsKeyword(string name) => _keywords.Contains(name);

    public string Method(string name) => EscapeMember(CamelCase(StripUnderscores(name)));

    public string Parameter(string name) => Escape(CamelCase(StripUnderscores(name)));

    public string Property(string name) => EscapeMember(CamelCase(StripUnderscores(name)));

    public string Type(string name) => Escape(name);

    public string EnumMember(string name) => Escape(name);

    private string Escape(string name) => _keywords.Contains(name) ? name + "_" : name;

    private string EscapeMember(string name) => _keywords.Contains(name) || _reservedMembers.Contains(name) ? name + "_" : name;

    /// <summary>Removes leading underscores (<c>_count</c> → <c>count</c>); a name of only underscores is kept.</summary>
    internal static string StripUnderscores(string name)
    {
        var start = 0;
        while (start < name.Length && name[start] == '_')
            start++;
        return start == name.Length ? name : name.Substring(start);
    }

    /// <summary>
    /// camelCase as System.Text.Json does it: the leading run of upper-case letters is lowered, except the last one
    /// when it starts a word (<c>Add</c> → <c>add</c>, <c>IOStream</c> → <c>ioStream</c>, <c>ID</c> → <c>id</c>).
    /// </summary>
    internal static string CamelCase(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
            return name;

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                break;

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }
}

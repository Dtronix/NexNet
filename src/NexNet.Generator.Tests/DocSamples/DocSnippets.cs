using System.Text;
using System.Text.RegularExpressions;

namespace NexNet.Generator.Tests.DocSamples;

/// <summary>One fenced code block of a Markdown page: the page, its first code line (1-based) and its text.</summary>
internal sealed record DocSnippet(string Page, int Line, string Code)
{
    public string Origin => Page + ":" + Line;
}

/// <summary>
/// Turns the fenced code blocks of the client guides into whole files that compile against the generator's output
/// (H25). The guides show fragments: a class beside the statements that use it, imports only where they help the
/// reader, and free names (a session token, a file's bytes) the reader supplies. The files declare the free names,
/// hoist imports and type declarations, and put each block's statements in a method of its own.
/// </summary>
internal static partial class DocSnippets
{
    /// <summary>The fenced blocks of <paramref name="markdown"/> whose info string is <paramref name="language"/>.</summary>
    public static List<DocSnippet> Blocks(string page, string markdown, string language)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var result = new List<DocSnippet>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() != "```" + language)
                continue;
            var start = i + 1;
            var end = start;
            while (end < lines.Length && lines[end].TrimEnd() != "```")
                end++;
            if (end == lines.Length)
                throw new InvalidOperationException(page + ":" + start + ": unterminated code block");
            result.Add(new DocSnippet(page, start + 1, string.Join("\n", lines[start..end])));
            i = end;
        }

        return result;
    }

    /// <summary>A block split into its imports, its top-level type declarations and its other lines.</summary>
    private sealed record Parts(List<string> Imports, List<string> Types, List<string> Statements);

    private static Parts Split(DocSnippet snippet, Regex import, Regex type)
    {
        var parts = new Parts(new List<string>(), new List<string>(), new List<string>());
        var lines = snippet.Code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (import.IsMatch(line))
            {
                // An import may span lines (JavaScript): up to the line that ends it.
                var text = new StringBuilder(line);
                while (!line.TrimEnd().EndsWith(';') && i + 1 < lines.Length)
                {
                    line = lines[++i];
                    text.Append('\n').Append(line);
                }

                parts.Imports.Add(text.ToString());
            }
            else if (type.IsMatch(line))
            {
                // A declaration at column 0 ends at the first '}' at column 0.
                var text = new StringBuilder("// " + snippet.Page + ":" + (snippet.Line + i) + "\n" + line);
                while (lines[i].TrimEnd() != "}")
                {
                    if (++i == lines.Length)
                        throw new InvalidOperationException(snippet.Origin + ": no closing '}' at column 0 for '" + line + "'");
                    text.Append('\n').Append(lines[i]);
                }

                parts.Types.Add(text.ToString());
            }
            else
            {
                parts.Statements.Add(line);
            }
        }

        while (parts.Statements.Count > 0 && parts.Statements[0].Trim().Length == 0)
            parts.Statements.RemoveAt(0);
        while (parts.Statements.Count > 0 && parts.Statements[^1].Trim().Length == 0)
            parts.Statements.RemoveAt(parts.Statements.Count - 1);
        return parts;
    }

    private static string Indent(IEnumerable<string> lines, string indent)
        => string.Concat(lines.Select(l => (l.Length == 0 ? "" : indent + l) + "\n"));

    // ------------------------------------------------------------------ Java

    [GeneratedRegex(@"^import\s+(static\s+)?[\w.]+(\.\*)?\s*;\s*$")]
    private static partial Regex JavaImport();

    [GeneratedRegex(@"^((public|final|abstract|static)\s+)*(class|interface|enum|record)\s")]
    private static partial Regex JavaType();

    /// <summary>
    /// One Java file, <c>docsamples/DocSamples.java</c>: the blocks' imports, then wildcard imports of every runtime
    /// package and the generated package; the blocks' type declarations as nested classes; and one method per block.
    /// </summary>
    public static string JavaFile(IReadOnlyList<DocSnippet> snippets, string generatedPackage, IEnumerable<string> runtimePackages)
    {
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var types = new List<string>();
        var methods = new StringBuilder();
        for (var n = 0; n < snippets.Count; n++)
        {
            var parts = Split(snippets[n], JavaImport(), JavaType());
            foreach (var import in parts.Imports)
                imports.Add(import.Trim());
            types.AddRange(parts.Types);
            if (parts.Statements.Count == 0)
                continue;
            methods.Append("\n    // ").Append(snippets[n].Origin).Append('\n')
                .Append("    void sample").Append(n + 1).Append("() throws Exception {\n")
                .Append(Indent(parts.Statements, "        "))
                .Append("    }\n");
        }

        var wildcards = runtimePackages.Append(generatedPackage)
            .Concat(new[] { "java.io", "java.net", "java.nio", "java.nio.charset", "java.nio.file", "java.util", "java.util.concurrent" })
            .Select(p => "import " + p + ".*;")
            .Where(i => !imports.Contains(i))
            .OrderBy(i => i, StringComparer.Ordinal);

        var text = new StringBuilder();
        text.Append("package docsamples;\n\n");
        foreach (var import in imports)
            text.Append(import).Append('\n');
        foreach (var import in wildcards)
            text.Append(import).Append('\n');
        text.Append("""

            /** The code samples of the Java client guide; never run, only compiled. */
            final class DocSamples {
                // The names the samples use without declaring them.
                private final String sessionToken = "token";
                private final String jwt = "jwt";
                private final byte[] spkiSha256 = new byte[32];
                private final char[] password = new char[0];
                private final byte[] fileBytes = new byte[0];
                private final URI uri = URI.create("wss://localhost/nexus");
                private final ClientConfig config = ClientConfig.webSocket(uri).build();
                private final NexusClient<ChatServerProxy> client = ChatClient.create(config, new ChatNexus());

                private DocSamples() {
                }

                private void refresh(List<String> rooms) {
                    rooms.clear();
                }

            """.Replace("\r\n", "\n"));
        foreach (var type in types)
            text.Append('\n').Append(Indent(type.Split('\n'), "    "));
        text.Append(methods);
        text.Append("}\n");
        return text.ToString();
    }

    // ------------------------------------------------------------------ JavaScript

    private const string RuntimePackage = "@dtronix/nexnet";

    [GeneratedRegex(@"^export\s*\{([^}]*)\}\s*from\s", RegexOptions.Multiline)]
    private static partial Regex JsExportList();

    /// <summary>The value exports of a declaration file's <c>export { … } from</c> lists (<c>a as b</c> gives b).</summary>
    public static IEnumerable<string> JsExports(string declarations)
        => JsExportList().Matches(declarations)
            .SelectMany(m => m.Groups[1].Value.Split(','))
            .Select(e => e.Split(" as ").Last().Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal);

    [GeneratedRegex(@"^import\s")]
    private static partial Regex JsImport();

    [GeneratedRegex(@"^(export\s+)?class\s")]
    private static partial Regex JsType();

    [GeneratedRegex(@"^\s*[A-Za-z_$][\w$]*\s*:")]
    private static partial Regex JsProperty();

    [GeneratedRegex(@"^import\s*\{([^}]*)\}\s*from\s*'([^']+)'\s*;\s*$", RegexOptions.Singleline)]
    private static partial Regex JsNamedImport();

    /// <summary>
    /// One JavaScript module, <c>samples.js</c>: the blocks' imports (named imports of one module merged), their classes,
    /// and one async function per block. A block that is an object member (<c>authenticate: …,</c>) goes into a
    /// <c>ClientConfig</c> literal.
    /// </summary>
    public static string JsFile(IReadOnlyList<DocSnippet> snippets, IEnumerable<string> runtimeExports)
    {
        var named = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var other = new SortedSet<string>(StringComparer.Ordinal);
        var types = new List<string>();
        var functions = new StringBuilder();
        for (var n = 0; n < snippets.Count; n++)
        {
            var parts = Split(snippets[n], JsImport(), JsType());
            foreach (var import in parts.Imports)
            {
                var match = JsNamedImport().Match(import.Trim());
                if (!match.Success)
                {
                    other.Add(import.Trim());
                    continue;
                }

                if (!named.TryGetValue(match.Groups[2].Value, out var names))
                    named[match.Groups[2].Value] = names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var name in match.Groups[1].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
                    names.Add(name);
            }

            types.AddRange(parts.Types);
            if (parts.Statements.Count == 0)
                continue;
            var statements = parts.Statements;
            if (JsProperty().IsMatch(statements[0]))
            {
                statements = new List<string> { "/** @type {import('@dtronix/nexnet').ClientConfig} */", "const config = {", "  webSocket: { url: 'wss://localhost/nexus' }," }
                    .Concat(parts.Statements.Select(l => l.Length == 0 ? l : "  " + l))
                    .Append("};")
                    .Append("void config;")
                    .ToList();
            }

            functions.Append("\n// ").Append(snippets[n].Origin).Append('\n')
                .Append("export async function sample").Append(n + 1).Append("() {\n")
                .Append(Indent(statements, "  "))
                .Append("}\n");
        }

        // As the Java wildcards: a runtime export a block uses without importing it is imported from the package.
        var imported = named.Values.SelectMany(n => n).Select(n => n.Split(" as ").Last().Trim()).ToHashSet(StringComparer.Ordinal);
        var body = string.Join("\n", types) + functions;
        foreach (var name in runtimeExports.Where(e => !imported.Contains(e)))
        {
            if (!Regex.IsMatch(body, @"(?<![\w$.])" + Regex.Escape(name) + @"(?![\w$])"))
                continue;
            if (!named.TryGetValue(RuntimePackage, out var names))
                named[RuntimePackage] = names = new SortedSet<string>(StringComparer.Ordinal);
            names.Add(name);
        }

        var text = new StringBuilder("// @ts-check\n");
        foreach (var import in other)
            text.Append(import).Append('\n');
        foreach (var (module, names) in named)
            text.Append("import { ").Append(string.Join(", ", names)).Append(" } from '").Append(module).Append("';\n");
        foreach (var type in types)
            text.Append('\n').Append(type).Append('\n');
        text.Append(functions);
        return text.ToString();
    }
}

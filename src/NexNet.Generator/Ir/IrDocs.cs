using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator.Ir;

/// <summary>Reads the summary of a symbol's XML documentation as plain text for the IR.</summary>
internal static class IrDocs
{
    /// <summary>
    /// The &lt;summary&gt; text of <paramref name="symbol"/> with references reduced to their simple names and whitespace
    /// collapsed, or null when there is none.
    /// </summary>
    public static string? FromSymbol(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var summary = root.Name.LocalName == "summary" ? root : root.Element("summary");
        if (summary == null)
            return null;

        var sb = new StringBuilder();
        Append(sb, summary);
        var text = Collapse(sb.ToString());
        return text.Length == 0 ? null : text;
    }

    private static void Append(StringBuilder sb, XElement element)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;

                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see":
                        case "seealso":
                            if (!child.IsEmpty)
                                Append(sb, child);
                            else if (child.Attribute("cref")?.Value is { } cref)
                                sb.Append(SimpleName(cref));
                            else if (child.Attribute("langword")?.Value is { } langword)
                                sb.Append(langword);
                            else if (child.Attribute("href")?.Value is { } href)
                                sb.Append(href);
                            break;

                        case "paramref":
                        case "typeparamref":
                            sb.Append(child.Attribute("name")?.Value);
                            break;

                        case "para":
                        case "br":
                            sb.Append(' ');
                            Append(sb, child);
                            sb.Append(' ');
                            break;

                        default:
                            Append(sb, child);
                            break;
                    }

                    break;
            }
        }
    }

    // "T:Ns.Type{T}.Method(System.Int32)" -> "Method"
    private static string SimpleName(string cref)
    {
        var name = cref;
        var colon = name.IndexOf(':');
        if (colon >= 0)
            name = name.Substring(colon + 1);

        var paren = name.IndexOf('(');
        if (paren >= 0)
            name = name.Substring(0, paren);

        var brace = name.IndexOf('{');
        if (brace >= 0)
            name = name.Substring(0, brace);

        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name.Substring(dot + 1);

        var tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }
}

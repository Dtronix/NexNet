using System.Text;

namespace NexNet.Generator.Targets;

/// <summary>
/// A small indenting text writer shared by the target emitters. Output always uses '\n' line endings, so emitted files
/// are byte-identical on every platform.
/// </summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private readonly string _indentUnit;
    private int _indent;

    /// <param name="indentUnit">The text of one indentation level (four spaces by default).</param>
    public CodeWriter(string indentUnit = "    ")
    {
        _indentUnit = indentUnit;
    }

    /// <summary>The current indentation level.</summary>
    public int IndentLevel => _indent;

    /// <summary>Writes one line at the current indentation. An empty line is written without indentation.</summary>
    public CodeWriter Line(string text = "")
    {
        if (text.Length > 0)
        {
            for (var i = 0; i < _indent; i++)
                _builder.Append(_indentUnit);
            _builder.Append(text);
        }

        _builder.Append('\n');
        return this;
    }

    /// <summary>Writes each line of a multi-line text ('\n' or "\r\n" separated) at the current indentation.</summary>
    public CodeWriter Lines(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            Line(line);
        return this;
    }

    public CodeWriter Indent()
    {
        _indent++;
        return this;
    }

    public CodeWriter Unindent()
    {
        if (_indent == 0)
            throw new InvalidOperationException("Unbalanced Unindent.");
        _indent--;
        return this;
    }

    /// <summary>
    /// Writes <paramref name="header"/> followed by <paramref name="open"/> (on the same line, after a space), indents,
    /// and on dispose unindents and writes <paramref name="close"/>.
    /// </summary>
    public IDisposable Block(string header, string open = "{", string close = "}")
    {
        Line(header.Length == 0 ? open : header + " " + open);
        Indent();
        return new BlockScope(this, close);
    }

    public override string ToString() => _builder.ToString();

    private sealed class BlockScope : IDisposable
    {
        private CodeWriter? _writer;
        private readonly string _close;

        public BlockScope(CodeWriter writer, string close)
        {
            _writer = writer;
            _close = close;
        }

        public void Dispose()
        {
            var writer = _writer;
            if (writer == null)
                return;
            _writer = null;
            writer.Unindent().Line(_close);
        }
    }
}

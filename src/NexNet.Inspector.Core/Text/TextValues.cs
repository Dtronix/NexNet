using System.Globalization;
using System.Text;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Text;

/// <summary>
/// The text renderer's number, size, time and value forms: invariant culture, no machine-local values, and every value
/// cut to the options' width and depth with `…`. Appends to a reused <see cref="StringBuilder"/>; strings decode into
/// a reused buffer.
/// </summary>
public sealed class TextValues
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private char[] _chars = new char[256];

    /// <summary>Creates a formatter for one set of options.</summary>
    /// <param name="options">The options (width and depth).</param>
    public TextValues(TextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Width = Math.Max(4, options.ValueWidth);
        Depth = Math.Max(0, options.Depth);
        MaxElements = Math.Max(1, Width / 8);
    }

    /// <summary>The value width.</summary>
    public int Width { get; }

    /// <summary>The nesting depth shown.</summary>
    public int Depth { get; }

    /// <summary>Elements shown per sequence or map.</summary>
    public int MaxElements { get; }

    /// <summary>`812B`, `18.4KB`, `1.2MB`, `3.0GB` (1 KB = 1024 B).</summary>
    public static StringBuilder AppendSize(StringBuilder sb, long bytes)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (bytes < 1024)
            return sb.Append(Invariant, $"{bytes}B");
        if (bytes < 1024 * 1024)
            return sb.Append(Invariant, $"{bytes / 1024.0:0.0}KB");
        if (bytes < 1024L * 1024 * 1024)
            return sb.Append(Invariant, $"{bytes / (1024.0 * 1024):0.0}MB");
        return sb.Append(Invariant, $"{bytes / (1024.0 * 1024 * 1024):0.0}GB");
    }

    /// <summary>A duration: `15.3ms` below a second, `1.23s` below a minute, `2m05s` after.</summary>
    public static StringBuilder AppendDuration(StringBuilder sb, double ms)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (ms < 1000)
            return sb.Append(Invariant, $"{ms:0.0}ms");
        if (ms < 60_000)
            return sb.Append(Invariant, $"{ms / 1000:0.00}s");
        var seconds = (long)Math.Round(ms / 1000);
        return sb.Append(Invariant, $"{seconds / 60}m{seconds % 60:00}s");
    }

    /// <summary>A line's time: `+22.0`, milliseconds since the session's start with one decimal.</summary>
    public static StringBuilder AppendTime(StringBuilder sb, double ms)
    {
        ArgumentNullException.ThrowIfNull(sb);
        return sb.Append(Invariant, $"+{ms:0.0}");
    }

    /// <summary>Text cut to the width with `…`, unquoted.</summary>
    public StringBuilder AppendCut(StringBuilder sb, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (text.Length <= Width)
            return sb.Append(text);
        return sb.Append(text[..(Width - 1)]).Append('…');
    }

    /// <summary>A quoted string cut to the width with `…`; quotes, backslashes and control characters escaped.</summary>
    public StringBuilder AppendQuoted(StringBuilder sb, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append('"');
        var shown = text.Length <= Width ? text : text[..(Width - 1)];
        foreach (var c in shown)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c))
                        sb.Append(Invariant, $"\\u{(int)c:x4}");
                    else
                        sb.Append(c);
                    break;
            }
        }

        if (shown.Length < text.Length)
            sb.Append('…');
        return sb.Append('"');
    }

    /// <summary>Quoted UTF-8 text cut to the width.</summary>
    public StringBuilder AppendQuotedUtf8(StringBuilder sb, ReadOnlySpan<byte> utf8)
    {
        // Decode at most what can show: a char per byte at most, plus one to know it was cut.
        var take = Math.Min(utf8.Length, (Width * 4) + 4);
        if (_chars.Length < take)
            _chars = new char[Math.Max(take, _chars.Length * 2)];
        var count = Encoding.UTF8.GetChars(utf8[..take], _chars);
        return AppendQuoted(sb, _chars.AsSpan(0, count));
    }

    /// <summary>
    /// A decoded value's top level as words: an object's members separated by spaces (`0 Info "chat" "started"`), any
    /// other value as <see cref="AppendField"/> writes it.
    /// </summary>
    public StringBuilder AppendTop(StringBuilder sb, DecodedValue value)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(value);
        var fields = value.Fields;
        if (fields.Length == 0)
            return sb;
        ref readonly var root = ref fields[0];
        if (root.Kind is FieldKind.Object && Depth > 0)
            return AppendChildren(sb, value, 0, 1, " ", int.MaxValue);
        return AppendField(sb, value, 0, 0);
    }

    /// <summary>An invocation's arguments: the root's children separated by `, `, without the parentheses.</summary>
    public StringBuilder AppendArguments(StringBuilder sb, DecodedValue value)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(value);
        var fields = value.Fields;
        if (fields.Length == 0)
            return sb;
        if (fields[0].Kind != FieldKind.Arguments)
            return AppendField(sb, value, 0, 0);
        return AppendChildren(sb, value, 0, 1, ", ", int.MaxValue);
    }

    /// <summary>One field at a depth (0 for a top-level value).</summary>
    public StringBuilder AppendField(StringBuilder sb, DecodedValue value, int index, int depth)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(value);
        ref readonly var field = ref value.Fields[index];
        switch (field.Kind)
        {
            case FieldKind.Raw:
                return AppendSize(sb.Append("raw "), field.Length);
            case FieldKind.Unknown:
            case FieldKind.External:
                return sb.Append('?');
            case FieldKind.Nil:
                return sb.Append("null");
            case FieldKind.Boolean:
                return sb.Append(field.Value.Boolean ? "true" : "false");
            case FieldKind.Integer:
                return sb.Append(Invariant, $"{field.Value.Int64}");
            case FieldKind.UnsignedInteger:
                return sb.Append(Invariant, $"{field.Value.UInt64}");
            case FieldKind.String:
            case FieldKind.Uri:
            case FieldKind.Version:
                return AppendQuotedUtf8(sb, value.GetContent(field));
            case FieldKind.Binary:
                return AppendSize(sb.Append("bin "), field.ContentLength);
            case FieldKind.PipeId:
                return sb.Append(Invariant, $"pipe:{field.Value.Int64}");
            case FieldKind.Object:
                if (depth >= Depth)
                    return sb.Append("{…}");
                sb.Append('{');
                return AppendChildren(sb, value, index, depth + 1, " ", int.MaxValue).Append('}');
            case FieldKind.Union:
                // Children: the tag, then the case value.
                return field.ChildCount >= 2 ? AppendField(sb, value, LastChild(value, index), depth) : sb.Append(field.Decl?.Name ?? "?");
            case FieldKind.Sequence:
                if (depth >= Depth)
                    return sb.Append(Invariant, $"[{field.ChildCount}]");
                sb.Append('[');
                return AppendChildren(sb, value, index, depth + 1, ", ", MaxElements).Append(']');
            case FieldKind.Map:
                if (depth >= Depth)
                    return sb.Append(Invariant, $"{{{field.ChildCount}}}");
                sb.Append('{');
                return AppendChildren(sb, value, index, depth + 1, ", ", MaxElements).Append('}');
            case FieldKind.MapEntry:
                return AppendChildren(sb, value, index, depth, ": ", 2);
            case FieldKind.Tuple:
                if (depth >= Depth)
                    return sb.Append(Invariant, $"({field.ChildCount})");
                sb.Append('(');
                return AppendChildren(sb, value, index, depth + 1, ", ", MaxElements).Append(')');
            case FieldKind.Arguments:
                sb.Append('(');
                return AppendChildren(sb, value, index, depth + 1, ", ", int.MaxValue).Append(')');
            case FieldKind.ExceptionDetail:
                return AppendException(sb, value, index);
            case FieldKind.CollectionMessage:
            case FieldKind.CollectionBody:
                return sb.Append(field.Name ?? "?");
            default:
                return AppendCut(sb, value.FormatValue(field));
        }
    }

    /// <summary>An exception detail: `InvalidOperationException "message"` (the code when it has one).</summary>
    public StringBuilder AppendException(StringBuilder sb, DecodedValue value, int index)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(value);
        var fields = value.Fields;
        var first = true;
        for (var child = fields[index].FirstChild; child >= 0; child = fields[child].NextSibling)
        {
            ref readonly var field = ref fields[child];
            if (field.Kind == FieldKind.Nil)
                continue;
            if (!first)
                sb.Append(' ');
            first = false;
            switch (field.Name)
            {
                case "Type" when field.Kind == FieldKind.String:
                    AppendCut(sb, value.GetText(field));
                    break;
                case "Code":
                    sb.Append("code ");
                    AppendField(sb, value, child, 1);
                    break;
                default:
                    AppendField(sb, value, child, 1);
                    break;
            }
        }

        return sb;
    }

    private StringBuilder AppendChildren(StringBuilder sb, DecodedValue value, int parent, int depth, string separator, int limit)
    {
        var fields = value.Fields;
        var shown = 0;
        var total = fields[parent].ChildCount;
        for (var child = fields[parent].FirstChild; child >= 0; child = fields[child].NextSibling)
        {
            if (shown == limit)
            {
                sb.Append(separator).Append(Invariant, $"…+{total - shown}");
                break;
            }

            if (shown > 0)
                sb.Append(separator);
            AppendField(sb, value, child, depth);
            shown++;
        }

        return sb;
    }

    private static int LastChild(DecodedValue value, int parent)
    {
        var fields = value.Fields;
        var last = -1;
        for (var child = fields[parent].FirstChild; child >= 0; child = fields[child].NextSibling)
            last = child;
        return last;
    }
}

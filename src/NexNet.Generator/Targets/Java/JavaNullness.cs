using System.Text.RegularExpressions;

namespace NexNet.Generator.Targets.Java;

/// <summary>
/// The <c>Nullness</c> option of the Java target (D31, docs/internals/generator-emitted-runtime.md §5): with
/// <c>none</c>, the runtime and the generated code carry no JSpecify annotations, so they compile with no jar at all.
/// </summary>
internal static class JavaNullness
{
    /// <summary>Option: <c>jspecify</c> keeps the JSpecify <c>@Nullable</c> annotations, <c>none</c> removes them.</summary>
    public const string Option = "Nullness";

    public const string JSpecify = "jspecify";

    public const string None = "none";

    private static readonly Regex Import = new(@"^import org\.jspecify\.annotations\.[A-Za-z]+;\n", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // "byte @Nullable []" -> "byte[]" (an annotated array type).
    private static readonly Regex ArrayAnnotation = new(@" ?@(?:Nullable|NonNull)\s*\[\]", RegexOptions.CultureInvariant);

    // "@Nullable T", "java.util.@Nullable List", "@NullMarked class".
    private static readonly Regex Annotation = new(@"@(?:Nullable|NonNull|NullMarked|NullUnmarked)\b\s*", RegexOptions.CultureInvariant);

    /// <summary>Whether the value is <c>jspecify</c> or <c>none</c> (any case).</summary>
    public static bool IsValid(string value)
        => string.Equals(value, JSpecify, StringComparison.OrdinalIgnoreCase) || string.Equals(value, None, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the annotations are kept: <c>Nullness</c> when set; otherwise only when the runtime is not emitted, i.e. it
    /// comes from the nexnet-client jar, which brings JSpecify to the compile class path (K28).
    /// </summary>
    public static bool Keep(TargetOptions options, bool emitsRuntime)
    {
        var value = options.Get(Option);
        return value == null ? !emitsRuntime : string.Equals(value, JSpecify, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Java source without JSpecify imports and annotations. Only type-use positions are annotated in NexNet code, so
    /// removing the annotation (and the space after it) leaves the same type.
    /// </summary>
    public static string Strip(string source)
    {
        if (source.IndexOf("jspecify", StringComparison.Ordinal) < 0 && source.IndexOf("@Nullable", StringComparison.Ordinal) < 0)
            return source;

        source = Import.Replace(source, "");
        source = ArrayAnnotation.Replace(source, "[]");
        return Annotation.Replace(source, "");
    }
}

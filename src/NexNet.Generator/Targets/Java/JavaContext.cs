using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>What the Java emitters share for one emission: the IR's declared types, the package and the type mappers.</summary>
internal sealed class JavaContext
{
    public JavaContext(JavaTarget target, NexusIr ir, string package)
    {
        Target = target;
        Ir = ir;
        Package = package;
        Scope = new TypeScope(ir);
        Qualified = target.QualifiedTypes(package);
        FormattersPackage = package + "." + FormatterEmitter.SubPackage;
    }

    public JavaTarget Target { get; }

    public NexusIr Ir { get; }

    /// <summary>The package of the generated types (the &lt;c&gt;Package&lt;/c&gt; option).</summary>
    public string Package { get; }

    /// <summary>The package of &lt;c&gt;Formatters&lt;/c&gt; and &lt;c&gt;UserFormatters&lt;/c&gt;.</summary>
    public string FormattersPackage { get; }

    public TypeScope Scope { get; }

    /// <summary>Maps types with generated classes written by simple name (code in &lt;see cref="Package"/&gt;).</summary>
    public ITypeMapper Simple => Target.Types;

    /// <summary>Maps types with generated classes written fully qualified (code in other packages).</summary>
    public ITypeMapper Qualified { get; }

    /// <summary>The simple Java name of a declared type.</summary>
    public string Name(TypeDeclIr decl) => Target.TypeName(decl);

    /// <summary>The fully qualified Java name of a declared type.</summary>
    public string QualifiedName(TypeDeclIr decl) => Package + "." + Target.TypeName(decl);

    /// <summary>The &lt;c&gt;Formatters&lt;/c&gt; class, fully qualified.</summary>
    public string FormattersClass => FormattersPackage + "." + FormatterEmitter.ClassName;

    /// <summary>The &lt;c&gt;UserFormatters&lt;/c&gt; class, fully qualified.</summary>
    public string UserFormattersClass => FormattersPackage + "." + FormatterEmitter.UserClassName;
}

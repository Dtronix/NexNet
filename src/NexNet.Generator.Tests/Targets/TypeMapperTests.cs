using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;
using NexNet.Generator.Targets.JavaScript;

namespace NexNet.Generator.Tests.Targets;

/// <summary>Every row of the normative type mapping (report §3.5, 01-context §5) for Java and JavaScript.</summary>
public class TypeMapperTests
{
    private static readonly TypeScope Scope = new(new NexusIr(
        NexusIr.CurrentVersion,
        EquatableArray<ExportIr>.Empty,
        new EquatableArray<TypeDeclIr>(new TypeDeclIr[]
        {
            new EnumDeclIr("Ns.Color", "Ns.Color", "Ns", "Color", EquatableArray<string>.Empty, null, PrimKind.Byte, false, EquatableArray<EnumMemberIr>.Empty),
            new ObjectDeclIr("Ns.Outer+Box`1", "Ns.Outer+Box`1", "Ns", "Box", new EquatableArray<string>(new[] { "Outer" }), null,
                false, false, false, new EquatableArray<string>(new[] { "T" }), null, 1, false, EquatableArray<MemberIr>.Empty),
            new UnionDeclIr("Ns.Shape", "Ns.Shape", "Ns", "Shape", EquatableArray<string>.Empty, null, true, EquatableArray<string>.Empty, false, EquatableArray<UnionCaseIr>.Empty),
        })));

    private static TypeRef Prim(PrimKind prim) => new(TypeRefKind.Prim) { Prim = prim };

    private static TypeRef Of(TypeRefKind kind, string? clr, params TypeRef[] args)
        => new(kind) { Clr = clr, Args = new EquatableArray<TypeRef>(args) };

    private static TypeRef Decl(string id, params TypeRef[] args)
        => new(TypeRefKind.Decl) { DeclId = id, Args = new EquatableArray<TypeRef>(args) };

    private static TargetType Java(TypeRef type) => JavaTarget.Instance.Types.Map(type, Scope);

    private static TargetType Js(TypeRef type) => JavaScriptTarget.Instance.Types.Map(type, Scope);

    [Test]
    [Arguments("Bool", "boolean", true, "boolean", true)]
    [Arguments("SByte", "byte", true, "number", true)]
    [Arguments("Byte", "int", true, "number", true)]
    [Arguments("Int16", "short", true, "number", true)]
    [Arguments("UInt16", "int", true, "number", true)]
    [Arguments("Int32", "int", true, "number", true)]
    [Arguments("UInt32", "long", true, "number", true)]
    [Arguments("Int64", "long", true, "bigint", true)]
    [Arguments("UInt64", "long", true, "bigint", true)]
    [Arguments("Single", "float", true, "number", true)]
    [Arguments("Double", "double", true, "number", true)]
    [Arguments("Half", "float", true, "number", true)]
    [Arguments("Char", "char", true, "string", true)]
    [Arguments("Decimal", "java.math.BigDecimal", true, "NexusDecimal", false)]
    [Arguments("String", "String", true, "string", true)]
    [Arguments("DateTime", "com.dtronix.nexnet.NexusDateTime", true, "NexusDateTime", false)]
    [Arguments("DateTimeOffset", "com.dtronix.nexnet.NexusDateTimeOffset", true, "NexusDateTimeOffset", false)]
    [Arguments("TimeSpan", "com.dtronix.nexnet.NexusTimeSpan", true, "NexusTimeSpan", false)]
    [Arguments("DateOnly", "com.dtronix.nexnet.NexusDateOnly", true, "NexusDateOnly", false)]
    [Arguments("TimeOnly", "com.dtronix.nexnet.NexusTimeOnly", true, "NexusTimeOnly", false)]
    [Arguments("Guid", "java.util.UUID", true, "string", true)]
    [Arguments("BigInteger", "java.math.BigInteger", true, "bigint", true)]
    [Arguments("Uri", "String", true, "string", true)]
    [Arguments("Version", "com.dtronix.nexnet.NexusVersion", true, "Version", false)]
    public async Task Primitive(string prim, string java, bool javaValueEquality, string js, bool jsValueEquality)
    {
        var type = Prim((PrimKind)Enum.Parse(typeof(PrimKind), prim));
        await Assert.That(Java(type)).IsEqualTo(new TargetType(java, javaValueEquality));
        await Assert.That(Js(type)).IsEqualTo(new TargetType(js, jsValueEquality));
    }

    [Test]
    public async Task EveryPrimitive_HasAMapping()
    {
        foreach (PrimKind prim in Enum.GetValues(typeof(PrimKind)))
        {
            if (prim == PrimKind.None)
                continue;
            await Assert.That(Java(Prim(prim)).IsSupported).IsTrue().Because(prim + " in Java");
            await Assert.That(Js(Prim(prim)).IsSupported).IsTrue().Because(prim + " in JavaScript");
        }
    }

    [Test]
    [Arguments("SByte", "byte[]", "Int8Array")]
    [Arguments("Int16", "short[]", "Int16Array")]
    [Arguments("UInt16", "char[]", "Uint16Array")]
    [Arguments("Int32", "int[]", "Int32Array")]
    [Arguments("UInt32", "int[]", "Uint32Array")]
    [Arguments("Int64", "long[]", "BigInt64Array")]
    [Arguments("UInt64", "long[]", "BigUint64Array")]
    [Arguments("Single", "float[]", "Float32Array")]
    [Arguments("Double", "double[]", "Float64Array")]
    [Arguments("Half", "float[]", "Float32Array")]
    [Arguments("Char", "char[]", "Uint16Array")]
    public async Task PrimitiveArray(string prim, string java, string js)
    {
        var type = new TypeRef(TypeRefKind.PrimArray) { Prim = (PrimKind)Enum.Parse(typeof(PrimKind), prim), Clr = "Array" };
        await Assert.That(Java(type)).IsEqualTo(new TargetType(java, false));
        await Assert.That(Js(type)).IsEqualTo(new TargetType(js, false));
    }

    [Test]
    public async Task Containers()
    {
        var i = Prim(PrimKind.Int32);
        var s = Prim(PrimKind.String);
        var rows = new (TypeRef Type, TargetType Java, TargetType Js)[]
        {
            (Of(TypeRefKind.Bin, "Array"), new("byte[]", false), new("Uint8Array", false)),
            (Of(TypeRefKind.Seq, "List", i), new("java.util.List<Integer>", true), new("Array<number>", false)),
            (Of(TypeRefKind.Seq, "Array", s), new("java.util.List<String>", true), new("Array<string>", false)),
            (Of(TypeRefKind.Set, "HashSet", s), new("java.util.Set<String>", true), new("Set<string>", false)),
            (Of(TypeRefKind.Map, "Dictionary", s, i), new("java.util.Map<String, Integer>", true), new("Map<string, number>", false)),
            (Of(TypeRefKind.Queue, "Queue", i), new("java.util.ArrayDeque<Integer>", false), new("Array<number>", false)),
            (Of(TypeRefKind.Stack, "Stack", i), new("java.util.ArrayDeque<Integer>", false), new("Array<number>", false)),
            (Of(TypeRefKind.Tuple, "ValueTuple", i, s), new("com.dtronix.nexnet.Tuple2<Integer, String>", true), new("[number, string]", false)),
            (Of(TypeRefKind.Tuple, "Tuple", i), new("com.dtronix.nexnet.Tuple1<Integer>", true), new("[number]", false)),
            (Of(TypeRefKind.Tuple, "KeyValuePair", s, i), new("java.util.Map.Entry<String, Integer>", true), new("[string, number]", false)),
            (Of(TypeRefKind.Nullable, null, i), new("Integer", true), new("number | null", true)),
            (Of(TypeRefKind.Nullable, null, Prim(PrimKind.Guid)), new("java.util.UUID", true), new("string | null", true)),
            (s with { RefNullable = true }, new("String", true), new("string | null", true)),
            (Decl("Ns.Color"), new("Color", true), new("Color", true)),
            (Decl("Ns.Shape"), new("Shape", true), new("Shape", false)),
            (Decl("Ns.Outer+Box`1", Prim(PrimKind.Int64)), new("Outer_Box<Long>", true), new("Outer_Box<bigint>", false)),
            (Of(TypeRefKind.Seq, "List", Of(TypeRefKind.Map, "Dictionary", Prim(PrimKind.Int64), Decl("Ns.Color"))),
                new("java.util.List<java.util.Map<Long, Color>>", true), new("Array<Map<bigint, Color>>", false)),
        };

        foreach (var (type, java, js) in rows)
        {
            await Assert.That(Java(type)).IsEqualTo(java);
            await Assert.That(Js(type)).IsEqualTo(js);
        }
    }

    [Test]
    public async Task TypeParameter_UsesTheEnclosingName()
    {
        var type = new TypeRef(TypeRefKind.TypeParam) { Ordinal = 0 };
        var scope = Scope.WithTypeParams(new[] { "TItem" });
        await Assert.That(JavaTarget.Instance.Types.Map(type, scope).Name).IsEqualTo("TItem");
        await Assert.That(JavaScriptTarget.Instance.Types.Map(type, scope).Name).IsEqualTo("TItem");
    }

    [Test]
    public async Task ExternalType_NeedsANativeFormatterPerTarget()
    {
        var bare = new TypeRef(TypeRefKind.External) { Clr = "Ns.Money" };
        await Assert.That(Java(bare).IsSupported).IsFalse();
        await Assert.That(Js(bare).IsSupported).IsFalse();

        var javaOnly = bare with
        {
            NativeFormatters = new EquatableArray<NativeFormatterIr>(new[] { new NativeFormatterIr(1, "com.example.MoneyFormatter") }),
        };
        await Assert.That(Java(javaOnly).NativeFormatter).IsEqualTo("com.example.MoneyFormatter");
        await Assert.That(Js(javaOnly).IsSupported).IsFalse();

        // The failure of an argument is the failure of the container.
        await Assert.That(Js(Of(TypeRefKind.Seq, "List", javaOnly)).Unsupported).Contains("NexusTargets.JavaScript");
    }

    [Test]
    public async Task LongTuple_IsUnsupportedInJavaOnly()
    {
        var args = Enumerable.Range(0, 8).Select(_ => Prim(PrimKind.Int32)).ToArray();
        var tuple = Of(TypeRefKind.Tuple, "ValueTuple", args);
        await Assert.That(Java(tuple).IsSupported).IsFalse();
        await Assert.That(Js(tuple).IsSupported).IsTrue();
    }

    [Test]
    public async Task UnknownDeclaredType_IsUnsupported()
    {
        await Assert.That(Java(Decl("Ns.Missing")).IsSupported).IsFalse();
        await Assert.That(Js(Decl("Ns.Missing")).IsSupported).IsFalse();
    }
}

using NexNet.Generator.Ir;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;

namespace NexNet.Generator.Tests.Targets.Java;

/// <summary>The Java formatter and type emitters (K03): text snapshots and structure; no JDK needed.</summary>
public class JavaEmitterTests
{
    private static JavaRun? _zoo;
    private static JavaRun? _features;

    internal static JavaRun Zoo => _zoo ??= JavaTestRunner.Run(JavaFixtures.ZooPackage, JavaFixtures.Zoo());

    internal static JavaRun Features => _features ??= JavaTestRunner.Run(JavaFixtures.FeaturePackage, JavaFixtures.Features);

    private static async Task AssertClean(JavaRun run)
    {
        await Assert.That(run.Ir).IsNotNull();
        await Assert.That(run.Diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(run.CompileErrors.Select(d => d.ToString())).IsEmpty();
    }

    // ------------------------------------------------------------------ snapshots

    [Test]
    public async Task ZooRunsClean() => await AssertClean(Zoo);

    [Test]
    public async Task FeaturesRunClean() => await AssertClean(Features);

    [Test]
    public Task ZooSnapshot() => JavaSnapshots.Verify("zoo", JavaProxyEmitterTests.MaskRuntimeVersion(Zoo.Concatenated()));

    [Test]
    public Task FeaturesSnapshot() => JavaSnapshots.Verify("features", JavaProxyEmitterTests.MaskRuntimeVersion(Features.Concatenated()));

    // ------------------------------------------------------------------ layout

    [Test]
    public async Task OneFilePerTypeInTheFlatPackage_FormattersInTheirSubPackage()
    {
        var options = JavaTestRunner.TargetOptionsFor(JavaFixtures.FeaturePackage);
        var expected = Features.Ir!.Types
            .SelectMany(t => JavaTarget.Instance.TypeFiles(t, options))
            .Append("com/acme/formatters/Formatters.java")
            .Append("com/acme/formatters/UserFormatters.java")
            .Concat(JavaTarget.Instance.ExportFiles(Features.Ir.Exports.Single(), options))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(Features.Files.Keys.OrderBy(p => p, StringComparer.Ordinal)).IsEquivalentTo(expected);
        await Assert.That(Features.Files.Keys).Contains("com/acme/Outer_Inner.java");
        foreach (var (path, content) in Features.Files)
        {
            var package = path.Substring(0, path.LastIndexOf('/')).Replace('/', '.');
            await Assert.That(content).StartsWith(JavaSyntax.Header + "\npackage " + package + ";\n");
        }
    }

    [Test]
    public async Task NoUserFormattersFileWithoutMappings()
    {
        var run = JavaTestRunner.Run("com.acme", Source("[NexusObject] public class Item { [NexusKey(0)] public int Id { get; set; } }", "Item"));
        await Assert.That(run.Diagnostics).IsEmpty();
        var exportFiles = JavaTarget.Instance.ExportFiles(run.Ir!.Exports.Single(), JavaTestRunner.TargetOptionsFor("com.acme"));
        await Assert.That(run.Files.Keys.Except(exportFiles).OrderBy(p => p, StringComparer.Ordinal))
            .IsEquivalentTo(new[] { "com/acme/Item.java", "com/acme/formatters/Formatters.java" });
    }

    [Test]
    public async Task OutputIsDeterministic()
    {
        var again = JavaTestRunner.Run(JavaFixtures.FeaturePackage, JavaFixtures.Features);
        await Assert.That(again.Concatenated()).IsEqualTo(Features.Concatenated());
        await Assert.That(Features.Concatenated()).DoesNotContain("\r");
    }

    [Test]
    public async Task TestScaffoldingModeEmitsNothing()
    {
        var files = JavaTarget.Instance.Emit(Features.Ir!, JavaTestRunner.TargetOptionsFor("com.acme"), EmitMode.TestScaffolding);
        await Assert.That(files).IsEmpty();
    }

    // ------------------------------------------------------------------ types

    [Test]
    public async Task ObjectsAreValuePojos()
    {
        var item = Features.File("com/acme/Defaults.java");
        await Assert.That(item).Contains("public class Defaults {");
        await Assert.That(item).Contains("public Defaults() {");
        await Assert.That(item).Contains("public int getCount() {");
        await Assert.That(item).Contains("public void setCount(int count) {");
        await Assert.That(item).Contains("public boolean equals(Object obj) {");
        await Assert.That(item).Contains("public int hashCode() {");
        await Assert.That(item).Contains("public String toString() {");
        await Assert.That(item).Contains("private @Nullable String name = ");

        // Structs are final; a [NexusObject] base class is extended; interface unions are implemented.
        await Assert.That(Features.File("com/acme/Spot.java")).Contains("public final class Spot implements IEvent {");
        await Assert.That(Features.File("com/acme/BigCircle.java")).Contains("public class BigCircle extends Circle {");
        await Assert.That(Features.File("com/acme/BigCircle.java")).Contains("super(radius);");
        await Assert.That(Features.File("com/acme/Circle.java")).Contains("public class Circle extends Shape {");
        await Assert.That(Features.File("com/acme/Shape.java")).Contains("public abstract class Shape {");
        await Assert.That(Features.File("com/acme/IEvent.java")).Contains("public interface IEvent {");
        await Assert.That(Features.File("com/acme/Holder.java")).Contains("public class Holder<T> {");
    }

    [Test]
    public async Task MembersStartAtTheirCSharpInitialValue()
    {
        var defaults = Features.File("com/acme/Defaults.java");
        await Assert.That(defaults).Contains("private int count = 5;");
        await Assert.That(defaults).Contains("private @Nullable String name = \"a\\\"b\\\\c\\n\\u00e9\";");
        await Assert.That(defaults).Contains("private double ratio = 1.5d;");
        await Assert.That(defaults).Contains("private float single = 2.25f;");
        await Assert.That(defaults).Contains("private java.math.BigDecimal price = new java.math.BigDecimal(\"1.25\");");
        await Assert.That(defaults).Contains("private Small mode = Small.B;");
        await Assert.That(defaults).Contains("private char letter = '\\u00e9';");
        await Assert.That(defaults).Contains("private long unsigned = -1L;");
        await Assert.That(defaults).Contains("private short short_ = (short) -3;");
        await Assert.That(defaults).Contains("private byte tiny = (byte) -7;");
        await Assert.That(defaults).Contains("private long count32 = 4000000000L;");

        // C# value types that are Java classes start at default(T), as C# writes them.
        await Assert.That(defaults).Contains("private com.dtronix.nexnet.NexusDateTime when = com.dtronix.nexnet.NexusDateTime.of(0L, com.dtronix.nexnet.NexusDateTimeKind.UNSPECIFIED);");
        await Assert.That(defaults).Contains("private com.dtronix.nexnet.Tuple2<Integer, String> pair = com.dtronix.nexnet.Tuple2.of(0, null);");
        await Assert.That(defaults).Contains("private java.util.Map.Entry<String, Integer> entry = new java.util.AbstractMap.SimpleImmutableEntry<>(null, 0);");
        await Assert.That(defaults).Contains("private int[] memory = new int[0];");
        await Assert.That(defaults).Contains("private java.util.UUID id = new java.util.UUID(0L, 0L);");
        await Assert.That(Features.File("com/acme/Everything.java")).Contains("private Spot spot = new Spot();");
        await Assert.That(Features.File("com/acme/Everything.java")).Contains("private Huge huge = Huge.of(0L);");
    }

    [Test]
    public async Task EnumsAreOpenValueClasses()
    {
        var small = Features.File("com/acme/Small.java");
        await Assert.That(small).Contains("public final class Small {");
        await Assert.That(small).Contains("public static final Small Top = new Small(128);");
        await Assert.That(small).Contains("public static Small of(int value) {");
        await Assert.That(small).Contains("public Small or(Small other) {");
        await Assert.That(small).Contains("public boolean has(Small flags) {");

        var neg = Features.File("com/acme/Neg.java");
        await Assert.That(neg).Contains("public static final Neg M = new Neg((byte) -128);");
        await Assert.That(neg).DoesNotContain(" or(");

        var huge = Features.File("com/acme/Huge.java");
        await Assert.That(huge).Contains("public static final Huge Max = new Huge(-1L);");
        await Assert.That(huge).Contains("com.dtronix.nexnet.UnsignedLongs.toString(this.value$)");
    }

    [Test]
    public async Task NullableMembersAreAnnotatedWhereJavaAllowsIt()
    {
        var everything = Features.File("com/acme/Everything.java");
        await Assert.That(everything).Contains("import org.jspecify.annotations.Nullable;");
        await Assert.That(everything).Contains("private java.util.@Nullable List<Object> monies;");
        await Assert.That(everything).Contains("private byte @Nullable [] bin;");
        await Assert.That(everything).Contains("private @Nullable Shape shape;");
        await Assert.That(everything).Contains("private com.dtronix.nexnet.@Nullable NexusVersion version;");
        await Assert.That(Features.File("com/acme/Circle.java")).DoesNotContain("Nullable");
    }

    // ------------------------------------------------------------------ formatters

    [Test]
    public async Task FormattersHaveAConstantPerTypeAndAFactoryPerGenericType()
    {
        var formatters = Features.File("com/acme/formatters/Formatters.java");
        await Assert.That(formatters).Contains("public static final Formatter<com.acme.Defaults> Defaults = new Fmt$Defaults();");
        await Assert.That(formatters).Contains("public static final Formatter<com.acme.Outer_Inner> Outer_Inner = new Fmt$Outer_Inner();");
        await Assert.That(formatters).Contains("public static <T> Formatter<com.acme.Holder<T>> Holder(Formatter<T> f0) {");
    }

    [Test]
    public async Task GenericSequencesTakeTheirBoundWireForm()
    {
        var formatters = Features.File("com/acme/formatters/Formatters.java");
        await Assert.That(formatters).Contains("this.c0 = Formatters.seq(f0, false);");
        await Assert.That(formatters).Contains("this.c1 = Formatters.seq(f0, true);");
        await Assert.That(formatters).Contains("this.c2 = com.dtronix.nexnet.serialization.Formatters.list(f0);");
        await Assert.That(formatters).Contains("static <T> Formatter<java.util.List<T>> seq(Formatter<T> element, boolean array) {");
    }

    [Test]
    public async Task RecursiveGenericsDoNotRecurseOnConstruction()
    {
        var formatters = Features.File("com/acme/formatters/Formatters.java");
        await Assert.That(formatters).Contains("this.c0 = com.dtronix.nexnet.serialization.Formatters.list(this);");
        await Assert.That(formatters).Contains("this.c0 = Formatters.lazy(() -> Formatters.Pong(f0));");
        await Assert.That(formatters).Contains("this.c0 = Formatters.lazy(() -> Formatters.Ping(f0));");
    }

    [Test]
    public async Task UnionsWriteDeeperCasesFirst()
    {
        var formatters = Features.File("com/acme/formatters/Formatters.java");
        var big = formatters.IndexOf("if (value instanceof com.acme.BigCircle)", StringComparison.Ordinal);
        var circle = formatters.IndexOf("if (value instanceof com.acme.Circle)", StringComparison.Ordinal);
        await Assert.That(big).IsGreaterThan(0);
        await Assert.That(circle).IsGreaterThan(big);
        await Assert.That(formatters).Contains("int tag = reader.readUInt16();");
    }

    [Test]
    public async Task StructsRefuseNull()
    {
        var formatters = Features.File("com/acme/formatters/Formatters.java");
        await Assert.That(formatters).Contains("\"Spot is a value type and cannot be null.\"");
        await Assert.That(formatters).Contains("\"nil is not a value of the value type Spot.\"");
        await Assert.That(formatters).Contains("\"Small is a value type and cannot be null.\"");
    }

    [Test]
    public async Task UserFormattersAreInstantiatedByClassName()
    {
        var user = Features.File("com/acme/formatters/UserFormatters.java");
        await Assert.That(user).Contains("public static final Formatter<Object> com_acme_ext_MoneyFormatter = cast(new com.acme.ext.MoneyFormatter());");
        await Assert.That(Features.File("com/acme/formatters/Formatters.java")).Contains("UserFormatters.com_acme_ext_MoneyFormatter.write(writer, value.getMoney());");
    }

    [Test]
    public async Task ExpressionsForProxies()
    {
        // K04 builds argument formatters with the same builder, written fully qualified.
        var expressions = new FormatterExpressions(new TypeScope(Features.Ir!), "com.acme.formatters.Formatters", "com.acme.formatters.UserFormatters");
        var list = new TypeRef(TypeRefKind.Seq) { Clr = "List", Args = new(new[] { new TypeRef(TypeRefKind.Decl) { DeclId = "Feat.Defaults" } }) };
        await Assert.That(expressions.For(list)).IsEqualTo("com.dtronix.nexnet.serialization.Formatters.list(com.acme.formatters.Formatters.Defaults)");
    }

    // ------------------------------------------------------------------ diagnostics

    [Test]
    [Arguments("Formatters")]
    [Arguments("UserFormatters")]
    [Arguments("formatters")]
    public async Task TypeNamedLikeTheFormatterClasses_Is041(string name)
    {
        var run = TargetTestRunner.Run(JavaTestRunner.GlobalOptions("com.acme"),
            Source("[NexusObject] public class " + name + " { [NexusKey(0)] public int Id { get; set; } }", name));
        var messages = run.Messages("NEXNET041");
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "Name collision (Java): type 'Demo." + name + "' maps to '" + name + "', which the generated formatter classes use",
        });
    }

    private static string Source(string types, string itemType) => TargetTestRunner.Usings + """
        #nullable enable
        namespace Demo;

        """ + types + """

        public interface IClientNexus { ValueTask Notify(string message); }
        public interface IServerNexus { ValueTask<ITEM> Get(); }

        [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
        public partial class ClientNexus { }

        [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.Java)]
        public partial class ServerNexus { }
        """.Replace("ITEM", itemType);
}

using Microsoft.CodeAnalysis;
using NexNet.Generator.Serialization;
using TUnit.Assertions.Enums;

namespace NexNet.Generator.Tests;

public class ShapeBuilderTests
{
    private static (ShapeBuilder Shapes, Compilation Compilation) Build(string source)
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation("using NexNet.Serialization;\n#nullable enable\n" + source);
        return (new ShapeBuilder(compilation), compilation);
    }

    private static ObjectShape GetObject(string source, string typeName)
    {
        var (shapes, compilation) = Build(source);
        var type = compilation.GetTypeByMetadataName(typeName)
                   ?? throw new InvalidOperationException($"Type '{typeName}' not found.");
        return (ObjectShape)shapes.Get(type);
    }

    [Test]
    public async Task MembersAreInKeyOrder()
    {
        var shape = GetObject("""
            [NexusObject] public class Message
            {
                [NexusKey(3)] public int C { get; set; }
                [NexusKey(0)] public int A { get; set; }
                [NexusKey(1)] public int B { get; set; }
            }
            """, "Message");

        await Assert.That(shape.Members.Select(m => m.Key).ToArray()).IsEquivalentTo(new[] { 0, 1, 3 }, CollectionOrdering.Matching);
        await Assert.That(shape.Members.Select(m => m.Name).ToArray()).IsEquivalentTo(new[] { "A", "B", "C" }, CollectionOrdering.Matching);
        await Assert.That(shape.Members.Select(m => m.LocalName).ToArray()).IsEquivalentTo(new[] { "__m0", "__m1", "__m2" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task NexusIgnoreRemovesKeyedMember()
    {
        var shape = GetObject("""
            [NexusObject] public class Message
            {
                [NexusKey(0)] public int A { get; set; }
                [NexusKey(1), NexusIgnore] public int B { get; set; }
            }
            """, "Message");

        await Assert.That(shape.Members.Select(m => m.Name).ToArray()).IsEquivalentTo(new[] { "A" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConstructedGenericHasSubstitutedMembers()
    {
        var (shapes, compilation) = Build("""
            [NexusObject] public class Envelope<T> { [NexusKey(0)] public T Value { get; set; } = default!; }
            """);
        var definition = compilation.GetTypeByMetadataName("Envelope`1")!;
        var constructed = definition.Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        var shape = (ObjectShape)shapes.Get(constructed);
        var member = (NamedShape)shape.Members.Single().Type;
        await Assert.That(member.Kind).IsEqualTo(NamedKind.Special);
        await Assert.That(member.MetadataName).IsEqualTo("System.Int32");

        var open = (ObjectShape)shapes.Get(definition);
        await Assert.That(open).IsNotSameReferenceAs(shape);
        await Assert.That(open.Members.Single().Type).IsAssignableTo<TypeParameterShape>();
    }

    [Test]
    public async Task SelfReferenceReturnsSameShape()
    {
        var shape = GetObject("""
            [NexusObject] public class Node
            {
                [NexusKey(0)] public int Value { get; set; }
                [NexusKey(1)] public Node? Next { get; set; }
            }
            """, "Node");

        await Assert.That(shape.Members[1].Type).IsSameReferenceAs(shape);
    }

    [Test]
    public async Task ReferenceNullabilityGivesSameShape()
    {
        var shape = GetObject("""
            [NexusObject] public class Message
            {
                [NexusKey(0)] public string A { get; set; } = "";
                [NexusKey(1)] public string? B { get; set; }
            }
            """, "Message");

        await Assert.That(shape.Members[0].Type).IsAssignableTo<NamedShape>();
        await Assert.That(shape.Members[1].Type).IsSameReferenceAs(shape.Members[0].Type);
        await Assert.That(shape.Members[1].DeclaredType.NullableAnnotation).IsEqualTo(NullableAnnotation.Annotated);
    }
}

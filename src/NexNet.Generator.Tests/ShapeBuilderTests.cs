using Microsoft.CodeAnalysis;
using NexNet.Generator.Serialization;
using NUnit.Framework;

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
                   ?? throw new AssertionException($"Type '{typeName}' not found.");
        return (ObjectShape)shapes.Get(type);
    }

    [Test]
    public void MembersAreInKeyOrder()
    {
        var shape = GetObject("""
            [NexusObject] public class Message
            {
                [NexusKey(3)] public int C { get; set; }
                [NexusKey(0)] public int A { get; set; }
                [NexusKey(1)] public int B { get; set; }
            }
            """, "Message");

        Assert.That(shape.Members.Select(m => m.Key), Is.EqualTo(new[] { 0, 1, 3 }));
        Assert.That(shape.Members.Select(m => m.Name), Is.EqualTo(new[] { "A", "B", "C" }));
        Assert.That(shape.Members.Select(m => m.LocalName), Is.EqualTo(new[] { "__m0", "__m1", "__m2" }));
    }

    [Test]
    public void NexusIgnoreRemovesKeyedMember()
    {
        var shape = GetObject("""
            [NexusObject] public class Message
            {
                [NexusKey(0)] public int A { get; set; }
                [NexusKey(1), NexusIgnore] public int B { get; set; }
            }
            """, "Message");

        Assert.That(shape.Members.Select(m => m.Name), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public void ConstructedGenericHasSubstitutedMembers()
    {
        var (shapes, compilation) = Build("""
            [NexusObject] public class Envelope<T> { [NexusKey(0)] public T Value { get; set; } = default!; }
            """);
        var definition = compilation.GetTypeByMetadataName("Envelope`1")!;
        var constructed = definition.Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        var shape = (ObjectShape)shapes.Get(constructed);
        var member = (NamedShape)shape.Members.Single().Type;
        Assert.That(member.Kind, Is.EqualTo(NamedKind.Special));
        Assert.That(member.MetadataName, Is.EqualTo("System.Int32"));

        var open = (ObjectShape)shapes.Get(definition);
        Assert.That(open, Is.Not.SameAs(shape));
        Assert.That(open.Members.Single().Type, Is.InstanceOf<TypeParameterShape>());
    }

    [Test]
    public void SelfReferenceReturnsSameShape()
    {
        var shape = GetObject("""
            [NexusObject] public class Node
            {
                [NexusKey(0)] public int Value { get; set; }
                [NexusKey(1)] public Node? Next { get; set; }
            }
            """, "Node");

        Assert.That(shape.Members[1].Type, Is.SameAs(shape));
    }

    [Test]
    public void ReferenceNullabilityGivesSameShape()
    {
        var shape = GetObject("""
            [NexusObject] public class Message
            {
                [NexusKey(0)] public string A { get; set; } = "";
                [NexusKey(1)] public string? B { get; set; }
            }
            """, "Message");

        Assert.That(shape.Members[0].Type, Is.InstanceOf<NamedShape>());
        Assert.That(shape.Members[1].Type, Is.SameAs(shape.Members[0].Type));
        Assert.That(shape.Members[1].DeclaredType.NullableAnnotation, Is.EqualTo(NullableAnnotation.Annotated));
    }
}

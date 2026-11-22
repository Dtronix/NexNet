using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;
using NexNet.Generator.Targets.JavaScript;

namespace NexNet.Generator.Tests.Targets;

/// <summary>The name mappers: camelCase, '_' stripping and keyword escaping.</summary>
public class NameMapperTests
{
    private static INameMapper Java => JavaTarget.Instance.Names;

    private static INameMapper Js => JavaScriptTarget.Instance.Names;

    [Test]
    [Arguments("Add", "add")]
    [Arguments("add", "add")]
    [Arguments("GetUserID", "getUserID")]
    [Arguments("ID", "id")]
    [Arguments("IOStream", "ioStream")]
    [Arguments("X", "x")]
    [Arguments("_count", "count")]
    [Arguments("__Value", "value")]
    public async Task Method_IsCamelCaseWithoutLeadingUnderscores(string name, string expected)
    {
        await Assert.That(Java.Method(name)).IsEqualTo(expected);
        await Assert.That(Js.Method(name)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("class", "class_", "class_")]
    [Arguments("Class", "class_", "class_")]
    [Arguments("default", "default_", "default_")]
    [Arguments("Default", "default_", "default_")]
    [Arguments("function", "function", "function_")]
    [Arguments("Function", "function", "function_")]
    [Arguments("int", "int_", "int")]
    [Arguments("Package", "package_", "package_")]
    [Arguments("await", "await", "await_")]
    [Arguments("record", "record_", "record")]
    public async Task Parameter_EscapesKeywords(string name, string java, string js)
    {
        await Assert.That(Java.Parameter(name)).IsEqualTo(java);
        await Assert.That(Js.Parameter(name)).IsEqualTo(js);
    }

    [Test]
    [Arguments("ToString", "toString_", "toString_")]
    [Arguments("HashCode", "hashCode_", "hashCode")]
    [Arguments("GetClass", "getClass_", "getClass")]
    [Arguments("Constructor", "constructor", "constructor_")]
    [Arguments("Then", "then", "then_")]
    [Arguments("Default", "default_", "default_")]
    public async Task MethodAndProperty_AvoidInheritedMembers(string name, string java, string js)
    {
        await Assert.That(Java.Method(name)).IsEqualTo(java);
        await Assert.That(Java.Property(name)).IsEqualTo(java);
        await Assert.That(Js.Method(name)).IsEqualTo(js);
        await Assert.That(Js.Property(name)).IsEqualTo(js);
    }

    [Test]
    public async Task UnderscoreOnly_IsKeptAndEscapedWhereItIsAKeyword()
    {
        await Assert.That(Java.Parameter("_")).IsEqualTo("__");
        await Assert.That(Js.Parameter("_")).IsEqualTo("_");
    }

    [Test]
    public async Task ParameterIsNotEscapedForInheritedMembers()
    {
        await Assert.That(Java.Parameter("ToString")).IsEqualTo("toString");
        await Assert.That(Js.Parameter("Constructor")).IsEqualTo("constructor");
    }

    [Test]
    public async Task TypeAndEnumMember_KeepTheirCaseAndEscapeKeywords()
    {
        await Assert.That(Java.Type("ChatServer")).IsEqualTo("ChatServer");
        await Assert.That(Js.Type("ChatServer")).IsEqualTo("ChatServer");
        await Assert.That(Java.EnumMember("Red")).IsEqualTo("Red");
        await Assert.That(Java.EnumMember("default")).IsEqualTo("default_");
        await Assert.That(Js.EnumMember("function")).IsEqualTo("function_");
        await Assert.That(Js.Type("string")).IsEqualTo("string_");
    }

    [Test]
    [Arguments("com.acme", true)]
    [Arguments("com.acme.chat_v1", true)]
    [Arguments("a", true)]
    [Arguments("com..acme", false)]
    [Arguments("com.default", false)]
    [Arguments("com.1acme", false)]
    [Arguments("com.acme-chat", false)]
    [Arguments("", false)]
    public async Task JavaPackageNames(string package, bool valid)
    {
        await Assert.That(JavaTarget.IsValidPackage(package)).IsEqualTo(valid);
    }
}

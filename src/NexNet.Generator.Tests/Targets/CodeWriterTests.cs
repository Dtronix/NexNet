using NexNet.Generator.Targets;

namespace NexNet.Generator.Tests.Targets;

public class CodeWriterTests
{
    [Test]
    public async Task Blocks_IndentAndUseLfLineEndings()
    {
        var writer = new CodeWriter();
        writer.Line("package com.acme;").Line();
        using (writer.Block("public final class Chat"))
        {
            using (writer.Block("void run()"))
                writer.Lines("a();\r\nb();");
        }

        await Assert.That(writer.ToString()).IsEqualTo(
            "package com.acme;\n\npublic final class Chat {\n    void run() {\n        a();\n        b();\n    }\n}\n");
        await Assert.That(writer.IndentLevel).IsEqualTo(0);
    }

    [Test]
    public async Task Unindent_BelowZeroThrows()
    {
        await Assert.That(() => new CodeWriter().Unindent()).Throws<InvalidOperationException>();
    }
}

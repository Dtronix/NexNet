using NexNet.Interop.Hosts;
using NexNet.Interop.Remote;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests;

public class HostLaunchConfigTests
{
    [Test]
    public async Task Parse_ReadsEveryMember()
    {
        var root = Path.GetTempPath();
        var config = HostLaunchConfig.Parse(
            """{"command":"node","args":["host.js","--x"],"workingDir":"clients/js","env":{"A":"1"},"maxConcurrency":6,"startupTimeoutMs":1500}""",
            root);
        await Assert.That(config.Command).IsEqualTo("node");
        await Assert.That(config.Args).IsEquivalentTo(new[] { "host.js", "--x" });
        await Assert.That(config.WorkingDir).IsEqualTo(Path.GetFullPath(Path.Combine(root, "clients", "js")));
        await Assert.That(config.Env["A"]).IsEqualTo("1");
        await Assert.That(config.MaxConcurrency).IsEqualTo(6);
        await Assert.That(config.StartupTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1500));

        var defaults = HostLaunchConfig.Parse("""{"command":"java"}""", root);
        await Assert.That(defaults.MaxConcurrency).IsEqualTo(HostLaunchConfig.DefaultMaxConcurrency);
        await Assert.That(Path.TrimEndingDirectorySeparator(defaults.WorkingDir)).IsEqualTo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        await Assert.That(() => HostLaunchConfig.Parse("""{"args":[]}""", root)).Throws<FormatException>();
        await Assert.That(() => HostLaunchConfig.Parse("not json", root)).Throws<FormatException>();
    }

    [Test]
    public async Task Load_OfAMissingHost_NamesTheBuildCommand()
    {
        var root = Directory.CreateTempSubdirectory("nexnet-h05-").FullName;
        try
        {
            var node = Assert.Throws<HostNotBuiltException>(() => HostLaunchConfig.Load("node", root));
            await Assert.That(node!.Message).Contains(HostBuildCommands.JavaScript);
            var java = Assert.Throws<HostNotBuiltException>(() => HostLaunchConfig.Load("java", root));
            await Assert.That(java!.Message).Contains(HostBuildCommands.Java);

            // Browsers fall back to the shared browser host.
            Directory.CreateDirectory(Path.Combine(root, "artifacts", "interop", "browser"));
            await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "interop", "browser", "host.json"), """{"command":"node","args":["launch.js"]}""");
            var browser = HostLaunchConfig.Load("browser-webkit", root);
            await Assert.That(browser.BuildArguments("browser-webkit", "127.0.0.1:5", "tok"))
                .IsEquivalentTo(new[] { "launch.js", "--control", "127.0.0.1:5", "--token", "tok", "--browser", "webkit" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task SchemaHash_IsTheSha256OfTheIrFile()
    {
        var root = HostLaunchConfig.FindRepoRoot();
        await Assert.That(root).IsNotNull();
        var path = SchemaHash.IrPath(root!);
        await Assert.That(path).EndsWith(Path.Combine("NexNet.TestNexus", "obj", "nexnet-ir.json"));

        var temp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(temp, "abc");
            await Assert.That(SchemaHash.OfFile(temp)).IsEqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        }
        finally
        {
            File.Delete(temp);
        }

        // The TestNexus project is built before this one, so the IR exists.
        await Assert.That(SchemaHash.Expected(root!).Length).IsEqualTo(64);
    }

    [Test]
    public async Task ControlJson_WritesSpecsTypesAndNexusNames()
    {
        var spec = ControlJson.ToJson(new ClientSpec { Transport = ClientTransport.Tcp, Address = "tcp://h:1", ReconnectDelaysMs = [1, 2], FakeTime = true });
        await Assert.That(spec.Select(p => p.Key)).IsEquivalentTo(new[] { "transport", "address", "reconnectDelaysMs", "fakeTime" });
        await Assert.That(spec["transport"]!.GetValue<string>()).IsEqualTo("Tcp");

        await Assert.That(ControlJson.TypeExpression(TypedCodecs.Get<InteropItem>().Schema)).IsEqualTo("NexNet.TestNexus.Interop.InteropItem");
        await Assert.That(ControlJson.TypeExpression(TypedCodecs.Get<long>().Schema)).IsEqualTo("long");
        await Assert.That(ControlJson.TypeExpression(TypeSchema.Map(TypeSchema.Primitive(SchemaPrim.String), TypeSchema.Seq(TypeSchema.Primitive(SchemaPrim.Int32), "List"))))
            .IsEqualTo("Dictionary<string,List<int>>");
        await Assert.That(ControlJson.TypeExpression(TypeSchema.Nullable(TypeSchema.Tuple(TypeSchema.Primitive(SchemaPrim.Guid), TypeSchema.Bin()))))
            .IsEqualTo("(Guid,byte[])?");

        var (client, server) = ControlJson.NexusPair<InteropClientNexus, InteropClientNexus.ServerProxy>();
        await Assert.That(client).IsEqualTo(typeof(IInteropClientNexus).FullName);
        await Assert.That(server).IsEqualTo(typeof(IInteropServerNexus).FullName);
    }

    [Test]
    public async Task RetryOnHostCrash_RetriesOnlyHostCrashes()
    {
        var crash = new HostCrashedException("gone");
        await Assert.That(RetryOnHostCrashAttribute.IsHostCrash(crash)).IsTrue();
        await Assert.That(RetryOnHostCrashAttribute.IsHostCrash(new InvalidOperationException("wrapped", crash))).IsTrue();
        await Assert.That(RetryOnHostCrashAttribute.IsHostCrash(new AggregateException(new TimeoutException(), crash))).IsTrue();
        await Assert.That(RetryOnHostCrashAttribute.IsHostCrash(new TimeoutException())).IsFalse();
        await Assert.That(new RetryOnHostCrashAttribute().Times).IsEqualTo(1);
    }

    [Test]
    public async Task ParallelLimit_DefaultsWhenTheHostIsNotBuilt()
    {
        var limit = LanguageParallelLimit.For("no-such-language");
        await Assert.That(limit.Limit).IsEqualTo(HostLaunchConfig.DefaultMaxConcurrency);
        await Assert.That(LanguageParallelLimit.For("no-such-language")).IsSameReferenceAs(limit);
    }
}

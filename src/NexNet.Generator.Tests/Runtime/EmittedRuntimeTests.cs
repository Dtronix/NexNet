using System.Text.Json;
using System.Text.Json.Nodes;
using NexNet.Generator.Bundle;
using NexNet.Generator.Runtime;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;
using NexNet.Generator.Targets.JavaScript;
using NexNet.Generator.Tests.Bundle;
using NexNet.Generator.Tests.Targets.Java;
using NexNet.Generator.Tests.Targets.JavaScript;

namespace NexNet.Generator.Tests.Runtime;

/// <summary>
/// The client runtime the generator emits beside the generated code (D31, docs/internals/generator-emitted-runtime.md):
/// embedding, layout, options, version stamp, determinism and, SDK-gated (D27), that the output compiles and runs with
/// nothing else: <c>javac --release 11</c> with no class path, <c>tsc</c> and Node with no workspace package.
/// </summary>
public class EmittedRuntimeTests
{
    private const string JavaRuntime = "java/" + JavaTarget.RuntimePath;
    private const string JsRuntime = "javascript/" + JavaScriptTarget.RuntimeDirectory + "/";

    private static readonly SemaphoreSlim ToolLock = new(2, 2);

    // ------------------------------------------------------------------ embedding

    [Test]
    public async Task EmbeddedSourcesAreTheWorkspaceSources()
    {
        var js = Path.Combine(Repo, "clients", "js", "packages", "nexnet");
        await AssertEmbedded("javascript", 50, (Path.Combine(js, "src"), "*.js"), (Path.Combine(js, "types"), "*.d.ts"));
        await AssertEmbedded("java", 50, (Path.Combine(Repo, "clients", "java", "nexnet-client", "src", "main", "java"), "*.java"));
        foreach (var module in JavaTarget.RuntimeModuleNames)
        {
            var root = Path.Combine(Repo, "clients", "java", "nexnet-client-" + module, "src", "main", module == "kotlin" ? "kotlin" : "java");
            await AssertEmbedded(JavaTarget.ModuleResourceTarget(module), 0, (root, module == "kotlin" ? "*.kt" : "*.java"));
        }

        await Assert.That(EmbeddedRuntime.License).IsEqualTo(EmbeddedRuntime.Normalize(System.IO.File.ReadAllText(Path.Combine(Repo, "LICENSE"))));
        await Assert.That(EmbeddedRuntime.License).Contains("MIT License");
    }

    /// <summary>The embedded sources of a target are exactly the files of every (root, pattern), merged by relative path; each set has more than <paramref name="minimum"/> files.</summary>
    private static async Task AssertEmbedded(string target, int minimum, params (string Root, string Pattern)[] sets)
    {
        var onDisk = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (root, pattern) in sets)
        {
            var files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
            await Assert.That(files.Length).IsGreaterThan(minimum).Because(root + " " + pattern);
            foreach (var path in files)
                onDisk.Add(Path.GetRelativePath(root, path).Replace('\\', '/'), path);
        }

        var embedded = EmbeddedRuntime.Sources(target);
        await Assert.That(embedded.Select(f => f.Path).ToArray()).IsEquivalentTo(onDisk.Keys.ToArray());
        foreach (var file in embedded)
            await Assert.That(file.Content).IsEqualTo(EmbeddedRuntime.Normalize(System.IO.File.ReadAllText(onDisk[file.Path])));
    }

    [Test]
    public async Task EveryJavaScriptModuleHasItsDeclarations()
    {
        // J27: the committed tsc output has one .d.ts per runtime module, emitted beside it.
        var paths = EmbeddedRuntime.Sources("javascript").Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var modules = paths.Where(p => p.EndsWith(".js", StringComparison.Ordinal)).ToArray();
        await Assert.That(modules.Where(p => !paths.Contains(p.Substring(0, p.Length - 3) + ".d.ts"))).IsEmpty();
        await Assert.That(paths.Count).IsEqualTo(modules.Length * 2);

        var emitted = Bundle(Options()).Select(f => f.Path).ToArray();
        await Assert.That(emitted).Contains(JsRuntime + "index.d.ts");
        await Assert.That(emitted).Contains(JsRuntime + "node.d.ts");
        await Assert.That(emitted).Contains(JsRuntime + "diagnostics.d.ts");
    }

    // ------------------------------------------------------------------ layout and options

    [Test]
    public async Task TheRuntimeIsEmittedByDefault()
    {
        var files = Bundle(Options());
        var paths = files.Select(f => f.Path).ToArray();

        await Assert.That(paths).Contains(JavaRuntime + "NexusRuntime.java");
        await Assert.That(paths).Contains(JavaRuntime + "client/NexusClient.java");
        await Assert.That(paths).Contains(JavaRuntime + "NexusRuntimeVersion.java");
        await Assert.That(paths).Contains(JavaRuntime + "LICENSE.txt");
        await Assert.That(paths.Count(p => p.StartsWith(JavaRuntime, StringComparison.Ordinal))).IsEqualTo(EmbeddedRuntime.Sources("java").Count + 2);

        await Assert.That(paths).Contains(JsRuntime + "index.js");
        await Assert.That(paths).Contains(JsRuntime + "node.js");
        await Assert.That(paths).Contains(JsRuntime + "package.json");
        await Assert.That(paths).Contains(JsRuntime + "LICENSE");
        await Assert.That(paths.Count(p => p.StartsWith(JsRuntime, StringComparison.Ordinal))).IsEqualTo(EmbeddedRuntime.Sources("javascript").Count + 2);

        // Generated code is still where it was.
        await Assert.That(paths).Contains("java/com/acme/ProofServerProxy.java");
        await Assert.That(paths).Contains("javascript/ProofServerNexus.js");
    }

    [Test]
    public async Task EmitRuntimeFalseEmitsOnlyTheGeneratedCode()
    {
        var files = Bundle(Options(("java_EmitRuntime", "false"), ("javascript_EmitRuntime", "False")));

        await Assert.That(files.Any(f => f.Path.StartsWith(JavaRuntime, StringComparison.Ordinal))).IsFalse();
        await Assert.That(files.Any(f => f.Path.StartsWith(JsRuntime, StringComparison.Ordinal))).IsFalse();
        // The runtime then comes from the nexnet-client jar, which brings JSpecify: the annotations stay.
        await Assert.That(FileOf(files, "java/com/acme/Note.java")).Contains("private @Nullable String text;");
        await Assert.That(FileOf(files, "java/com/acme/Note.java")).Contains("import org.jspecify.annotations.Nullable;");
    }

    [Test]
    public async Task InRepoHostsTurnTheRuntimeOffImplicitly()
    {
        // Test scaffolding (both targets) and a JavaScript ModuleName: the in-repo hosts use the workspace runtime.
        var files = Bundle(Options(("java_EmitTestScaffolding", "true"), ("javascript_ModuleName", "nexnet-testnexus")));
        await Assert.That(files.Any(f => f.Path.StartsWith(JavaRuntime, StringComparison.Ordinal))).IsFalse();
        await Assert.That(files.Any(f => f.Path.StartsWith(JsRuntime, StringComparison.Ordinal))).IsFalse();

        files = Bundle(Options(("javascript_EmitTestScaffolding", "true")));
        await Assert.That(files.Any(f => f.Path.StartsWith(JsRuntime, StringComparison.Ordinal))).IsFalse();

        // An explicit EmitRuntime wins.
        files = Bundle(Options(("java_EmitTestScaffolding", "true"), ("java_EmitRuntime", "true"),
            ("javascript_ModuleName", "nexnet-testnexus"), ("javascript_EmitRuntime", "true")));
        await Assert.That(files.Any(f => f.Path == JavaRuntime + "NexusRuntime.java")).IsTrue();
        await Assert.That(files.Any(f => f.Path == JsRuntime + "index.js")).IsTrue();
        await Assert.That(FileOf(files, "javascript/ProofServerNexus.js")).Contains("from 'nexnet-testnexus';");
    }

    [Test]
    public async Task OnlyTargetsWithAnExportGetTheRuntime()
    {
        var files = Bundle(Options(), EmittedRuntimeFixture.Source.Replace("NexusTargets.Java | NexusTargets.JavaScript", "NexusTargets.Java", StringComparison.Ordinal));
        await Assert.That(files.Any(f => f.Path.StartsWith(JavaRuntime, StringComparison.Ordinal))).IsTrue();
        await Assert.That(files.Any(f => f.Path.StartsWith("javascript/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task RelativeImportPointsAtTheRuntimeFolder()
    {
        var files = Bundle(Options(("javascript_RuntimeImport", "relative")));
        var generated = files.Where(f => f.Path.StartsWith("javascript/", StringComparison.Ordinal) && !f.Path.StartsWith(JsRuntime, StringComparison.Ordinal)).ToArray();

        await Assert.That(FileOf(files, "javascript/ProofServerNexus.js")).Contains("import * as $rt from './nexnet-runtime/index.js';");
        await Assert.That(FileOf(files, "javascript/ProofServerNexus.d.ts")).Contains("import * as $rt from './nexnet-runtime/index.js';");
        await Assert.That(generated.Where(f => f.Content.Contains("@dtronix/nexnet", StringComparison.Ordinal)).Select(f => f.Path)).IsEmpty();

        // Files in a sub-folder (the test scaffolding) go up a level.
        files = Bundle(Options(("javascript_RuntimeImport", "Relative"), ("javascript_EmitTestScaffolding", "true"), ("javascript_EmitRuntime", "true")));
        var scaffolding = files.Where(f => f.Path.StartsWith("javascript/scaffolding/", StringComparison.Ordinal)).ToArray();
        await Assert.That(scaffolding.Length).IsGreaterThan(0);
        await Assert.That(scaffolding.Any(f => f.Content.Contains("'../nexnet-runtime/index.js'", StringComparison.Ordinal))).IsTrue();
        await Assert.That(scaffolding.Where(f => f.Content.Contains("'./nexnet-runtime/", StringComparison.Ordinal)).Select(f => f.Path)).IsEmpty();
    }

    [Test]
    public async Task PackageJsonHasTheWorkspaceExportsAndTypes()
    {
        var emitted = JsonNode.Parse(FileOf(Bundle(Options()), JsRuntime + "package.json"))!;
        var workspace = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(Repo, "clients", "js", "packages", "nexnet", "package.json")))!;

        // The workspace exports with src/ and the declaration folder as the root: the emitted runtime has each .d.ts
        // beside its .js (J27).
        var expected = workspace["exports"]!.DeepClone().AsObject();
        foreach (var entry in expected.ToArray())
        {
            if (entry.Value is JsonObject conditions)
            {
                foreach (var condition in conditions.ToArray())
                {
                    conditions[condition.Key] = condition.Value!.GetValue<string>()
                        .Replace("./src/", "./", StringComparison.Ordinal)
                        .Replace("./dist/", "./", StringComparison.Ordinal)
                        .Replace("./types/", "./", StringComparison.Ordinal);
                }
            }
        }

        await Assert.That(JsonNode.DeepEquals(emitted["exports"], expected)).IsTrue()
            .Because("emitted: " + emitted["exports"]!.ToJsonString() + "\nexpected: " + expected.ToJsonString());
        await Assert.That(emitted["name"]!.GetValue<string>()).IsEqualTo(workspace["name"]!.GetValue<string>());
        await Assert.That(emitted["type"]!.GetValue<string>()).IsEqualTo("module");
        await Assert.That(emitted["private"]!.GetValue<bool>()).IsTrue();
        await Assert.That(emitted["types"]!.GetValue<string>()).IsEqualTo("./index.d.ts");

        // Every types condition names an emitted declaration file.
        var paths = Bundle(Options()).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in emitted["exports"]!.AsObject())
        {
            if (entry.Value is JsonObject conditions && conditions["types"] is { } types)
                await Assert.That(paths).Contains(JsRuntime + types.GetValue<string>().Substring(2));
        }
    }

    // ------------------------------------------------------------------ optional Java modules (G10)

    [Test]
    public async Task RuntimeModulesAreOptIn()
    {
        var paths = Bundle(Options()).Select(f => f.Path).ToArray();
        foreach (var module in JavaTarget.RuntimeModuleNames)
            await Assert.That(paths.Any(p => p.StartsWith(JavaRuntime + module + "/", StringComparison.Ordinal))).IsFalse().Because(module);
    }

    [Test]
    public async Task ListedRuntimeModulesAreEmittedInTheirPackages()
    {
        // ';' as an MSBuild list (after flattening it arrives as ','), any case, spaces and duplicates.
        var files = Bundle(Options(("java_EmitRuntimeModules", "Kotlin, okhttp;okhttp  micrometer,android")));
        var paths = files.Select(f => f.Path).ToArray();
        var header = EmbeddedRuntime.HeaderLine("//") + "\n";

        foreach (var module in JavaTarget.RuntimeModuleNames)
        {
            var sources = EmbeddedRuntime.Sources(JavaTarget.ModuleResourceTarget(module));
            await Assert.That(sources.Count).IsGreaterThan(0).Because(module);
            foreach (var source in sources)
            {
                await Assert.That(source.Path.StartsWith(JavaTarget.RuntimePath + module + "/", StringComparison.Ordinal)).IsTrue().Because(source.Path);
                await Assert.That(FileOf(files, "java/" + source.Path).StartsWith(header, StringComparison.Ordinal)).IsTrue().Because(source.Path);
            }

            await Assert.That(paths.Count(p => p.StartsWith(JavaRuntime + module + "/", StringComparison.Ordinal))).IsEqualTo(sources.Count).Because(module);
        }

        await Assert.That(paths).Contains(JavaRuntime + "kotlin/NexusCoroutines.kt");
        await Assert.That(paths).Contains(JavaRuntime + "okhttp/OkHttpWebSocketConnector.java");
        await Assert.That(paths).Contains(JavaRuntime + "micrometer/NexusMicrometerBinder.java");
        await Assert.That(paths).Contains(JavaRuntime + "android/NexusAndroid.java");
        // The core runtime is unchanged.
        await Assert.That(paths.Count(p => p.StartsWith(JavaRuntime, StringComparison.Ordinal)))
            .IsEqualTo(EmbeddedRuntime.Sources("java").Count + 2 + JavaTarget.RuntimeModuleNames.Sum(m => EmbeddedRuntime.Sources(JavaTarget.ModuleResourceTarget(m)).Count));

        // Only the listed modules.
        paths = Bundle(Options(("java_EmitRuntimeModules", "okhttp"))).Select(f => f.Path).ToArray();
        await Assert.That(paths).Contains(JavaRuntime + "okhttp/OkHttpWebSocketTransport.java");
        await Assert.That(paths.Any(p => p.StartsWith(JavaRuntime + "kotlin/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task RuntimeModulesFollowTheNullnessOption()
    {
        var files = Bundle(Options(("java_EmitRuntimeModules", "android,kotlin,micrometer,okhttp")));
        var modules = files.Where(f => JavaTarget.RuntimeModuleNames.Any(m => f.Path.StartsWith(JavaRuntime + m + "/", StringComparison.Ordinal))).ToArray();
        await Assert.That(modules.Where(f => f.Content.Contains("jspecify", StringComparison.Ordinal) || f.Content.Contains("@Nullable", StringComparison.Ordinal)).Select(f => f.Path)).IsEmpty();
        await Assert.That(FileOf(files, JavaRuntime + "okhttp/OkHttpWebSocketTransport.java")).Contains("private volatile WebSocket webSocket;");

        files = Bundle(Options(("java_EmitRuntimeModules", "okhttp"), ("java_Nullness", "jspecify")));
        await Assert.That(FileOf(files, JavaRuntime + "okhttp/OkHttpWebSocketTransport.java")).Contains("private volatile @Nullable WebSocket webSocket;");
    }

    [Test]
    public async Task InvalidRuntimeModulesAreNexnet045()
    {
        var run = BundleTestRunner.Run(Options(("java_EmitRuntimeModules", "kotlin,retrofit,Ktor")), EmittedRuntimeFixture.Source);
        var messages = run.Diagnostics.Where(d => d.Id == "NEXNET045").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await Assert.That(run.Bundle).IsNull();
        await Assert.That(messages.Any(m => m.Contains("EmitRuntimeModules of the java target lists 'ktor', 'retrofit'; the modules are android, kotlin, micrometer, okhttp", StringComparison.Ordinal)))
            .IsTrue().Because(string.Join("\n", messages));

        // Modules extend the emitted runtime; without it they have nothing to build on.
        run = BundleTestRunner.Run(Options(("java_EmitRuntimeModules", "kotlin"), ("java_EmitRuntime", "false")), EmittedRuntimeFixture.Source);
        messages = run.Diagnostics.Where(d => d.Id == "NEXNET045").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await Assert.That(run.Bundle).IsNull();
        await Assert.That(messages.Any(m => m.Contains("EmitRuntimeModules of the java target needs the emitted runtime", StringComparison.Ordinal)))
            .IsTrue().Because(string.Join("\n", messages));
    }

    // ------------------------------------------------------------------ version, licence, determinism

    [Test]
    public async Task EveryRuntimeFileCarriesTheVersionedHeader()
    {
        var version = typeof(EmbeddedRuntime).Assembly.GetName().Version!;
        await Assert.That(EmbeddedRuntime.Version).IsEqualTo(version.Major + "." + version.Minor + "." + version.Build);

        // Lockstep: the generator, the npm package and the emitted runtime have one version.
        var workspace = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(Repo, "clients", "js", "packages", "nexnet", "package.json")))!;
        await Assert.That(workspace["version"]!.GetValue<string>()).IsEqualTo(EmbeddedRuntime.Version);

        var files = Bundle(Options());
        var header = EmbeddedRuntime.HeaderLine("//") + "\n";
        await Assert.That(header).Contains(EmbeddedRuntime.Version);
        await Assert.That(header).Contains("MIT License");
        foreach (var file in files.Where(f => (f.Path.StartsWith(JavaRuntime, StringComparison.Ordinal) && f.Path.EndsWith(".java", StringComparison.Ordinal))
                                              || (f.Path.StartsWith(JsRuntime, StringComparison.Ordinal) && (f.Path.EndsWith(".js", StringComparison.Ordinal) || f.Path.EndsWith(".d.ts", StringComparison.Ordinal)))))
            await Assert.That(file.Content.StartsWith(header, StringComparison.Ordinal)).IsTrue().Because(file.Path);

        await Assert.That(FileOf(files, JavaRuntime + "NexusRuntimeVersion.java")).Contains("static final String VALUE = \"" + EmbeddedRuntime.Version + "\";");
        await Assert.That(JsonNode.Parse(FileOf(files, JsRuntime + "package.json"))!["version"]!.GetValue<string>()).IsEqualTo(EmbeddedRuntime.Version);
        await Assert.That(FileOf(files, JsRuntime + "LICENSE")).IsEqualTo(EmbeddedRuntime.License);
        await Assert.That(FileOf(files, JavaRuntime + "LICENSE.txt")).IsEqualTo(EmbeddedRuntime.License);
    }

    [Test]
    public async Task OutputIsByteIdenticalAndLf()
    {
        var first = BundleTestRunner.Run(Options(), EmittedRuntimeFixture.Source).Bundle;
        var second = BundleTestRunner.Run(Options(), EmittedRuntimeFixture.Source).Bundle;
        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);

        foreach (var file in TargetBundle.Read(first!))
        {
            await Assert.That(file.Content.Contains('\r')).IsFalse().Because(file.Path);
            await Assert.That(file.Content.Length > 0 && file.Content[0] == (char)0xFEFF).IsFalse().Because(file.Path);
        }
    }

    // ------------------------------------------------------------------ JSpecify

    [Test]
    public async Task NoJSpecifyReferenceIsLeftByDefault()
    {
        var java = Bundle(Options()).Where(f => f.Path.EndsWith(".java", StringComparison.Ordinal)).ToArray();
        await Assert.That(java.Length).IsGreaterThan(150);
        await Assert.That(java.Where(f => f.Content.Contains("jspecify", StringComparison.Ordinal) || f.Content.Contains("@Nullable", StringComparison.Ordinal)).Select(f => f.Path)).IsEmpty();
        await Assert.That(FileOf(java, "java/com/acme/Note.java")).Contains("private String text;");
    }

    [Test]
    public async Task NullnessJSpecifyKeepsTheAnnotations()
    {
        var files = Bundle(Options(("java_Nullness", "jspecify")));
        await Assert.That(FileOf(files, "java/com/acme/Note.java")).Contains("private @Nullable String text;");
        await Assert.That(FileOf(files, JavaRuntime + "serialization/Formatters.java")).Contains("import org.jspecify.annotations.Nullable;");
    }

    [Test]
    [Arguments("import org.jspecify.annotations.Nullable;\nimport java.util.List;\n", "import java.util.List;\n")]
    [Arguments("private @Nullable String name;", "private String name;")]
    [Arguments("java.util.@Nullable List<@Nullable T> items", "java.util.List<T> items")]
    [Arguments("byte @Nullable [] data, int @Nullable[] more", "byte[] data, int[] more")]
    [Arguments("public static <T> @Nullable T read(@Nullable Object o)", "public static <T> T read(Object o)")]
    [Arguments("/** No annotation here. */ int x;", "/** No annotation here. */ int x;")]
    public async Task StripRemovesTypeUseAnnotations(string source, string expected)
    {
        await Assert.That(JavaNullness.Strip(source)).IsEqualTo(expected);
    }

    // ------------------------------------------------------------------ diagnostics

    [Test]
    public async Task InvalidOptionsAreNexnet045()
    {
        var run = BundleTestRunner.Run(Options(("java_EmitRuntime", "yes"), ("java_Nullness", "strict"), ("javascript_RuntimeImport", "cdn")), EmittedRuntimeFixture.Source);
        var messages = run.Diagnostics.Where(d => d.Id == "NEXNET045").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        await Assert.That(run.Bundle).IsNull();
        await Assert.That(messages.Any(m => m.Contains("EmitRuntime of the java target must be true or false, not 'yes'", StringComparison.Ordinal))).IsTrue();
        await Assert.That(messages.Any(m => m.Contains("Nullness of the java target must be none or jspecify, not 'strict'", StringComparison.Ordinal))).IsTrue();
        await Assert.That(messages.Any(m => m.Contains("RuntimeImport of the javascript target must be package or relative, not 'cdn'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AGeneratedFileOnARuntimePathIsNexnet047()
    {
        // A type named like a runtime class, generated into the runtime's own package.
        var options = Options();
        options["build_property.NexNetTarget_java_Package"] = "com.dtronix.nexnet";
        var run = BundleTestRunner.Run(options, EmittedRuntimeFixture.Source.Replace("class Note", "class Registration", StringComparison.Ordinal)
            .Replace("(Note note", "(Registration note", StringComparison.Ordinal).Replace("<Note?>", "<Registration?>", StringComparison.Ordinal));
        var messages = run.Diagnostics.Where(d => d.Id == "NEXNET047").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        await Assert.That(run.Bundle).IsNull();
        await Assert.That(messages.Any(m => m.Contains("'com/dtronix/nexnet/Registration.java'", StringComparison.Ordinal)
                                            && m.Contains("the Java client runtime (EmitRuntime)", StringComparison.Ordinal))).IsTrue()
            .Because(string.Join("\n", messages));

        // Without the runtime the same configuration is fine.
        options["build_property.NexNetTarget_java_EmitRuntime"] = "false";
        run = BundleTestRunner.Run(options, EmittedRuntimeFixture.Source.Replace("class Note", "class Registration", StringComparison.Ordinal)
            .Replace("(Note note", "(Registration note", StringComparison.Ordinal).Replace("<Note?>", "<Registration?>", StringComparison.Ordinal));
        await Assert.That(run.Diagnostics.Where(d => d.Id == "NEXNET047")).IsEmpty();
    }

    // ------------------------------------------------------------------ proof (SDK-gated)

    /// <summary>
    /// The Java output alone (generated code and emitted runtime, no jar, no JSpecify) compiles with
    /// <c>javac --release 11 -Xlint:all -Werror</c> and runs: a client from the generated factory and a round trip of a
    /// generated type through its generated formatter.
    /// </summary>
    [Test]
    public async Task JavaOutputCompilesAndRunsAlone()
    {
        var (javac, java) = JavaSdk.Tools();
        using var dir = new TempDir("nexnet-g09-java-");
        var files = Bundle(Options());
        var source = Path.Combine(dir.Root, "src");
        var classes = Path.Combine(dir.Root, "classes");
        var sources = new List<string>();
        foreach (var file in files.Where(f => f.Path.StartsWith("java/", StringComparison.Ordinal)))
            sources.Add(Write(source, file.Path.Substring("java/".Length), file.Content));
        sources.Add(Write(source, "proof/runner/Runner.java", JavaRunner));
        Directory.CreateDirectory(classes);

        var args = new List<string> { "--release", "11", "-encoding", "UTF-8", "-Xlint:all", "-Werror", "-Xdoclint:all,-missing", "-implicit:none", "-d", classes };
        args.AddRange(sources.Where(s => s.EndsWith(".java", StringComparison.Ordinal)));
        var argFile = Path.Combine(dir.Root, "javac.args");
        await System.IO.File.WriteAllLinesAsync(argFile, args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));

        var compiled = await Locked(() => JavaSdk.RunProcess(javac, new[] { "@" + argFile }, dir.Root, TimeSpan.FromMinutes(5)));
        await Assert.That(compiled.Success).IsTrue().Because("javac failed:\n" + compiled.Output);
        await Assert.That(compiled.Output.Trim()).IsEmpty().Because("javac printed:\n" + compiled.Output);

        var ran = await Locked(() => JavaSdk.RunProcess(java, new[] { "-cp", classes, "proof.runner.Runner" }, dir.Root, TimeSpan.FromMinutes(2)));
        await Assert.That(ran.Success).IsTrue().Because("The runner failed:\n" + ran.Output);
        await Assert.That(ran.Output).Contains("version=" + EmbeddedRuntime.Version + " hex=9207a26869 equal=true proxy=true");
    }

    /// <summary>
    /// <c>RuntimeImport=relative</c>: the JavaScript output alone, with no node_modules, type-checks with <c>tsc</c>
    /// (strict, no <c>allowJs</c>: the runtime's declarations, J27) and runs in Node.
    /// </summary>
    [Test]
    public async Task JavaScriptOutputRunsAloneWithRelativeImports()
    {
        var sdk = await NodeSdk.Get();
        using var dir = new TempDir("nexnet-g09-js-");
        foreach (var file in Bundle(Options(("javascript_RuntimeImport", "relative"))).Where(f => f.Path.StartsWith("javascript/", StringComparison.Ordinal)))
            Write(dir.Root, file.Path.Substring("javascript/".Length), file.Content);

        await TypeCheckAndRun(sdk, dir.Root, "./nexnet-runtime/index.js", expectNodeClient: false);
    }

    /// <summary>
    /// <c>RuntimeImport=package</c> (the default): with <c>node_modules/@dtronix/nexnet</c> linked to the emitted
    /// <c>nexnet-runtime</c> folder, as <c>npm install file:</c> does, the output type-checks and runs in Node, and the
    /// generated factory gets the Node client through the package's <c>node</c> condition.
    /// </summary>
    [Test]
    public async Task JavaScriptOutputRunsAloneAsAFileDependency()
    {
        var sdk = await NodeSdk.Get();
        using var dir = new TempDir("nexnet-g09-js-");
        foreach (var file in Bundle(Options()).Where(f => f.Path.StartsWith("javascript/", StringComparison.Ordinal)))
            Write(dir.Root, file.Path.Substring("javascript/".Length), file.Content);

        await NodeSdk.LinkRuntime(dir.Root, Path.Combine(dir.Root, JavaScriptTarget.RuntimeDirectory));
        try
        {
            await TypeCheckAndRun(sdk, dir.Root, "@dtronix/nexnet", expectNodeClient: true);
        }
        finally
        {
            NodeSdk.UnlinkRuntime(dir.Root);
        }
    }

    private static async Task TypeCheckAndRun(NodeSdk sdk, string root, string runtime, bool expectNodeClient)
    {
        Write(root, "package.json", "{ \"private\": true, \"type\": \"module\" }\n");
        Write(root, "usage.ts", JsUsage.Replace("__RUNTIME__", runtime, StringComparison.Ordinal));
        Write(root, "runner.js", JsRunner.Replace("__RUNTIME__", runtime, StringComparison.Ordinal)
            .Replace("__NODE_RUNTIME__", runtime == "@dtronix/nexnet" ? "@dtronix/nexnet/node" : "./nexnet-runtime/node.js", StringComparison.Ordinal)
            .Replace("__EXPECT_NODE__", expectNodeClient ? "true" : "false", StringComparison.Ordinal));

        var declarations = Directory.GetFiles(root, "*.d.ts").Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var config = new
        {
            compilerOptions = new Dictionary<string, object>
            {
                ["target"] = "ES2022",
                ["module"] = "NodeNext",
                ["moduleResolution"] = "NodeNext",
                ["lib"] = new[] { "ES2023", "DOM", "DOM.Iterable", "ESNext.Disposable" },
                ["types"] = Array.Empty<string>(),
                ["strict"] = true,
                ["noEmit"] = true,
                // J27: the emitted declarations type the runtime; no JavaScript is read.
                ["allowJs"] = false,
                ["skipLibCheck"] = false,
            },
            files = declarations.Append("usage.ts").ToArray(),
        };
        Write(root, "tsconfig.json", JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        var checkedTypes = await Locked(() => sdk.TypeCheck(Path.Combine(root, "tsconfig.json")));
        await Assert.That(checkedTypes.Success).IsTrue().Because("tsc failed:\n" + checkedTypes.Output);
        await Assert.That(checkedTypes.Output.Trim()).IsEmpty().Because("tsc printed:\n" + checkedTypes.Output);

        var ran = await Locked(() => sdk.RunScript(Path.Combine(root, "runner.js")));
        await Assert.That(ran.Success).IsTrue().Because("The runner failed:\n" + ran.Output);
        await Assert.That(ran.Output).Contains("version=" + EmbeddedRuntime.Version + " hex=9207a26869 equal=true client=true node=" + (expectNodeClient ? "true" : "false"));
    }

    private const string JavaRunner = """
        package proof.runner;

        import com.acme.Note;
        import com.acme.ProofClient;
        import com.acme.ProofClientNexusBase;
        import com.acme.ProofServerProxy;
        import com.dtronix.nexnet.NexusRuntime;
        import com.dtronix.nexnet.client.ClientConfig;
        import com.dtronix.nexnet.client.NexusClient;
        import com.dtronix.nexnet.serialization.Formatters;
        import java.util.concurrent.CompletableFuture;

        /** Uses the generated code and the emitted runtime, and nothing else. */
        public final class Runner {
            private Runner() {
            }

            private static final class Nexus extends ProofClientNexusBase {
                @Override
                protected CompletableFuture<Void> tell(Note note) {
                    return CompletableFuture.completedFuture(null);
                }
            }

            /**
             * Runs the checks.
             *
             * @param args unused
             */
            public static void main(String[] args) {
                Note note = new Note(7, "hi");
                byte[] bytes = Formatters.serialize(com.acme.formatters.Formatters.Note, note);
                StringBuilder hex = new StringBuilder();
                for (byte b : bytes) {
                    hex.append(String.format("%02x", b & 0xFF));
                }
                Note back = Formatters.deserialize(com.acme.formatters.Formatters.Note, bytes);
                boolean proxy;
                try (NexusClient<ProofServerProxy> client = ProofClient.create(ClientConfig.tcp("localhost", 1).build(), new Nexus())) {
                    proxy = client.proxy() != null;
                }
                System.out.println("version=" + NexusRuntime.VERSION + " hex=" + hex + " equal=" + note.equals(back) + " proxy=" + proxy);
            }
        }
        """;

    private const string JsUsage = """
        import { MsgPackWriter, NexusClient } from '__RUNTIME__';
        import { ProofClientNexusBase, ProofServerProxy, createProofClient } from './ProofServerNexus.js';
        import { NoteFormatter } from './formatters.js';
        import { Note } from './Note.js';

        class MyNexus extends ProofClientNexusBase {
          protected tell(note: Note): void {}
        }

        export const client: NexusClient<MyNexus, ProofServerProxy> = createProofClient({ webSocket: { url: 'ws://localhost:1' } }, new MyNexus());
        export const echoed: Promise<Note | null> = client.proxy.echo(new Note({ id: 1, text: null }), 'tag');
        const writer = new MsgPackWriter();
        NoteFormatter.write(writer, new Note({ id: 2, text: 'x' }));
        export const length: number = writer.toUint8Array().length;
        // @ts-expect-error a Note id is a number
        export const wrong: Note = new Note({ id: 'one' });
        """;

    private const string JsRunner = """
        import * as rt from '__RUNTIME__';
        import { NodeNexusClient } from '__NODE_RUNTIME__';
        import { ProofClientNexusBase, createProofClient } from './ProofServerNexus.js';
        import { NoteFormatter } from './formatters.js';
        import { Note } from './Note.js';

        class MyNexus extends ProofClientNexusBase {
          tell() {}
        }

        const note = new Note({ id: 7, text: 'hi' });
        const writer = new rt.MsgPackWriter();
        NoteFormatter.write(writer, note);
        const bytes = writer.toUint8Array().slice();
        const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
        const back = NoteFormatter.read(new rt.MsgPackReader(bytes));
        const client = createProofClient({ webSocket: { url: 'ws://localhost:1' } }, new MyNexus());
        const version = JSON.parse((await import('node:fs')).readFileSync(new URL('./nexnet-runtime/package.json', import.meta.url), 'utf8')).version;
        console.log('version=' + version + ' hex=' + hex + ' equal=' + note.equals(back) + ' client=' + (client instanceof rt.NexusClient)
          + ' node=' + (client instanceof NodeNexusClient) + ' expected=' + __EXPECT_NODE__);
        """;

    // ------------------------------------------------------------------ plumbing

    private static string Repo { get; } = RepoRoot();

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    /// <summary>Both targets configured (Java package com.acme), plus <c>NexNetTarget_&lt;key&gt;</c> options.</summary>
    private static Dictionary<string, string> Options(params (string Key, string Value)[] extra)
    {
        var options = BundleTestRunner.Configured();
        foreach (var (key, value) in extra)
            options["build_property.NexNetTarget_" + key] = value;
        return options;
    }

    private static IReadOnlyList<GeneratedFile> Bundle(Dictionary<string, string> options, string? source = null)
    {
        var run = BundleTestRunner.Run(options, source ?? EmittedRuntimeFixture.Source);
        if (run.Bundle == null)
            throw new InvalidOperationException("No bundle:\n" + string.Join("\n", run.Diagnostics.Select(d => d.ToString())));
        return TargetBundle.Read(run.Bundle);
    }

    private static string FileOf(IEnumerable<GeneratedFile> files, string path)
        => files.FirstOrDefault(f => f.Path == path)?.Content ?? throw new InvalidOperationException("No file " + path);

    private static string Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    private static async Task<T> Locked<T>(Func<Task<T>> action)
    {
        await ToolLock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            ToolLock.Release();
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(string prefix)
        {
            Root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

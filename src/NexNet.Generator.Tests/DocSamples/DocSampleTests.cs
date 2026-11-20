using System.Text;
using System.Text.Json;
using NexNet.Generator.Bundle;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.JavaScript;
using NexNet.Generator.Tests.Bundle;
using NexNet.Generator.Tests.Targets.Java;
using NexNet.Generator.Tests.Targets.JavaScript;

namespace NexNet.Generator.Tests.DocSamples;

/// <summary>
/// The code samples of docs/articles/java-client.md and javascript-client.md compile against what the generator emits
/// for the guides' example API (H25). The C# block of each guide is run through the generator (both targets, emitted
/// runtime); every ```java block is compiled with <c>javac --release 11 -Xlint:all -Werror</c> and every ```js block is
/// type-checked by <c>tsc</c> (strict, <c>checkJs</c>) with the runtime linked as a file dependency. SDK-gated (D27):
/// skipped without <c>NEXNET_REQUIRE_SDKS=1</c> or <c>NEXNET_TEST_LANGS</c>; CI runs them (clients.yml, doc-samples).
/// </summary>
public partial class DocSampleTests
{
    private const string JavaPage = "docs/articles/java-client.md";
    private const string JsPage = "docs/articles/javascript-client.md";
    private const string JavaPackage = "com.example.chat";
    private const string JavaRuntimeRoot = "java/com/dtronix/nexnet/";

    private static readonly SemaphoreSlim ToolLock = new(2, 2);

    // ------------------------------------------------------------------ extraction (no SDK)

    [Test]
    public async Task GuidesShareTheExampleApi()
    {
        var java = DocSnippets.Blocks(JavaPage, Page(JavaPage), "csharp");
        var js = DocSnippets.Blocks(JsPage, Page(JsPage), "csharp");
        await Assert.That(java.Count).IsEqualTo(1);
        await Assert.That(js.Count).IsEqualTo(1);
        await Assert.That(js[0].Code).IsEqualTo(java[0].Code);
    }

    [Test]
    public async Task ExampleApiGeneratesBothClients()
    {
        var files = Generate();
        await Assert.That(files.Keys).Contains("java/com/example/chat/ChatServerProxy.java");
        await Assert.That(files.Keys).Contains("java/com/example/chat/formatters/Formatters.java");
        await Assert.That(files.Keys).Contains("javascript/ChatServerNexus.js");
        await Assert.That(files.Keys).Contains("javascript/" + JavaScriptTarget.RuntimeDirectory + "/index.js");
    }

    [Test]
    public async Task EverySampleIsExtracted()
    {
        var java = DocSnippets.Blocks(JavaPage, Page(JavaPage), "java");
        var js = DocSnippets.Blocks(JsPage, Page(JsPage), "js");
        await Assert.That(java.Count).IsGreaterThanOrEqualTo(9);
        await Assert.That(js.Count).IsGreaterThanOrEqualTo(11);

        // Every block lands in the file: nothing is dropped by the splitting.
        var javaFile = DocSnippets.JavaFile(java, JavaPackage, new[] { "com.dtronix.nexnet" });
        var jsFile = DocSnippets.JsFile(js, Array.Empty<string>());
        foreach (var line in java.SelectMany(s => s.Code.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("import ", StringComparison.Ordinal)))
            await Assert.That(javaFile).Contains(line);
        foreach (var line in js.SelectMany(s => s.Code.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("import ", StringComparison.Ordinal)))
            await Assert.That(jsFile).Contains(line);
    }

    // ------------------------------------------------------------------ compile (SDK-gated)

    [Test]
    public async Task JavaSamplesCompile()
    {
        var (javac, _) = JavaSdk.Tools();
        using var dir = new TempDir("nexnet-h25-java-");
        var files = Generate();
        var source = Path.Combine(dir.Root, "src");
        var classes = Path.Combine(dir.Root, "classes");
        var sources = new List<string>();
        foreach (var (path, content) in files.Where(f => f.Key.StartsWith("java/", StringComparison.Ordinal) && f.Key.EndsWith(".java", StringComparison.Ordinal)))
            sources.Add(Write(source, path.Substring("java/".Length), content));

        var runtimePackages = files.Keys
            .Where(p => p.StartsWith(JavaRuntimeRoot, StringComparison.Ordinal) && p.EndsWith(".java", StringComparison.Ordinal))
            .Select(p => p.Substring("java/".Length, p.LastIndexOf('/') - "java/".Length).Replace('/', '.'))
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        var sample = DocSnippets.JavaFile(DocSnippets.Blocks(JavaPage, Page(JavaPage), "java"), JavaPackage, runtimePackages);
        sources.Add(Write(source, "docsamples/DocSamples.java", sample));
        Directory.CreateDirectory(classes);

        var args = new List<string> { "--release", "11", "-encoding", "UTF-8", "-Xlint:all", "-Werror", "-implicit:none", "-d", classes };
        args.AddRange(sources);
        var argFile = Path.Combine(dir.Root, "javac.args");
        await File.WriteAllLinesAsync(argFile, args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));

        var compiled = await Locked(() => JavaSdk.RunProcess(javac, new[] { "@" + argFile }, dir.Root, TimeSpan.FromMinutes(5)));
        await Assert.That(compiled.Success).IsTrue().Because("javac failed on the samples of " + JavaPage + ":\n" + compiled.Output + Listing(sample));
        await Assert.That(compiled.Output.Trim()).IsEmpty().Because("javac printed:\n" + compiled.Output + Listing(sample));
    }

    [Test]
    public async Task JavaScriptSamplesTypeCheck()
    {
        var sdk = await NodeSdk.Get();
        using var dir = new TempDir("nexnet-h25-js-");
        foreach (var (path, content) in Generate().Where(f => f.Key.StartsWith("javascript/", StringComparison.Ordinal)))
            Write(dir.Root, "src/generated/" + path.Substring("javascript/".Length), content);

        // RuntimeImport=package with "@dtronix/nexnet": "file:src/generated/nexnet-runtime", as the guide sets it up.
        Write(dir.Root, "package.json", "{ \"private\": true, \"type\": \"module\" }\n");
        await NodeSdk.LinkRuntime(dir.Root, Path.Combine(dir.Root, "src", "generated", JavaScriptTarget.RuntimeDirectory));
        try
        {
            var runtime = Path.Combine(dir.Root, "src", "generated", JavaScriptTarget.RuntimeDirectory);
            var exports = DocSnippets.JsExports(File.ReadAllText(Path.Combine(runtime, "index.d.ts")));
            var sample = DocSnippets.JsFile(DocSnippets.Blocks(JsPage, Page(JsPage), "js"), exports);
            Write(dir.Root, "src/samples.js", sample);
            Write(dir.Root, "globals.d.ts", JsGlobals);
            var config = new
            {
                compilerOptions = new Dictionary<string, object>
                {
                    ["target"] = "ES2022",
                    ["module"] = "NodeNext",
                    ["moduleResolution"] = "NodeNext",
                    ["lib"] = new[] { "ES2023", "DOM", "DOM.Iterable", "DOM.AsyncIterable", "ESNext.Disposable" },
                    ["types"] = new[] { "node" },
                    ["typeRoots"] = new[] { Path.Combine(NodeSdk.JsRoot, "node_modules", "@types").Replace('\\', '/') },
                    ["strict"] = true,
                    // Plain JavaScript: an unannotated parameter is any, as in any JavaScript project.
                    ["noImplicitAny"] = false,
                    ["noEmit"] = true,
                    ["allowJs"] = true,
                    ["checkJs"] = true,
                    ["skipLibCheck"] = false,
                },
                files = new[] { "globals.d.ts", "src/samples.js" },
            };
            Write(dir.Root, "tsconfig.json", JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            var result = await Locked(() => sdk.TypeCheck(Path.Combine(dir.Root, "tsconfig.json")));
            var lines = sample.Split('\n');
            var errors = result.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith(' ')).ToList();
            var unexpected = new List<string>();
            var seen = new HashSet<KnownJsError>();
            foreach (var error in errors)
            {
                var match = TscError().Match(error);
                var line = match.Success && int.TryParse(match.Groups[2].Value, out var n) && n <= lines.Length ? lines[n - 1] : "";
                var known = match.Success && match.Groups[1].Value == "src/samples.js"
                    ? KnownJsErrors.FirstOrDefault(k => k.Code == match.Groups[3].Value && line.Contains(k.Line, StringComparison.Ordinal))
                    : null;
                if (known == null)
                    unexpected.Add(error);
                else
                    seen.Add(known);
            }

            await Assert.That(unexpected).IsEmpty().Because("tsc failed on the samples of " + JsPage + " (all output below):\n" + result.Output + Listing(sample));
            await Assert.That(result.Success || errors.Count > 0).IsTrue().Because("tsc failed without errors:\n" + result.Output);
            var stale = KnownJsErrors.Where(k => !seen.Contains(k)).Select(k => k.Code + " on '" + k.Line + "' (" + k.Reason + ")");
            await Assert.That(stale).IsEmpty().Because("these known errors are gone: remove them from KnownJsErrors");
        }
        finally
        {
            NodeSdk.UnlinkRuntime(dir.Root);
        }
    }

    /// <summary>A tsc error the samples are known to hit: its code, a text of the sample line it is on, and why.</summary>
    private sealed record KnownJsError(string Code, string Line, string Reason);

    /// <summary>
    /// Errors that come from gaps in the runtime's declarations, not from the guide. Each one fails the test once it is
    /// fixed, so the entry goes with the fix.
    /// </summary>
    private static readonly KnownJsError[] KnownJsErrors = Array.Empty<KnownJsError>();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(.+?)\((\d+),\d+\): error (TS\d+):")]
    private static partial System.Text.RegularExpressions.Regex TscError();

    /// <summary>The names the JavaScript samples use without declaring them.</summary>
    private const string JsGlobals = """
        import type { ClientConfig, NexusClient } from '@dtronix/nexnet';
        import type { ChatClientNexusBase, ChatServerProxy } from './src/generated/ChatServerNexus.js';

        declare global {
          const sessionToken: string;
          const jwt: string;
          const fileBytes: Uint8Array;
          const url: string;
          const config: ClientConfig;
          const client: NexusClient<ChatClientNexusBase, ChatServerProxy>;
          function fetchToken(options: { signal: AbortSignal }): Promise<string>;
          function render(items: unknown[]): void;
        }

        export {};
        """;

    // ------------------------------------------------------------------ plumbing

    private static string Repo { get; } = RepoRoot();

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    private static string Page(string page) => File.ReadAllText(Path.Combine(Repo, page));

    /// <summary>The generator's files for the guides' example API: Java (package com.example.chat) and JavaScript, each with its runtime.</summary>
    private static Dictionary<string, string> Generate()
    {
        var api = DocSnippets.Blocks(JavaPage, Page(JavaPage), "csharp").Single().Code;
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using NexNet;
            using NexNet.Collections;
            using NexNet.Collections.Lists;
            using NexNet.Pipes;
            using NexNet.Serialization;

            namespace DocSamples;

            """ + api;
        var options = BundleTestRunner.Configured();
        options["build_property.NexNetTarget_java_Package"] = JavaPackage;
        var run = BundleTestRunner.Run(options, source);
        if (run.Bundle == null || run.Diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            throw new InvalidOperationException("The guides' example API does not generate:\n" + string.Join("\n", run.Diagnostics.Select(d => d.ToString())));
        return TargetBundle.Read(run.Bundle).ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
    }

    private static string Listing(string text)
    {
        var listing = new StringBuilder("\nThe compiled file:\n");
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            listing.Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").Append(lines[i]).Append('\n');
        return listing.ToString();
    }

    private static string Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
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

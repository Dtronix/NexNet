using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using NexNet.Generator.Bundle;
using NexNet.Generator.Targets;

namespace NexNet.Generator.Tests.Bundle;

/// <summary>
/// Real `dotnet build`s of a copy of src/NexNet.Generator.IntegrationSample (G04): the generator's bundle, the MSBuild
/// unpack and clean targets, incrementality and determinism. Each test works in its own temporary copy, referencing
/// the NexNet.dll and NexNet.Generator.dll this test assembly was built with.
/// </summary>
[NotInParallel("NexNetSampleBuild")]
public class BundleBuildTests
{
    private const string Targets = ", Targets = NexusTargets.Java | NexusTargets.JavaScript";

    [Test]
    public async Task Build_UnpacksIncrementallyDeterministicallyAndCleans()
    {
        using var sample = SampleProject.Create();

        // Files appear: every file of the bundle is on disk with its exact bytes.
        await sample.Build();
        var bundle = sample.ReadBundle();
        await Assert.That(bundle.Select(f => f.Path).ToArray()).Contains(TargetBundle.IrFileName);
        var files = bundle.Select(f => (Path: sample.Destination(f.Path), Bytes: Encoding.UTF8.GetBytes(f.Content))).ToArray();
        foreach (var (path, bytes) in files)
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(bytes)).IsTrue().Because(path + " matches the bundle");
        await Assert.That(File.ReadAllText(sample.IrPath)).Contains("\"name\": \"SampleServerNexus\"");
        await Assert.That(File.Exists(sample.ManifestPath)).IsTrue();

        // Incremental: a recompile that leaves the output unchanged rewrites nothing.
        var stamps = files.ToDictionary(f => f.Path, f => File.GetLastWriteTimeUtc(f.Path));
        var bundleStamp = File.GetLastWriteTimeUtc(sample.BundlePath);
        await Task.Delay(1100);
        sample.EditSource(s => s + "\n// an edit that changes no generated output\n");
        await sample.Build();
        await Assert.That(File.GetLastWriteTimeUtc(sample.BundlePath)).IsNotEqualTo(bundleStamp).Because("the edit recompiled");
        foreach (var (path, _) in files)
            await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(stamps[path]).Because(path + " is untouched");

        // Clean removes every unpacked file and the manifest.
        await sample.Run("clean");
        foreach (var (path, _) in files)
            await Assert.That(File.Exists(path)).IsFalse().Because(path + " is cleaned");
        await Assert.That(File.Exists(sample.ManifestPath)).IsFalse();

        // Determinism: the rebuild writes identical bytes.
        await sample.Build();
        await Assert.That(sample.ReadBundle().Select(f => f.Path).ToArray()).IsEquivalentTo(bundle.Select(f => f.Path).ToArray());
        foreach (var (path, bytes) in files)
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(bytes)).IsTrue().Because(path + " is byte-identical");

        // Removing the export deletes its files.
        sample.EditSource(s => s.Replace(Targets, ""));
        await sample.Build();
        await Assert.That(sample.ReadBundle()).IsEmpty();
        foreach (var (path, _) in files)
            await Assert.That(File.Exists(path)).IsFalse().Because(path + " is no longer generated");
    }

    [Test]
    public async Task Unpack_WritesChangedFilesOnlyDeletesStaleOnesAndRejectsBadBundles()
    {
        using var sample = SampleProject.Create();
        var javaDir = Path.Combine(sample.Directory, "generated", "java");
        var jsDir = Path.Combine(sample.Directory, "generated", "js");
        var a = Path.Combine(javaDir, "com", "acme", "A.java");
        var b = Path.Combine(javaDir, "com", "acme", "sub", "B.java");
        var index = Path.Combine(jsDir, "index.js");

        await sample.Unpack(
            new GeneratedFile("java/com/acme/A.java", "class A {}\n"),
            new GeneratedFile("java/com/acme/sub/B.java", "class B {}\n"),
            new GeneratedFile("javascript/index.js", "export {};\n"),
            new GeneratedFile(TargetBundle.IrFileName, "{}\n"));
        await Assert.That(File.ReadAllText(a)).IsEqualTo("class A {}\n");
        await Assert.That(File.ReadAllText(b)).IsEqualTo("class B {}\n");
        await Assert.That(File.ReadAllText(index)).IsEqualTo("export {};\n");
        await Assert.That(File.ReadAllText(sample.IrPath)).IsEqualTo("{}\n");

        var aStamp = File.GetLastWriteTimeUtc(a);
        await Task.Delay(1100);
        await sample.Unpack(
            new GeneratedFile("java/com/acme/A.java", "class A {}\n"),
            new GeneratedFile("javascript/index.js", "export const x = 1;\n"),
            new GeneratedFile(TargetBundle.IrFileName, "{}\n"));
        await Assert.That(File.GetLastWriteTimeUtc(a)).IsEqualTo(aStamp).Because("an unchanged file is not rewritten");
        await Assert.That(File.Exists(b)).IsFalse().Because("a file no longer emitted is deleted");
        await Assert.That(System.IO.Directory.Exists(Path.GetDirectoryName(b))).IsFalse().Because("an emptied directory is removed");
        await Assert.That(File.ReadAllText(index)).IsEqualTo("export const x = 1;\n");
        await Assert.That(System.IO.Directory.EnumerateFiles(sample.Directory, "*.tmp", SearchOption.AllDirectories).Any()).IsFalse();

        // A corrupt bundle (hash mismatch) fails before anything is written.
        static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        var tampered = TargetBundle.Write(new[] { new GeneratedFile("java/com/acme/A.java", "class A2 {}\n") })
            .Replace(Base64("class A2 {}\n"), Base64("class A9 {}\n"));
        var corrupt = await sample.UnpackText(tampered, expectSuccess: false);
        await Assert.That(corrupt).Contains("NEXNETB002");
        await Assert.That(File.ReadAllText(a)).IsEqualTo("class A {}\n");

        // Files for a target without an OutputDir, and paths escaping it, are rejected.
        var unknown = await sample.UnpackText(TargetBundle.Write(new[] { new GeneratedFile("rust/lib.rs", "") }), expectSuccess: false);
        await Assert.That(unknown).Contains("NEXNETB003");

        var escaping = TargetBundle.Write(new[] { new GeneratedFile("java/x/A.java", "") }).Replace("java/x/A.java", "java/../A.java");
        await Assert.That(await sample.UnpackText(escaping, expectSuccess: false)).Contains("NEXNETB002");

        // Clean deletes everything of the last manifest.
        await sample.Run("msbuild", "-t:NexNetCleanTargets");
        await Assert.That(File.Exists(a) || File.Exists(index) || File.Exists(sample.IrPath)).IsFalse();
        await Assert.That(File.Exists(sample.ManifestPath)).IsFalse();
    }

    [Test]
    public async Task ProjectFileImport_StampLandsInIntermediateOutputPath()
    {
        // The sample imports the props and targets in its project file, as NexNet.TestNexus does, where
        // IntermediateOutputPath is still empty (G07).
        using var sample = SampleProject.Create();
        await sample.Build();

        var stamp = Path.Combine(sample.ObjConfigDir, "nexnet-targets.stamp");
        await Assert.That(File.Exists(stamp)).IsTrue().Because("the unpack stamp is in obj/<Configuration>/<TargetFramework>");
        await Assert.That(System.IO.Directory.GetFiles(sample.Directory, "*.stamp")).IsEmpty().Because("nothing lands in the project folder");

        // The stamp keeps an up-to-date build from running the unpack again.
        var written = File.GetLastWriteTimeUtc(stamp);
        await Task.Delay(1100);
        await sample.Build();
        await Assert.That(File.GetLastWriteTimeUtc(stamp)).IsEqualTo(written);
    }

    [Test]
    public async Task NoTargetConfigured_BuildIsUnchanged()
    {
        using var sample = SampleProject.Create();
        sample.EditSource(s => s.Replace(Targets, ""));

        await sample.Build("-p:NexNetJavaOutputDir=", "-p:NexNetJavaScriptOutputDir=");

        await Assert.That(File.Exists(sample.BundlePath)).IsFalse();
        await Assert.That(System.IO.Directory.Exists(Path.Combine(sample.ObjConfigDir, "generated"))).IsFalse().Because("EmitCompilerGeneratedFiles stays off");
        await Assert.That(File.Exists(sample.ManifestPath)).IsFalse();
        var editorConfig = System.IO.Directory.GetFiles(sample.ObjConfigDir, "*.GeneratedMSBuildEditorConfig.editorconfig").Single();
        await Assert.That(File.ReadAllText(editorConfig)).DoesNotContain("NexNetTarget");
    }

    /// <summary>A temporary copy of the integration sample.</summary>
    private sealed class SampleProject : IDisposable
    {
        private const string ProjectName = "NexNet.Generator.IntegrationSample";
        private int _bundles;

        private SampleProject(string directory) => Directory = directory;

        public string Directory { get; }

        private string ProjectFile => Path.Combine(Directory, ProjectName + ".csproj");

        private string SourceFile => Path.Combine(Directory, "SampleNexus.cs");

        public string ObjConfigDir => Path.Combine(Directory, "obj", "Debug", "net10.0");

        public string BundlePath => Path.Combine(ObjConfigDir, "generated", "NexNet.Generator", "NexNet.Generator.NexusGenerator", TargetBundle.HintName);

        public string IrPath => Path.Combine(Directory, "obj", TargetBundle.IrFileName);

        public string ManifestPath => Path.Combine(Directory, "obj", "nexnet-targets.manifest");

        public static SampleProject Create()
        {
            var source = Path.Combine(SrcDirectory(), ProjectName);
            var directory = Path.Combine(Path.GetTempPath(), "nexnet-g04-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            System.IO.Directory.CreateDirectory(directory);
            foreach (var file in new[] { ProjectName + ".csproj", "SampleNexus.cs" })
                File.Copy(Path.Combine(source, file), Path.Combine(directory, file));

            // Stop MSBuild from importing Directory.Build.* files above the temporary directory.
            File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />\n");
            File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), "<Project />\n");
            return new SampleProject(directory);
        }

        /// <summary>Where the unpack target writes a bundle path, per the sample's configuration.</summary>
        public string Destination(string bundlePath)
        {
            if (bundlePath == TargetBundle.IrFileName)
                return IrPath;
            var slash = bundlePath.IndexOf('/');
            var root = bundlePath.Substring(0, slash) switch
            {
                "java" => Path.Combine(Directory, "generated", "java"),
                "javascript" => Path.Combine(Directory, "generated", "js"),
                var other => throw new InvalidOperationException("Unexpected target " + other),
            };
            return Path.Combine(root, bundlePath.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar));
        }

        public IReadOnlyList<GeneratedFile> ReadBundle() => TargetBundle.Read(File.ReadAllText(BundlePath));

        public void EditSource(Func<string, string> edit) => File.WriteAllText(SourceFile, edit(File.ReadAllText(SourceFile)));

        public Task<string> Build(params string[] extra) => Run(new[] { "build" }.Concat(extra).ToArray());

        public Task Unpack(params GeneratedFile[] files) => UnpackText(TargetBundle.Write(files), expectSuccess: true);

        /// <summary>Runs only the unpack target on a hand-written bundle.</summary>
        public Task<string> UnpackText(string bundle, bool expectSuccess)
        {
            var path = Path.Combine(Directory, "bundle" + (++_bundles) + ".g.cs");
            File.WriteAllText(path, bundle);
            return RunCore(expectSuccess, "msbuild", "-restore", "-t:NexNetUnpackTargets", "-p:NexNetTargetBundleFile=" + path);
        }

        public Task<string> Run(params string[] args) => RunCore(true, args);

        private async Task<string> RunCore(bool expectSuccess, params string[] args)
        {
            var info = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            info.ArgumentList.Add(ProjectFile);
            info.ArgumentList.Add("-nologo");
            info.ArgumentList.Add("-nodeReuse:false");
            info.ArgumentList.Add("-p:UseSharedCompilation=false");
            info.ArgumentList.Add("-p:NexNetSrcDir=" + SrcDirectory() + Path.DirectorySeparatorChar);
            info.ArgumentList.Add("-p:NexNetAssemblyDir=" + AppContext.BaseDirectory);

            // The test host may run under an MSBuild-derived environment; the child build resolves its own.
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToArray())
                info.Environment.Remove(key);
            info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            info.Environment["DOTNET_NOLOGO"] = "1";

            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + await stderr;

            if (expectSuccess && process.ExitCode != 0)
                throw new InvalidOperationException("dotnet " + string.Join(" ", args) + " failed (" + process.ExitCode + "):\n" + output);
            if (!expectSuccess && process.ExitCode == 0)
                throw new InvalidOperationException("dotnet " + string.Join(" ", args) + " succeeded unexpectedly:\n" + output);
            return output;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string SrcDirectory([CallerFilePath] string path = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
    }
}
